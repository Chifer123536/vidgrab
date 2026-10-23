using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace VidGrab
{
    static class Host
    {
        const string Version = "1.0.0";
        const int MaxIn = 64 * 1024 * 1024;

        static readonly object writeLock = new object();
        static readonly object jobsLock = new object();
        static readonly object updateLock = new object();
        static readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
        static readonly Regex idRx = new Regex("^[A-Za-z0-9_-]{1,64}$");
        static readonly Regex proxyRx = new Regex("^(https?|socks4a?|socks5h?)://[^\\s\"]+$", RegexOptions.IgnoreCase);
        static readonly Regex authRx = new Regex("sign in|log ?in|cookies|age[- ]restrict|confirm your age|inappropriate|not a bot|private|members|premium|registered|authenticat|account", RegexOptions.IgnoreCase);
        static readonly Regex finalRx = new Regex("Unsupported URL|not a valid URL|Video unavailable|has been removed|getaddrinfo|timed out|Connection|Unable to download webpage|HTTP Error 404|PLAYLIST|LIVE", RegexOptions.IgnoreCase);
        static readonly string[] mediaExt = { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi", ".flv", ".ts", ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".oga", ".wav", ".flac" };

        static Stream output;
        static string root, binDir, tmpDir, ytdlp, deno, stampFile;
        static int running;

        static int Main(string[] args)
        {
            root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            binDir = Path.Combine(root, "bin");
            tmpDir = Path.Combine(root, "tmp");
            ytdlp = Path.Combine(binDir, "yt-dlp.exe");
            deno = Path.Combine(binDir, "deno.exe");
            stampFile = Path.Combine(binDir, "ytdlp.stamp");

            Native.DisableStdInherit();
            CleanTmp();

            Stream input = Console.OpenStandardInput();
            output = Console.OpenStandardOutput();
            var json = NewJson();
            try
            {
                while (true)
                {
                    byte[] body = ReadFrame(input);
                    if (body == null) break;
                    Dictionary<string, object> msg;
                    try { msg = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body)); }
                    catch (Exception) { continue; }
                    if (msg == null) continue;
                    try { Handle(msg); }
                    catch (Exception e) { Send(Msg("t", "error", "id", Str(msg, "id"), "error", e.Message)); }
                }
            }
            finally
            {
                Shutdown();
            }
            return 0;
        }

        static void Handle(Dictionary<string, object> m)
        {
            string t = Str(m, "t");
            string id = Str(m, "id");
            if (id != null && !idRx.IsMatch(id)) return;

            switch (t)
            {
                case "hello":
                    Send(Msg("t", "hello", "id", id, "version", Version, "ytdlp", StampVersion(),
                        "ext", ExtVersion(), "defaultDir", DownloadsDir(), "ready", File.Exists(ytdlp)));
                    break;
                case "probe":
                    Spawn(() => Probe(id, m));
                    break;
                case "dl":
                    StartDownload(id, m);
                    break;
                case "cancel":
                    Cancel(id);
                    break;
                case "pick":
                    SpawnSta(() => Send(Msg("t", "pick", "id", id, "dir", FolderPicker.Pick(Str(m, "dir")))));
                    break;
                case "open":
                    Open(Str(m, "path"), Bool(m, "reveal"));
                    break;
                case "update":
                    Spawn(() => ManualUpdate(id));
                    break;
            }
        }

        static void Probe(string id, Dictionary<string, object> m)
        {
            string url = Str(m, "url");
            if (id == null) return;
            if (!ValidUrl(url))
            {
                Send(Msg("t", "probe", "id", id, "ok", false, "error", "Unsupported URL"));
                return;
            }

            var job = new Job { Id = id };
            string proxy = Str(m, "proxy");
            RunResult r = Attempts(job, m, ck =>
            {
                var a = BaseArgs(ck, proxy);
                a.AddRange(new[] { "-J", "--no-playlist", "--flat-playlist", "--", url });
                return a;
            }, null, true, 180000, ParseProbe);

            if (r.Exit != 0 || r.Info == null)
                Send(Msg("t", "probe", "id", id, "ok", false, "error", r.Error ?? "yt-dlp: пустой ответ"));
            else
                Send(Msg("t", "probe", "id", id, "ok", true, "info", r.Info));
        }

        static void ParseProbe(RunResult r)
        {
            if (r.Exit != 0) return;
            string text = r.Out.ToString().Trim();
            int start = text.IndexOf('{');
            if (start < 0) { r.Exit = 1; r.Error = "yt-dlp: пустой ответ"; return; }

            Dictionary<string, object> d;
            try { d = NewJson().Deserialize<Dictionary<string, object>>(text.Substring(start)); }
            catch (Exception e) { r.Exit = 1; r.Error = "yt-dlp: " + e.Message; return; }

            if (Str(d, "_type") == "playlist") { r.Exit = 1; r.Error = "PLAYLIST"; return; }
            if (Bool(d, "is_live")) { r.Exit = 1; r.Error = "LIVE"; return; }

            var byLabel = new Dictionary<int, int[]>();
            double abr = 0;
            var formats = d.ContainsKey("formats") ? d["formats"] as IEnumerable : null;
            if (formats != null)
            {
                foreach (object o in formats)
                {
                    var f = o as Dictionary<string, object>;
                    if (f == null || Str(f, "ext") == "mhtml") continue;
                    string vcodec = Str(f, "vcodec");
                    string acodec = Str(f, "acodec");
                    double h = Num(f, "height"), w = Num(f, "width");

                    if (vcodec == "none")
                    {
                        if (acodec != "none") abr = Math.Max(abr, Num(f, "abr"));
                        continue;
                    }
                    if (h <= 0) continue;

                    int res = (int)(w > 0 ? Math.Min(w, h) : h);
                    int label = Ladder(w, h);
                    int fps = (int)Math.Round(Num(f, "fps"));
                    int[] cur;
                    if (!byLabel.TryGetValue(label, out cur)) byLabel[label] = new[] { res, fps };
                    else { cur[0] = Math.Max(cur[0], res); cur[1] = Math.Max(cur[1], fps); }
                }
            }

            var labels = new List<int>(byLabel.Keys);
            labels.Sort((a, b) => b.CompareTo(a));
            var q = new List<object>();
            foreach (int l in labels)
                q.Add(Msg("label", l, "res", byLabel[l][0], "fps", byLabel[l][1]));

            r.Info = Msg(
                "title", Str(d, "title"),
                "thumb", Str(d, "thumbnail"),
                "duration", Num(d, "duration"),
                "uploader", Str(d, "uploader") ?? Str(d, "channel"),
                "site", Str(d, "extractor_key"),
                "q", q,
                "abr", Math.Round(abr));
        }

        static int Ladder(double w, double h)
        {
            double shortSide = w > 0 ? Math.Min(w, h) : h;
            double longSide = w > 0 ? Math.Max(w, h) : 0;
            return Math.Max(Step(shortSide, 1), Step(longSide, 16.0 / 9));
        }

        static int Step(double side, double ratio)
        {
            if (side <= 0) return 0;
            int[] steps = { 4320, 2160, 1440, 1080, 720, 480, 360, 240, 144 };
            foreach (int st in steps)
            {
                if (side >= st * ratio * 0.9) return st;
            }
            return ratio == 1 ? (int)side : 0;
        }

        static void StartDownload(string id, Dictionary<string, object> m)
        {
            if (id == null) return;
            var job = new Job { Id = id };
            lock (jobsLock)
            {
                if (jobs.ContainsKey(id)) return;
                jobs[id] = job;
            }
            Spawn(() =>
            {
                try { Download(job, m); }
                catch (Exception e) { Send(Msg("t", "fail", "id", id, "error", e.Message)); }
                finally { lock (jobsLock) jobs.Remove(id); }
            });
        }

        static void Download(Job job, Dictionary<string, object> m)
        {
            string url = Str(m, "url");
            if (!ValidUrl(url)) { Send(Msg("t", "fail", "id", job.Id, "error", "Unsupported URL")); return; }

            string dir = Str(m, "dir");
            if (string.IsNullOrEmpty(dir) || !SafeRooted(dir)) dir = DownloadsDir();
            string tmpRoot = Path.Combine(dir, ".vidgrab-tmp");
            job.TempDir = Path.Combine(tmpRoot, job.Id);
            try
            {
                Directory.CreateDirectory(job.TempDir);
                File.SetAttributes(tmpRoot, File.GetAttributes(tmpRoot) | FileAttributes.Hidden);
            }
            catch (Exception e)
            {
                Send(Msg("t", "fail", "id", job.Id, "error", "Нет доступа к папке: " + e.Message));
                return;
            }

            bool audio = Str(m, "mode") == "audio";
            string q = Str(m, "q") ?? "";
            string label = Str(m, "label");
            string proxy = Str(m, "proxy");
            var prog = new Progress(job.Id, audio);

            RunResult r = Attempts(job, m, ck => DownloadArgs(url, audio, q, label, dir, job.TempDir, ck, proxy),
                prog.Line, false, Timeout.Infinite, null);

            DeleteDir(job.TempDir);
            DeleteIfEmpty(tmpRoot);

            if (job.Cancelled) Send(Msg("t", "cancelled", "id", job.Id));
            else if (r.Exit == 0) Send(Msg("t", "done", "id", job.Id, "file", prog.File));
            else Send(Msg("t", "fail", "id", job.Id, "error", r.Error));
        }

        static List<string> DownloadArgs(string url, bool audio, string q, string label, string dir, string temp, string cookies, string proxy)
        {
            var a = BaseArgs(cookies, proxy);
            a.AddRange(new[]
            {
                "--no-playlist", "--no-simulate", "--newline", "--progress", "--progress-delta", "0.3",
                "--no-mtime", "--concurrent-fragments", "4", "--embed-metadata",
                "-P", dir, "-P", "temp:" + temp,
                "--progress-template", "download:[vgp]%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s|%(info.acodec)s|%(progress.fragment_index)s|%(progress.fragment_count)s",
                "--progress-template", "postprocess:[vgpp]%(progress.postprocessor)s|%(progress.status)s",
                "--print", "before_dl:[vgt]%(title)s",
                "--print", "after_move:[vgf]%(filepath)s"
            });

            int n;
            if (audio)
            {
                a.Add("-x");
                if (int.TryParse(q, out n) && n >= 32 && n <= 320)
                {
                    a.AddRange(new[] { "-f", "ba/b[height<=480]/b", "--audio-format", "mp3", "--audio-quality", n + "K" });
                }
                else
                {
                    a.AddRange(new[] { "-f", "ba[ext=m4a]/ba/b[height<=480]/b" });
                }
                a.AddRange(new[] { "--embed-thumbnail", "--convert-thumbnails", "jpg", "-o", "%(title).150B.%(ext)s" });
            }
            else
            {
                int.TryParse(q, out n);
                int l;
                string tag = label != null && int.TryParse(label, out l) && l > 0 ? l + "p" : "%(height)sp";
                a.AddRange(new[]
                {
                    "-f", "bv*+ba/b",
                    "-S", (n > 0 ? "res:" + n : "res") + ",fps,vcodec:h264,acodec:aac",
                    "--merge-output-format", "mp4/mkv",
                    "-o", "%(title).150B (" + tag + ").%(ext)s"
                });
            }

            a.Add("--");
            a.Add(url);
            return a;
        }

        static List<string> BaseArgs(string cookies, string proxy)
        {
            var a = new List<string> { "--ignore-config", "--encoding", "utf-8", "--ffmpeg-location", binDir };
            if (File.Exists(deno)) { a.Add("--js-runtimes"); a.Add("deno:" + deno); }
            if (cookies != null) { a.Add("--cookies"); a.Add(cookies); }
            if (!string.IsNullOrEmpty(proxy) && proxyRx.IsMatch(proxy)) { a.Add("--proxy"); a.Add(proxy); }
            return a;
        }

        static RunResult Attempts(Job job, Dictionary<string, object> m, Func<string, List<string>> build,
            Action<string> onLine, bool keepOut, int timeout, Action<RunResult> check)
        {
            string cookieText = Str(m, "cookies");
            bool haveCookies = !string.IsNullOrEmpty(cookieText);
            bool useCookies = false, updated = false;
            RunResult r = null;

            for (int i = 0; i < 3; i++)
            {
                EnsureFresh(false);
                string cookieFile = useCookies ? WriteCookies(job.Id, cookieText) : null;
                try { r = Run(job, build(cookieFile), onLine, keepOut, timeout); }
                finally { if (cookieFile != null) TryDelete(cookieFile); }
                if (check != null) check(r);

                if (job.Cancelled || r.Exit == 0) return r;
                string err = r.Error ?? "";
                if (!useCookies && haveCookies && authRx.IsMatch(err)) { useCookies = true; continue; }
                if (!updated && !finalRx.IsMatch(err) && EnsureFresh(true)) { updated = true; continue; }
                break;
            }
            return r;
        }

        static RunResult Run(Job job, List<string> args, Action<string> onLine, bool keepOut, int timeout)
        {
            var r = new RunResult();
            if (!File.Exists(ytdlp))
            {
                r.Exit = -1;
                r.Error = "Не найден yt-dlp. Запусти install.cmd ещё раз.";
                return r;
            }

            var psi = new ProcessStartInfo(ytdlp, JoinArgs(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = binDir
            };
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            var p = new Process { StartInfo = psi };
            var closed = new CountdownEvent(2);
            string lastError = null, lastLine = null;

            DataReceivedEventHandler handler = (s, e) =>
            {
                if (e.Data == null) { closed.Signal(); return; }
                string line = e.Data;
                if (line.StartsWith("[vg"))
                {
                    if (onLine != null) onLine(line);
                    return;
                }
                lock (r)
                {
                    if (line.StartsWith("ERROR:")) lastError = line.Substring(6).Trim();
                    else if (keepOut && line.StartsWith("{")) r.Out.Append(line);
                    else if (line.Trim().Length > 0 && !line.StartsWith("WARNING:")) lastLine = line.Trim();
                }
            };
            p.OutputDataReceived += handler;
            p.ErrorDataReceived += handler;

            IntPtr kill = Native.CreateKillJob();
            try
            {
                lock (updateLock)
                {
                    Interlocked.Increment(ref running);
                    try { p.Start(); }
                    catch (Exception e)
                    {
                        Interlocked.Decrement(ref running);
                        r.Exit = -1;
                        r.Error = "Не удалось запустить yt-dlp: " + e.Message;
                        return r;
                    }
                }

                try
                {
                    Native.Assign(kill, p);
                    if (job != null)
                    {
                        lock (job)
                        {
                            job.Proc = p;
                            job.KillJob = kill;
                            if (job.Cancelled) Native.Terminate(kill, p);
                        }
                    }

                    p.StandardInput.Close();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (!p.WaitForExit(timeout))
                    {
                        Native.Terminate(kill, p);
                        lastError = "yt-dlp не ответил вовремя";
                    }
                    p.WaitForExit();
                    closed.Wait(5000);
                    r.Exit = p.ExitCode;
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
            }
            finally
            {
                if (job != null) lock (job) { job.Proc = null; job.KillJob = IntPtr.Zero; }
                Native.CloseHandle(kill);
                p.Dispose();
            }

            lock (r)
            {
                r.Last = lastLine;
                if (r.Exit != 0) r.Error = lastError ?? lastLine ?? ("yt-dlp завершился с кодом " + r.Exit);
            }
            return r;
        }

        static bool EnsureFresh(bool force)
        {
            lock (updateLock)
            {
                TimeSpan age = File.Exists(stampFile) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(stampFile) : TimeSpan.MaxValue;
                if (age < TimeSpan.FromHours(force ? 1 : 20)) return false;
                if (Thread.VolatileRead(ref running) > 0) return false;

                Send(Msg("t", "busy", "text", "update"));
                try { RunUpdate(); }
                finally { Send(Msg("t", "busy", "text", null)); }
                return true;
            }
        }

        static RunResult RunUpdate()
        {
            RunResult r = Run(null, new List<string> { "--ignore-config", "--update-to", "nightly" }, null, false, 180000);
            RunResult v = Run(null, new List<string> { "--version" }, null, false, 30000);
            string ver = v.Exit == 0 ? (v.Last ?? "") : "";
            try
            {
                string old = StampVersion();
                File.WriteAllText(stampFile, ver.Length > 0 ? ver : (old ?? "?"));
            }
            catch (Exception) { }
            return r;
        }

        static void ManualUpdate(string id)
        {
            RunResult r = null;
            lock (updateLock)
            {
                if (Thread.VolatileRead(ref running) == 0)
                {
                    Send(Msg("t", "busy", "text", "update"));
                    try { r = RunUpdate(); }
                    finally { Send(Msg("t", "busy", "text", null)); }
                }
            }
            if (r == null) Send(Msg("t", "update", "id", id, "ok", false, "error", "Дождись окончания загрузок"));
            else Send(Msg("t", "update", "id", id, "ok", r.Exit == 0, "ytdlp", StampVersion(), "error", r.Exit == 0 ? null : r.Error));
        }

        static void Cancel(string id)
        {
            if (id == null) return;
            Job job;
            lock (jobsLock) jobs.TryGetValue(id, out job);
            if (job == null) return;
            lock (job)
            {
                job.Cancelled = true;
                if (job.Proc != null) Native.Terminate(job.KillJob, job.Proc);
            }
        }

        static void Shutdown()
        {
            List<Job> list;
            lock (jobsLock) list = new List<Job>(jobs.Values);
            foreach (Job j in list)
            {
                lock (j)
                {
                    j.Cancelled = true;
                    if (j.Proc != null) Native.Terminate(j.KillJob, j.Proc);
                }
            }
            if (list.Count == 0) return;
            Thread.Sleep(400);
            foreach (Job j in list)
            {
                if (j.TempDir == null) continue;
                DeleteDir(j.TempDir);
                DeleteIfEmpty(Path.GetDirectoryName(j.TempDir));
            }
        }

        static void Open(string path, bool reveal)
        {
            if (string.IsNullOrEmpty(path) || !SafeRooted(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    if (reveal) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    else if (Array.IndexOf(mediaExt, Path.GetExtension(path).ToLowerInvariant()) >= 0)
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                else if (Directory.Exists(path))
                {
                    Process.Start("explorer.exe", "\"" + path + "\"");
                }
                else if (reveal)
                {
                    string parent = Path.GetDirectoryName(path);
                    if (parent != null && Directory.Exists(parent)) Process.Start("explorer.exe", "\"" + parent + "\"");
                }
            }
            catch (Exception) { }
        }

        static string WriteCookies(string id, string text)
        {
            try
            {
                Directory.CreateDirectory(tmpDir);
                string f = Path.Combine(tmpDir, id + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".cookies.txt");
                File.WriteAllText(f, text, new UTF8Encoding(false));
                return f;
            }
            catch (Exception) { return null; }
        }

        static void CleanTmp()
        {
            try
            {
                if (!Directory.Exists(tmpDir)) return;
                foreach (string f in Directory.GetFiles(tmpDir))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(1)) TryDelete(f);
                }
            }
            catch (Exception) { }
        }

        static string StampVersion()
        {
            try { return File.Exists(stampFile) ? File.ReadAllText(stampFile).Trim() : null; }
            catch (Exception) { return null; }
        }

        static string ExtVersion()
        {
            try
            {
                string f = Path.Combine(root, "extension", "manifest.json");
                if (!File.Exists(f)) return null;
                Match mt = Regex.Match(File.ReadAllText(f), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return mt.Success ? mt.Groups[1].Value : null;
            }
            catch (Exception) { return null; }
        }

        static string DownloadsDir()
        {
            string p = Native.KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            if (string.IsNullOrEmpty(p)) p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return p;
        }

        static bool ValidUrl(string url)
        {
            Uri u;
            return url != null && url.Length < 8192 && Uri.TryCreate(url, UriKind.Absolute, out u) &&
                   (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        static bool SafeRooted(string path)
        {
            try { return path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(path) && !path.StartsWith("\\\\"); }
            catch (Exception) { return false; }
        }

        static void DeleteDir(string dir)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, true);
                    return;
                }
                catch (Exception) { Thread.Sleep(300); }
            }
        }

        static void DeleteIfEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            catch (Exception) { }
        }

        static void TryDelete(string f)
        {
            try { File.Delete(f); }
            catch (Exception) { }
        }

        static string JoinArgs(List<string> args)
        {
            var sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', slashes); sb.Append(c); }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static byte[] ReadFrame(Stream s)
        {
            byte[] len = ReadExact(s, 4);
            if (len == null) return null;
            int n = BitConverter.ToInt32(len, 0);
            if (n <= 0 || n > MaxIn) return null;
            return ReadExact(s, n);
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int got;
                try { got = s.Read(buf, off, n - off); }
                catch (IOException) { return null; }
                if (got <= 0) return null;
                off += got;
            }
            return buf;
        }

        internal static void Send(Dictionary<string, object> m)
        {
            lock (writeLock)
            {
                try
                {
                    byte[] body = Encoding.UTF8.GetBytes(NewJson().Serialize(m));
                    output.Write(BitConverter.GetBytes(body.Length), 0, 4);
                    output.Write(body, 0, body.Length);
                    output.Flush();
                }
                catch (Exception) { }
            }
        }

        internal static Dictionary<string, object> Msg(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static JavaScriptSerializer NewJson()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };
        }

        static string Str(Dictionary<string, object> m, string k)
        {
            object v;
            return m.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static bool Bool(Dictionary<string, object> m, string k)
        {
            object v;
            return m.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        static double Num(Dictionary<string, object> m, string k)
        {
            object v;
            if (!m.TryGetValue(k, out v) || v == null || v is string || v is bool) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        static void Spawn(ThreadStart fn)
        {
            new Thread(fn) { IsBackground = true }.Start();
        }

        static void SpawnSta(ThreadStart fn)
        {
            var t = new Thread(fn) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        class Job
        {
            public string Id;
            public volatile bool Cancelled;
            public Process Proc;
            public IntPtr KillJob;
            public string TempDir;
        }

        class RunResult
        {
            public int Exit;
            public string Error;
            public string Last;
            public readonly StringBuilder Out = new StringBuilder();
            public Dictionary<string, object> Info;
        }

        class Progress
        {
            readonly string id;
            readonly bool audio;
            double best;
            bool started;
            DateTime last = DateTime.MinValue;
            public string File;

            public Progress(string id, bool audio)
            {
                this.id = id;
                this.audio = audio;
            }

            public void Line(string line)
            {
                lock (this) Parse(line);
            }

            void Parse(string line)
            {
                if (line.StartsWith("[vgf]")) { File = line.Substring(5).Trim(); return; }
                if (line.StartsWith("[vgt]")) { Send(Msg("t", "title", "id", id, "title", line.Substring(5).Trim())); return; }
                if (line.StartsWith("[vgpp]"))
                {
                    string[] pp = line.Substring(6).Split('|');
                    if (pp.Length < 2 || pp[1] != "started" || !started) return;
                    best = Math.Max(best, audio ? 96 : 98);
                    Emit("pp:" + pp[0], double.NaN, double.NaN, true);
                    return;
                }
                if (!line.StartsWith("[vgp]")) return;

                string[] f = line.Substring(5).Split('|');
                if (f.Length < 10) return;
                started = true;
                double done = D(f[1]), total = D(f[2]), est = D(f[3]), speed = D(f[4]), eta = D(f[5]);
                double fi = D(f[8]), fc = D(f[9]);
                string part = f[6] == "none" ? "audio" : f[7] == "none" ? "video" : "single";

                double p;
                if (f[0] == "finished") p = 1;
                else if (total > 0) p = done / total;
                else if (est > 0) p = done / est;
                else if (fc > 0) p = fi / fc;
                else p = 0;
                p = Math.Max(0, Math.Min(1, p));

                double overall;
                if (audio) overall = p * 95;
                else if (part == "video") overall = p * 90;
                else if (part == "audio") overall = 90 + p * 8;
                else overall = p * 98;
                best = Math.Max(best, overall);

                Emit(part, speed, eta, f[0] == "finished");
            }

            void Emit(string stage, double speed, double eta, bool force)
            {
                DateTime now = DateTime.UtcNow;
                if (!force && (now - last).TotalMilliseconds < 250) return;
                last = now;
                Send(Msg("t", "prog", "id", id, "pct", Math.Round(best, 1), "stage", stage,
                    "speed", double.IsNaN(speed) ? (object)null : Math.Round(speed),
                    "eta", double.IsNaN(eta) ? (object)null : Math.Round(eta)));
            }

            static double D(string s)
            {
                double v;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
            }
        }
    }

    static class FolderPicker
    {
        public static string Pick(string current)
        {
            Form owner = null;
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                owner = new Form
                {
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Size = new Size(1, 1),
                    Location = new Point(wa.Left + wa.Width / 2, wa.Top + wa.Height / 3),
                    Opacity = 0,
                    TopMost = true
                };
                owner.Show();
                Native.ForceForeground(owner.Handle);

                var dlg = (IFileDialog)new FileOpenDialogCom();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | 0x20 | 0x40 | 0x800 | 0x8);
                dlg.SetTitle("Папка для загрузок");
                if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
                {
                    IShellItem start;
                    if (Native.SHCreateItemFromParsingName(current, IntPtr.Zero, typeof(IShellItem).GUID, out start) == 0)
                        dlg.SetFolder(start);
                }
                if (dlg.Show(owner.Handle) != 0) return null;

                IShellItem item;
                dlg.GetResult(out item);
                string path;
                item.GetDisplayName(0x80058000, out path);
                return path;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("pick: " + e);
                return null;
            }
            finally
            {
                if (owner != null) { owner.Close(); owner.Dispose(); }
            }
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialogCom { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attrs, string name);

        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int size);

        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        static extern bool TerminateJobObject(IntPtr job, uint code);

        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetStdHandle(int n);

        [DllImport("kernel32.dll")]
        static extern bool SetHandleInformation(IntPtr h, uint mask, uint flags);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint a, uint b, bool attach);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        internal static void DisableStdInherit()
        {
            foreach (int n in new[] { -10, -11, -12 })
            {
                IntPtr h = GetStdHandle(n);
                if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, 1, 0);
            }
        }

        internal static IntPtr CreateKillJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return job;
            var info = new ExtendedLimit();
            info.Basic.LimitFlags = 0x2000;
            SetInformationJobObject(job, 9, ref info, Marshal.SizeOf(typeof(ExtendedLimit)));
            return job;
        }

        internal static void Assign(IntPtr job, Process p)
        {
            if (job == IntPtr.Zero) return;
            try { AssignProcessToJobObject(job, p.Handle); }
            catch (Exception) { }
        }

        internal static void Terminate(IntPtr job, Process p)
        {
            if (job != IntPtr.Zero && TerminateJobObject(job, 1)) return;
            try { if (p != null && !p.HasExited) p.Kill(); }
            catch (Exception) { }
        }

        internal static void ForceForeground(IntPtr hwnd)
        {
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attach = fg != 0 && fg != me;
            if (attach) AttachThreadInput(me, fg, true);
            SetForegroundWindow(hwnd);
            if (attach) AttachThreadInput(me, fg, false);
        }

        internal static string KnownFolder(Guid id)
        {
            IntPtr p;
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
    }
}

/*
 * ═══════════════════════════════════════
 *  VIDGRAB HOST — КАК РАБОТАЕТ
 * ═══════════════════════════════════════
 *  Native messaging хост: stdin/stdout, кадр = int32 LE длина + UTF-8 JSON.
 *  Команды: hello, probe (yt-dlp -J → сжатая сводка качеств), dl, cancel, pick (папка), open, update.
 *  Каждый yt-dlp в своём Job Object (kill-on-close): отмена/выход хоста убивает и ffmpeg/deno.
 *  Попытки: без куки → с куки при ошибке входа → после форс-обновления yt-dlp (не чаще раза в час).
 *  Автообновление yt-dlp (nightly) раз в 20 ч, только когда нет запущенных процессов.
 *  Промежуточные файлы в <папка>\.vidgrab-tmp\<id>, удаляются после загрузки/отмены.
 * ═══════════════════════════════════════
*/
