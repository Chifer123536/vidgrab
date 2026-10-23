const HOST = 'com.vidgrab.host';
const KEEP_FINISHED = 20;
const PROBE_TTL = 10 * 60 * 1000;
const IDLE_MS = 30 * 1000;

const jobs = new Map();
const pending = new Map();
const probeCache = new Map();
const probing = new Map();
const popups = new Set();

let port = null;
let hostInfo = null;
let busy = null;
let idleTimer = null;
let saveTimer = null;
let pushTimer = null;

const DEFAULTS = { dir: '', proxy: '', mode: 'video', vq: 1080, aq: '320' };

const uid = () => crypto.randomUUID().replace(/-/g, '').slice(0, 16);
const isActive = (j) => j.status === 'run';

const ready = chrome.storage.local.get('jobs').then(({ jobs: saved }) => {
  for (const j of saved || []) {
    if (isActive(j)) Object.assign(j, { status: 'fail', error: 'INTERRUPTED' });
    jobs.set(j.id, j);
  }
  badge();
});

async function getSettings() {
  const { settings } = await chrome.storage.local.get('settings');
  return { ...DEFAULTS, ...settings };
}

async function setSettings(patch) {
  const next = { ...(await getSettings()), ...patch };
  await chrome.storage.local.set({ settings: next });
  return next;
}

function connect() {
  if (port) return port;
  port = chrome.runtime.connectNative(HOST);
  port.onMessage.addListener(onHost);
  port.onDisconnect.addListener(() => {
    const err = chrome.runtime.lastError?.message || 'Host disconnected';
    port = null;
    hostInfo = null;
    busy = null;
    for (const p of pending.values()) {
      clearTimeout(p.timer);
      p.resolve({ t: 'error', error: err });
    }
    pending.clear();
    for (const j of jobs.values()) {
      if (isActive(j)) finish(j, { status: 'fail', error: err });
    }
    push();
  });
  return port;
}

function request(msg, timeout) {
  return new Promise((resolve) => {
    const id = (msg.id = msg.id || uid());
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve({ t: 'error', error: 'TIMEOUT' });
    }, timeout);
    pending.set(id, { resolve, timer });
    try {
      connect().postMessage(msg);
    } catch (e) {
      clearTimeout(timer);
      pending.delete(id);
      resolve({ t: 'error', error: String(e?.message || e) });
    }
    scheduleIdle();
  });
}

function hostFail(error) {
  const e = String(error || '');
  if (/not found/i.test(e)) return { ok: false, code: 'nohost', error: e };
  if (/forbidden/i.test(e)) return { ok: false, code: 'forbidden', error: e };
  return { ok: false, code: 'error', error: e };
}

async function hello() {
  if (hostInfo) return { ok: true };
  const r = await request({ t: 'hello' }, 10000);
  if (r.t !== 'hello') return hostFail(r.error);
  if (!r.ready) return { ok: false, code: 'nohost', error: 'yt-dlp missing' };
  hostInfo = r;
  await maybeReload(r.ext);
  return { ok: true };
}

async function maybeReload(diskVersion) {
  const mine = chrome.runtime.getManifest().version;
  if (!diskVersion || diskVersion === mine) return;
  if ([...jobs.values()].some(isActive)) return;
  const { reloadedFor } = await chrome.storage.local.get('reloadedFor');
  if (reloadedFor === diskVersion) return;
  await chrome.storage.local.set({ reloadedFor: diskVersion });
  chrome.runtime.reload();
}

function onHost(msg) {
  const job = msg.id ? jobs.get(msg.id) : null;
  switch (msg.t) {
    case 'busy':
      busy = msg.text || null;
      push();
      return;
    case 'title':
      if (job) Object.assign(job, { title: msg.title });
      break;
    case 'prog':
      if (job && isActive(job)) {
        Object.assign(job, { pct: msg.pct, stage: msg.stage, speed: msg.speed, eta: msg.eta });
      }
      break;
    case 'done':
      if (job) finish(job, { status: 'done', pct: 100, file: msg.file || null });
      break;
    case 'fail':
      if (job) finish(job, { status: 'fail', error: msg.error });
      break;
    case 'cancelled':
      if (job) {
        jobs.delete(job.id);
        save();
        badge();
      }
      break;
    default: {
      const p = pending.get(msg.id);
      if (p) {
        clearTimeout(p.timer);
        pending.delete(msg.id);
        p.resolve(msg);
      }
      return;
    }
  }
  pushSoon();
}

function finish(job, patch) {
  Object.assign(job, patch, { speed: null, eta: null });
  save();
  badge();
  notify(job);
  scheduleIdle();
}

function notify(job) {
  if (popups.size > 0 || (job.status !== 'done' && job.status !== 'fail')) return;
  if (job.error === 'INTERRUPTED') return;
  chrome.notifications.create('vg-' + job.id, {
    type: 'basic',
    iconUrl: 'icons/128.png',
    title: job.status === 'done' ? 'Скачано' : 'Не удалось скачать',
    message: job.title || job.url,
    priority: 0
  });
}

function badge() {
  const n = [...jobs.values()].filter(isActive).length;
  chrome.action.setBadgeBackgroundColor({ color: '#4F3DF5' });
  chrome.action.setBadgeText({ text: n ? String(n) : '' });
}

function save() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    const list = [...jobs.values()].sort((a, b) => b.ts - a.ts);
    const active = list.filter(isActive);
    const rest = list.filter((j) => !isActive(j)).slice(0, KEEP_FINISHED);
    const keep = new Set([...active, ...rest].map((j) => j.id));
    for (const id of jobs.keys()) if (!keep.has(id)) jobs.delete(id);
    chrome.storage.local.set({ jobs: [...active, ...rest] });
  }, 500);
}

function snapshot() {
  return {
    jobs: [...jobs.values()].sort((a, b) => b.ts - a.ts),
    busy,
    ytdlp: hostInfo?.ytdlp || null,
    defaultDir: hostInfo?.defaultDir || null
  };
}

function push() {
  clearTimeout(pushTimer);
  pushTimer = null;
  const state = snapshot();
  for (const p of popups) {
    try {
      p.postMessage({ type: 'state', state });
    } catch {}
  }
}

function pushSoon() {
  if (!pushTimer) pushTimer = setTimeout(push, 150);
}

function scheduleIdle() {
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => {
    const idle = !pending.size && !popups.size && ![...jobs.values()].some(isActive);
    if (idle && port) {
      port.disconnect();
      port = null;
      hostInfo = null;
    } else if (port) {
      scheduleIdle();
    }
  }, IDLE_MS);
}

function baseDomain(host) {
  const parts = host.split('.');
  if (parts.length <= 2) return host;
  const sld = parts[parts.length - 2];
  const tld = parts[parts.length - 1];
  const n = tld.length === 2 && /^(co|com|net|org|gov|edu|ac)$/.test(sld) ? 3 : 2;
  return parts.slice(-n).join('.');
}

async function cookiesFor(url) {
  let u;
  try {
    u = new URL(url);
  } catch {
    return '';
  }
  let list = [];
  try {
    const [a, b] = await Promise.all([
      chrome.cookies.getAll({ url }),
      chrome.cookies.getAll({ domain: baseDomain(u.hostname) })
    ]);
    list = [...a, ...b];
  } catch {
    return '';
  }
  const seen = new Set();
  const lines = ['# Netscape HTTP Cookie File'];
  const fallbackExpiry = Math.floor(Date.now() / 1000) + 86400;
  for (const c of list) {
    const key = `${c.name}\t${c.domain}\t${c.path}`;
    if (seen.has(key) || /[\t\r\n]/.test(c.name + c.value)) continue;
    seen.add(key);
    const sub = !c.hostOnly;
    const bare = c.domain.replace(/^\./, '');
    const expiry = c.session || !c.expirationDate ? fallbackExpiry : Math.floor(c.expirationDate);
    lines.push([sub ? '.' + bare : bare, sub ? 'TRUE' : 'FALSE', c.path, c.secure ? 'TRUE' : 'FALSE', expiry, c.name, c.value].join('\t'));
  }
  return lines.length > 1 ? lines.join('\n') + '\n' : '';
}

async function probe(url, fresh) {
  const cached = probeCache.get(url);
  if (!fresh && cached && Date.now() - cached.ts < PROBE_TTL) return cached.res;
  if (probing.has(url)) return probing.get(url);

  const run = (async () => {
    const h = await hello();
    if (!h.ok) return h;
    const settings = await getSettings();
    const r = await request({ t: 'probe', url, proxy: settings.proxy, cookies: await cookiesFor(url) }, 200000);
    if (r.t !== 'probe') return hostFail(r.error);
    if (!r.ok) return { ok: false, code: 'fail', error: r.error };
    const res = { ok: true, info: r.info };
    probeCache.set(url, { res, ts: Date.now() });
    if (probeCache.size > 30) probeCache.delete(probeCache.keys().next().value);
    return res;
  })();

  probing.set(url, run);
  try {
    return await run;
  } finally {
    probing.delete(url);
  }
}

async function startJob({ url, mode, q, label, title, thumb }) {
  await ready;
  const settings = await getSettings();
  const job = {
    id: uid(),
    url,
    mode: mode === 'audio' ? 'audio' : 'video',
    q: q ?? '',
    label: label ?? null,
    title: title || url,
    thumb: thumb || null,
    status: 'run',
    pct: 0,
    stage: 'start',
    ts: Date.now()
  };
  jobs.set(job.id, job);
  badge();
  push();
  save();

  const h = await hello();
  if (!h.ok) {
    finish(job, { status: 'fail', error: h.code === 'nohost' ? 'NOHOST' : h.error });
    push();
    return job.id;
  }
  try {
    connect().postMessage({
      t: 'dl',
      id: job.id,
      url,
      mode: job.mode,
      q: String(job.q),
      label: job.label != null ? String(job.label) : null,
      dir: settings.dir,
      proxy: settings.proxy,
      cookies: await cookiesFor(url)
    });
  } catch (e) {
    finish(job, { status: 'fail', error: String(e?.message || e) });
  }
  push();
  return job.id;
}

async function handle(m) {
  await ready;
  switch (m.cmd) {
    case 'state':
      return { ...snapshot(), settings: await getSettings() };
    case 'probe':
      return probe(m.url, m.fresh);
    case 'dl':
      return { id: await startJob(m) };
    case 'cancel':
      if (port) port.postMessage({ t: 'cancel', id: m.id });
      return { ok: true };
    case 'retry': {
      const j = jobs.get(m.id);
      if (!j) return { ok: false };
      jobs.delete(j.id);
      return { id: await startJob(j) };
    }
    case 'remove':
      jobs.delete(m.id);
      save();
      push();
      return { ok: true };
    case 'clear':
      for (const j of [...jobs.values()]) if (!isActive(j)) jobs.delete(j.id);
      save();
      push();
      return { ok: true };
    case 'open': {
      const h = await hello();
      if (h.ok) connect().postMessage({ t: 'open', path: m.path, reveal: !!m.reveal });
      return h;
    }
    case 'pick': {
      const h = await hello();
      if (!h.ok) return h;
      const settings = await getSettings();
      const r = await request({ t: 'pick', dir: settings.dir || hostInfo?.defaultDir || '' }, 15 * 60 * 1000);
      if (r.t === 'pick' && r.dir) await setSettings({ dir: r.dir });
      push();
      return { ok: true, dir: r.dir || null };
    }
    case 'update': {
      const h = await hello();
      if (!h.ok) return h;
      const r = await request({ t: 'update' }, 240000);
      if (r.ytdlp && hostInfo) hostInfo.ytdlp = r.ytdlp;
      probeCache.clear();
      push();
      return { ok: !!r.ok, error: r.error || null, ytdlp: r.ytdlp || null };
    }
    case 'settings':
      return setSettings(m.patch || {});
    case 'resetHost':
      hostInfo = null;
      probeCache.clear();
      return hello();
    default:
      return { ok: false };
  }
}

chrome.runtime.onMessage.addListener((m, _sender, sendResponse) => {
  handle(m).then(sendResponse, (e) => sendResponse({ ok: false, code: 'error', error: String(e?.message || e) }));
  return true;
});

chrome.runtime.onConnect.addListener((p) => {
  if (p.name !== 'popup') return;
  popups.add(p);
  ready.then(() => p.postMessage({ type: 'state', state: snapshot() }));
  p.onDisconnect.addListener(() => {
    popups.delete(p);
    scheduleIdle();
  });
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    const patterns = ['http://*/*', 'https://*/*'];
    const contexts = ['page', 'link', 'video'];
    chrome.contextMenus.create({ id: 'vg', title: 'VidGrab', contexts, documentUrlPatterns: patterns });
    chrome.contextMenus.create({ id: 'vg-video', parentId: 'vg', title: 'Скачать видео', contexts, documentUrlPatterns: patterns });
    chrome.contextMenus.create({ id: 'vg-audio', parentId: 'vg', title: 'Скачать звук', contexts, documentUrlPatterns: patterns });
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== 'vg-video' && info.menuItemId !== 'vg-audio') return;
  const link = info.linkUrl && /^https?:/i.test(info.linkUrl) ? info.linkUrl : null;
  const url = link || info.pageUrl || tab?.url;
  if (!url || !/^https?:/i.test(url)) return;
  const s = await getSettings();
  const audio = info.menuItemId === 'vg-audio';
  await startJob({
    url,
    mode: audio ? 'audio' : 'video',
    q: audio ? s.aq : s.vq,
    label: null,
    title: link ? link : tab?.title
  });
});

chrome.notifications.onClicked.addListener(async (nid) => {
  chrome.notifications.clear(nid);
  const job = jobs.get(nid.replace(/^vg-/, ''));
  if (job?.status === 'done' && job.file) handle({ cmd: 'open', path: job.file, reveal: true });
});

/*
 * ═══════════════════════════════════════
 *  VIDGRAB BACKGROUND — КАК РАБОТАЕТ
 * ═══════════════════════════════════════
 *  Один native-порт к com.vidgrab.host: открыт, пока есть загрузки, запросы или открытый попап;
 *  через 30 с простоя закрывается и хост завершается. Открытый порт держит service worker живым.
 *  Попап: команды через runtime.sendMessage, состояние приходит пушем в порт "popup".
 *  Куки текущего сайта передаются хосту в Netscape-формате; он подставляет их только при ошибке входа.
 *  История загрузок в storage.local (последние 20), probe-кэш на 10 мин.
 *  Если версия расширения на диске новее загруженной — один раз runtime.reload().
 * ═══════════════════════════════════════
*/
