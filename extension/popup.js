const $ = (id) => document.getElementById(id);

const AUDIO = [
  { q: 'best', big: 'Оригинал', small: 'm4a' },
  { q: '320', big: '320', small: 'kbps' },
  { q: '192', big: '192', small: 'kbps' },
  { q: '128', big: '128', small: 'kbps' }
];

const STAGES = {
  start: 'Подготовка',
  video: 'Видео',
  audio: 'Звук',
  single: 'Загрузка',
  'pp:Merger': 'Склейка',
  'pp:FFmpegMerger': 'Склейка',
  'pp:ExtractAudio': 'Конвертация',
  'pp:FFmpegExtractAudio': 'Конвертация',
  'pp:EmbedThumbnail': 'Обложка',
  'pp:FFmpegMetadata': 'Метаданные',
  'pp:Metadata': 'Метаданные',
  'pp:MoveFiles': 'Сохранение'
};

const ERRORS = [
  [/^NOHOST$|host not found/i, 'Помощник не установлен: запусти install.cmd'],
  [/forbidden/i, 'Нет доступа к помощнику: запусти install.cmd ещё раз'],
  [/^INTERRUPTED$/, 'Прервано: браузер закрылся во время загрузки'],
  [/^PLAYLIST$/, 'Это плейлист или канал: открой конкретное видео'],
  [/^LIVE$/, 'Это прямой эфир: скачать можно после окончания'],
  [/Unsupported URL/i, 'Этот сайт не поддерживается'],
  [/Private video/i, 'Приватное видео'],
  [/not a bot/i, 'YouTube просит войти: зайди в аккаунт в этом браузере и повтори'],
  [/confirm your age|age[- ]restrict|inappropriate/i, 'Возрастное ограничение: войди в аккаунт на сайте и повтори'],
  [/members[- ]only|premium|subscri/i, 'Видео только для подписчиков'],
  [/Video unavailable|not available|has been removed/i, 'Видео недоступно или удалено'],
  [/Requested format is not available/i, 'Это качество недоступно, выбери другое'],
  [/HTTP Error 403/i, 'Сайт отклонил запрос (403), повтори через минуту'],
  [/HTTP Error 429|Too Many Requests/i, 'Слишком много запросов, подожди немного'],
  [/No space left|Errno 28/i, 'Не хватает места на диске'],
  [/getaddrinfo|timed out|Connection|Unable to download|Network|SSL/i, 'Нет связи с сайтом. Если он открывается только через VPN-расширение, укажи прокси в настройках'],
  [/Native host has exited|Host disconnected/i, 'Помощник неожиданно закрылся, повтори'],
  [/^TIMEOUT$/, 'Помощник не ответил вовремя']
];

const NOTICES = {
  page: ['Здесь нечего скачивать', 'Открой страницу с видео и нажми на значок снова.', null],
  nohost: ['Помощник не установлен', 'Запусти install.cmd из папки VidGrab. Он поставит yt-dlp и ffmpeg, это займёт пару минут.', 'Проверить снова'],
  forbidden: ['Помощник не пускает расширение', 'Запусти install.cmd ещё раз, он заново пропишет доступ.', 'Проверить снова'],
  error: ['Помощник не отвечает', '', 'Проверить снова']
};

let tab = null;
let info = null;
let settings = null;
let state = { jobs: [], busy: null, ytdlp: null, defaultDir: null };
let selected = { video: null, audio: null };
const rows = new Map();

function errText(raw) {
  const s = String(raw || '');
  for (const [rx, text] of ERRORS) if (rx.test(s)) return text;
  return s || 'Неизвестная ошибка';
}

function fmtDuration(sec) {
  if (!sec || sec < 0) return '';
  sec = Math.round(sec);
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = String(sec % 60).padStart(2, '0');
  return h ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
}

function fmtSpeed(bps) {
  if (!bps) return '';
  const mb = bps / 1048576;
  return mb >= 1 ? `${mb.toFixed(1).replace('.', ',')} МБ/с` : `${Math.round(bps / 1024)} КБ/с`;
}

function baseName(p) {
  return String(p || '').replace(/[\\/]+$/, '').split(/[\\/]/).pop() || p;
}

function send(cmd, extra) {
  return chrome.runtime.sendMessage({ cmd, ...extra });
}

function mode() {
  return document.body.classList.contains('audio') ? 'audio' : 'video';
}

function setMode(m) {
  document.body.classList.toggle('audio', m === 'audio');
  document.body.classList.toggle('video', m !== 'audio');
  for (const b of document.querySelectorAll('.modes button')) {
    b.setAttribute('aria-selected', String(b.dataset.mode === m));
  }
  renderScale();
}

function videoOptions() {
  return (info?.q || []).map((o) => ({
    q: String(o.res),
    label: o.label,
    big: String(o.label),
    small: o.fps > 30 ? `p${o.fps}` : 'p'
  }));
}

function renderScale() {
  const m = mode();
  const opts = m === 'audio' ? AUDIO : videoOptions();
  const scale = $('scale');
  scale.textContent = '';

  if (!opts.length) {
    selected[m] = null;
    $('hint').hidden = false;
    $('hint').textContent = m === 'audio' ? '' : 'Качество не указано, скачаю лучшее доступное.';
    updateGo();
    return;
  }

  if (!selected[m] || !opts.some((o) => o.q === selected[m].q)) {
    if (m === 'audio') {
      selected.audio = opts.find((o) => o.q === String(settings.aq)) || opts[1];
    } else {
      const want = Number(settings.vq) || 1080;
      selected.video = opts.find((o) => o.label === want) || opts.find((o) => o.label <= want) || opts[opts.length - 1];
    }
  }

  for (const o of opts) {
    const b = document.createElement('button');
    b.type = 'button';
    b.setAttribute('role', 'radio');
    b.setAttribute('aria-checked', String(o.q === selected[m].q));
    const big = document.createElement('b');
    big.textContent = o.big;
    if (!/^\d+$/.test(o.big)) big.className = 'word';
    const small = document.createElement('small');
    small.textContent = o.small;
    b.append(big, small);
    b.addEventListener('click', () => {
      selected[m] = o;
      for (const x of scale.children) x.setAttribute('aria-checked', String(x === b));
      updateGo();
    });
    scale.append(b);
  }

  const hint = $('hint');
  if (m === 'audio' && info?.abr > 0) {
    hint.hidden = false;
    hint.textContent = `Звук в источнике ≈ ${info.abr} kbps. MP3 с битрейтом выше не станет качественнее.`;
  } else {
    hint.hidden = true;
  }
  updateGo();
}

function updateGo() {
  const go = $('go');
  if (go.classList.contains('added')) return;
  const s = selected[mode()];
  if (mode() === 'audio') {
    go.textContent = !s || s.q === 'best' ? 'Скачать звук без перекодирования' : `Скачать MP3 ${s.q}`;
  } else {
    go.textContent = s ? `Скачать ${s.label}p` : 'Скачать видео';
  }
}

function showNotice(kind, detail, onAction) {
  const [title, text, action] = NOTICES[kind] || [kind, '', null];
  $('noticeTitle').textContent = title;
  $('noticeText').textContent = detail ?? text;
  $('noticeText').hidden = !(detail ?? text);
  const btn = $('noticeBtn');
  btn.hidden = !action && !onAction;
  btn.textContent = action || 'Попробовать снова';
  btn.onclick = onAction || (() => location.reload());
  $('notice').hidden = false;
  $('picker').hidden = true;
}

function showMedia(data) {
  const media = $('media');
  media.hidden = false;
  media.setAttribute('aria-busy', String(!data));
  $('title').textContent = data?.title || tab?.title || '';
  $('sub').textContent = data ? data.uploader || data.site || '' : '';
  const img = $('thumbImg');
  if (data?.thumb) {
    img.src = data.thumb;
    img.hidden = false;
    img.onerror = () => (img.hidden = true);
  }
  const dur = fmtDuration(data?.duration);
  $('dur').hidden = !dur;
  $('dur').textContent = dur;
}

async function probe(fresh) {
  $('notice').hidden = true;
  showMedia(null);
  const r = await send('probe', { url: tab.url, fresh });
  if (r?.ok) {
    info = r.info;
    showMedia(info);
    $('picker').hidden = false;
    renderScale();
    return;
  }
  info = null;
  if (r?.code === 'nohost' || r?.code === 'forbidden' || r?.code === 'error') {
    $('media').hidden = true;
    showNotice(r.code, r.code === 'error' ? errText(r.error) : undefined, async () => {
      await send('resetHost');
      probe(true);
    });
    return;
  }
  showMedia({ title: tab.title });
  $('media').setAttribute('aria-busy', 'false');
  const raw = r?.error || '';
  const title = errText(raw);
  const detail = title === raw ? '' : raw;
  $('noticeTitle').textContent = title;
  $('noticeText').textContent = detail;
  $('noticeText').hidden = !detail;
  $('noticeBtn').hidden = false;
  $('noticeBtn').textContent = 'Попробовать снова';
  $('noticeBtn').onclick = () => probe(true);
  $('notice').hidden = false;
  $('picker').hidden = true;
}

async function download() {
  const m = mode();
  const s = selected[m];
  const go = $('go');
  const payload = {
    url: tab.url,
    mode: m,
    q: s ? s.q : '',
    label: m === 'video' && s ? s.label : null,
    title: info?.title || tab.title,
    thumb: info?.thumb || null
  };
  await send('dl', payload);
  settings = await send('settings', {
    patch: m === 'audio' ? { mode: m, aq: s ? s.q : 'best' } : { mode: m, vq: s ? s.label : settings.vq }
  });
  go.classList.add('added');
  go.textContent = 'Добавлено в загрузки';
  setTimeout(() => {
    go.classList.remove('added');
    updateGo();
  }, 1400);
}

function jobTag(j) {
  if (j.mode === 'audio') return /^\d+$/.test(String(j.q)) ? `MP3 ${j.q}` : 'звук';
  return j.label ? `${j.label}p` : 'видео';
}

function jobStatus(j) {
  if (j.status === 'done') return 'Готово';
  if (j.status === 'fail') return errText(j.error);
  const stage = STAGES[j.stage] || (String(j.stage).startsWith('pp:') ? 'Обработка' : 'Загрузка');
  const pct = j.stage === 'start' || String(j.stage).startsWith('pp:') ? '' : ` ${Math.floor(j.pct || 0)}%`;
  const extra = [fmtSpeed(j.speed), j.eta ? `ещё ${fmtDuration(j.eta)}` : ''].filter(Boolean).join(', ');
  return `${stage}${pct}${extra ? ` ${extra}` : ''}`;
}

function isThrottled(j) {
  if (j.status !== 'run' || !j.speed || j.speed > 300 * 1024) return false;
  if (!/(^|\.)(youtube\.com|youtu\.be)$/i.test(hostOf(j.url))) return false;
  return (j.eta || 0) > 120;
}

function hostOf(url) {
  try {
    return new URL(url).hostname;
  } catch {
    return '';
  }
}

function actionButton(text, cls, onClick, label) {
  const b = document.createElement('button');
  b.type = 'button';
  b.textContent = text;
  if (cls) b.className = cls;
  if (label) b.setAttribute('aria-label', label);
  b.addEventListener('click', onClick);
  return b;
}

function buildRow() {
  const li = document.createElement('li');
  li.className = 'job';
  li.innerHTML = '<div class="row1"><span class="jt"></span><span class="tag"></span></div><div class="bar"><i></i></div><div class="row2"><span class="st"></span><span class="acts"></span></div><p class="warn" hidden>Скорость упирается в замедление YouTube: yt-dlp идёт мимо VPN-расширения браузера. Включи системный VPN или укажи прокси в настройках.</p>';
  return li;
}

function fillActions(li, j) {
  const acts = li.querySelector('.acts');
  if (acts.dataset.for === j.status) return;
  acts.dataset.for = j.status;
  acts.textContent = '';
  if (j.status === 'run') {
    acts.append(actionButton('×', 'x', () => send('cancel', { id: j.id }), 'Отменить'));
  } else if (j.status === 'done') {
    if (j.file) {
      acts.append(
        actionButton('Открыть', '', () => send('open', { path: j.file })),
        actionButton('В папке', '', () => send('open', { path: j.file, reveal: true }))
      );
    }
    acts.append(actionButton('×', 'x', () => send('remove', { id: j.id }), 'Убрать из списка'));
  } else {
    acts.append(
      actionButton('Повторить', '', () => send('retry', { id: j.id })),
      actionButton('×', 'x', () => send('remove', { id: j.id }), 'Убрать из списка')
    );
  }
}

function renderJobs() {
  const ul = $('jobs');
  const list = state.jobs || [];
  $('list').hidden = list.length === 0;
  $('clear').hidden = !list.some((j) => j.status !== 'run');

  const alive = new Set(list.map((j) => j.id));
  for (const [id, li] of rows) {
    if (!alive.has(id)) {
      li.remove();
      rows.delete(id);
    }
  }

  list.forEach((j, i) => {
    let li = rows.get(j.id);
    if (!li) {
      li = buildRow();
      rows.set(j.id, li);
    }
    if (ul.children[i] !== li) ul.insertBefore(li, ul.children[i] || null);
    li.dataset.status = j.status;
    li.classList.toggle('audio', j.mode === 'audio');
    li.querySelector('.jt').textContent = j.title || j.url;
    li.querySelector('.jt').title = j.file || j.title || j.url;
    li.querySelector('.tag').textContent = jobTag(j);
    li.querySelector('.bar i').style.width = `${Math.min(100, j.pct || 0)}%`;
    const st = li.querySelector('.st');
    st.textContent = jobStatus(j);
    st.title = j.status === 'fail' ? String(j.error || '') : '';
    li.querySelector('.warn').hidden = !isThrottled(j);
    fillActions(li, j);
  });
}

function renderState() {
  $('busy').hidden = state.busy !== 'update';
  const dir = settings?.dir || state.defaultDir;
  $('folderName').textContent = dir ? baseName(dir) : 'Загрузки';
  $('folder').title = dir ? `Открыть ${dir}` : 'Открыть папку загрузок';
  $('ver').textContent = state.ytdlp ? `yt-dlp ${state.ytdlp}` : 'yt-dlp';
  renderJobs();
}

function bindSettings() {
  $('gear').addEventListener('click', () => {
    const open = $('settings').hidden;
    $('settings').hidden = !open;
    $('gear').setAttribute('aria-expanded', String(open));
    if (open) $('proxy').focus();
  });

  const proxy = $('proxy');
  proxy.value = settings.proxy || '';
  const saveProxy = async () => {
    const v = proxy.value.trim();
    const ok = !v || /^(https?|socks4a?|socks5h?):\/\/\S+$/i.test(v);
    proxy.classList.toggle('bad', !ok);
    if (ok && v !== settings.proxy) settings = await send('settings', { patch: { proxy: v } });
  };
  proxy.addEventListener('change', saveProxy);
  proxy.addEventListener('blur', saveProxy);

  $('upd').addEventListener('click', async () => {
    const b = $('upd');
    b.disabled = true;
    b.textContent = 'Обновляю';
    const r = await send('update');
    b.disabled = false;
    b.textContent = r?.ok ? 'Обновлено' : 'Не вышло';
    if (!r?.ok && r?.error) b.title = r.error;
  });

  $('folder').addEventListener('click', () => {
    const dir = settings.dir || state.defaultDir;
    if (dir) send('open', { path: dir });
  });

  $('pick').addEventListener('click', async () => {
    const r = await send('pick');
    if (r?.dir) {
      settings = await send('state').then((s) => s.settings);
      renderState();
    }
  });

  $('clear').addEventListener('click', () => send('clear'));
}

async function init() {
  const initial = await send('state');
  settings = initial.settings;
  state = initial;
  bindSettings();
  renderState();

  const portToBg = chrome.runtime.connect({ name: 'popup' });
  portToBg.onMessage.addListener((m) => {
    if (m.type !== 'state') return;
    state = m.state;
    renderState();
  });

  for (const b of document.querySelectorAll('.modes button')) {
    b.addEventListener('click', () => setMode(b.dataset.mode));
  }
  $('go').addEventListener('click', download);
  setMode(settings.mode === 'audio' ? 'audio' : 'video');

  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.url || !/^https?:/i.test(tab.url)) {
    $('media').hidden = true;
    showNotice('page');
    return;
  }
  probe(false);
}

init();

/*
 * ═══════════════════════════════════════
 *  VIDGRAB POPUP — КАК РАБОТАЕТ
 * ═══════════════════════════════════════
 *  Открытие: probe URL активной вкладки → превью + шкала качеств из ответа yt-dlp.
 *  Режим «Видео»: качества по реальным форматам; «Звук»: оригинал (m4a) или MP3 320/192/128.
 *  Список загрузок обновляется пушем из background, строки переиспользуются по id.
 *  Последний режим и качество запоминаются в storage.local.
 * ═══════════════════════════════════════
*/
