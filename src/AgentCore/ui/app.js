'use strict';

// ---------- small helpers ----------------------------------------------------

const $ = (s, r = document) => r.querySelector(s);

function h(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k === 'class') e.className = v;
    else if (k === 'text') e.textContent = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (v !== false && v != null) e.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) if (kid != null) e.append(kid.nodeType ? kid : document.createTextNode(kid));
  return e;
}

function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'i');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', '#i-' + name);
  svg.append(use);
  return svg;
}

async function call(url, opts) {
  const r = await fetch(url, opts);
  const t = await r.text();
  let d = {};
  try { d = t ? JSON.parse(t) : {}; } catch { d = { error: t }; }
  if (!r.ok) { const e = new Error(d.error || r.statusText); e.status = r.status; throw e; }
  return d;
}
const get = (u) => call(u);
const post = (u, b) => call(u, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(b || {}) });

function parseErr(text) { try { return JSON.parse(text).error || text; } catch { return text; } }
const fmt = (n) => Number(n).toLocaleString('en-NZ');
const errBox = (msg) => h('div', { class: 'err', text: msg });

const pref = {
  get(k, d) { try { const v = localStorage.getItem('ashley.' + k); return v === null ? d : JSON.parse(v); } catch { return d; } },
  set(k, v) { try { localStorage.setItem('ashley.' + k, JSON.stringify(v)); } catch { /* private mode */ } },
};

const S = { busy: false, abort: null, voice: null, audio: null, status: null, ctx: null, panelPath: '.' };
const log = $('#log');
let stick = true;
log.addEventListener('scroll', () => { stick = log.scrollHeight - log.scrollTop - log.clientHeight < 120; });
const scrollDown = (force) => { if (force || stick) log.scrollTop = log.scrollHeight; };

let toastTimer;
function toast(msg, kind = '', ms = 4000) {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast ' + kind;
  t.hidden = false;
  clearTimeout(toastTimer);
  if (ms) toastTimer = setTimeout(() => { t.hidden = true; }, ms);
}
const clearToast = () => { $('#toast').hidden = true; };

// ---------- markdown (escaped first, so nothing from the model can inject HTML) ---

const esc = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

function inline(s) {
  s = esc(s);
  s = s.replace(/`([^`\n]+)`/g, '<code>$1</code>');
  s = s.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  s = s.replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>');
  return s;
}

function md(src) {
  const out = [];
  const lines = src.replace(/\r/g, '').split('\n');
  const isItem = (l) => /^\s*([-*]|\d+\.)\s+/.test(l);
  let i = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (/^```/.test(l)) {
      const buf = [];
      i++;
      while (i < lines.length && !/^```/.test(lines[i])) buf.push(lines[i++]);
      i++;
      out.push('<div class="code"><button type="button" class="copy" data-copy>Copy</button><pre><code>' + esc(buf.join('\n')) + '</code></pre></div>');
      continue;
    }
    if (isItem(l)) {
      const ordered = /^\s*\d+\./.test(l);
      const items = [];
      while (i < lines.length && isItem(lines[i])) items.push(inline(lines[i++].replace(/^\s*([-*]|\d+\.)\s+/, '')));
      const tag = ordered ? 'ol' : 'ul';
      out.push(`<${tag}>` + items.map((x) => `<li>${x}</li>`).join('') + `</${tag}>`);
      continue;
    }
    const head = /^#{1,4}\s+(.*)$/.exec(l);
    if (head) { out.push('<p class="h">' + inline(head[1]) + '</p>'); i++; continue; }
    if (!l.trim()) { i++; continue; }
    const buf = [];
    while (i < lines.length && lines[i].trim() && !/^```/.test(lines[i]) && !isItem(lines[i]) && !/^#{1,4}\s/.test(lines[i])) buf.push(inline(lines[i++]));
    out.push('<p>' + buf.join('<br>') + '</p>');
  }
  return out.join('');
}

// Plain version for speaking (mirrors SpeechText on the server).
function speakable(t) {
  t = t.replace(/```[\s\S]*?```/g, ' (code omitted) ').replace(/`([^`]*)`/g, '$1').replace(/https?:\/\/\S+/g, 'a link');
  const s = t.split('\n')
    .map((l) => l.replace(/^\s*([-*+•]|\d+[.)])\s+/, '').replace(/^\s*#{1,6}\s*/, '').replace(/(\*\*|\*|(?<!\w)__?|__?(?!\w))/g, '').trim())
    .filter(Boolean).map((l) => (/[.!?:]$/.test(l) ? l : l + '.')).join(' ');
  if (s.length <= 600) return s;
  const cut = s.slice(0, 600);
  const end = Math.max(cut.lastIndexOf('.'), cut.lastIndexOf('!'), cut.lastIndexOf('?'));
  return (end > 100 ? cut.slice(0, end + 1) : cut) + ' The rest is in the written reply.';
}

function copyText(t) {
  (navigator.clipboard ? navigator.clipboard.writeText(t) : Promise.reject())
    .then(() => toast('Copied', 'ok', 1500))
    .catch(() => toast('Couldn\u2019t copy', 'bad'));
}
log.addEventListener('click', (e) => {
  const b = e.target.closest('[data-copy]');
  if (b) copyText(b.parentElement.querySelector('code').textContent);
});

// ---------- status, banner, context meter -------------------------------------

function banner(s) {
  const b = $('#banner');
  if (!s) { b.hidden = true; return; }
  let text;
  if (s.offline) text = 'Can\u2019t reach the agent. Is its container running? cd F:\\AI-Agents\\ashley ; docker compose --profile server up -d server';
  else text = `Can\u2019t reach the model ${s.model.name}.` + (s.model.launch ? ` Start it with: ${s.model.launch}` : '');
  b.replaceChildren(icon('alert'), h('span', { text }));
  b.hidden = false;
}

async function refreshStatus() {
  try {
    const s = await get('/status');
    S.status = s;
    $('#agent-name').textContent = s.agent;
    document.title = s.agent;
    $('#dot').className = 'dot ' + (s.reachable ? 'ok' : 'bad');
    $('#dot').title = s.reachable ? 'Model server reachable' : s.problem;
    banner(s.reachable ? null : s);
    if (!S.busy) updateMeter(s.context);
  } catch {
    $('#dot').className = 'dot bad';
    banner({ offline: true });
  }
}

function updateMeter(c) {
  if (!c) return;
  S.ctx = c;
  const win = c.window;
  const pct = Math.min(100, (c.used / win) * 100);
  $('#meter-text').innerHTML = `<b>${fmt(c.used)}</b> / ${fmt(win)} tokens \u00b7 ${Math.round(pct)}%${c.estimated ? ' (estimate)' : ''}`;
  for (const k of ['system', 'tools', 'notes', 'history']) $('#seg-' + k).style.width = Math.min(100, (c[k] / win) * 100) + '%';
  const m = $('.meter');
  m.classList.toggle('warn', pct >= 70 && pct < 90);
  m.classList.toggle('full', pct >= 90);
  $('#meter-bar').setAttribute('aria-valuenow', Math.round(pct));
  const parts = [['system', 'System', c.system], ['tools', 'Tools', c.tools], ['notes', 'Notes', c.notes], ['history', 'Chat', c.history]];
  $('#legend').replaceChildren(
    ...parts.map(([k, l, n]) => h('span', {}, h('i', { style: `background:var(--seg-${k})` }), `${l} ${fmt(n)}`)),
    h('span', { style: 'margin-left:auto', text: `Replies up to ${fmt(c.maxOutput)} tokens` }));
}

// The server reports the real prompt size after each model call, mid-job.
function liveUsage(tokens, window) {
  if (!S.ctx) return;
  const c = { ...S.ctx, used: tokens, window: window || S.ctx.window, estimated: false };
  c.history = Math.max(0, tokens - c.system - c.tools - c.notes);
  updateMeter(c);
}

// ---------- selects: model, persona, voice ------------------------------------

async function loadModels() {
  const d = await get('/models');
  const sel = $('#sel-model');
  sel.replaceChildren(
    ...d.models.map((m) => h('option', { value: m.name }, m.name + (m.reachable ? '' : ' (offline)'))),
    h('option', { value: '__manage' }, 'Manage models\u2026'));
  sel.value = d.current;
  return d;
}

async function switchModel(name) {
  try {
    await post('/model', { name });
    toast(`Now using ${name}. The conversation continues.`, 'ok');
  } catch (e) { toast(e.message, 'bad'); }
  await Promise.all([loadModels().catch(() => {}), refreshStatus()]);
}

$('#sel-model').addEventListener('change', async (e) => {
  const v = e.target.value;
  if (v === '__manage') { e.target.value = S.status?.model.name || ''; openModels(); return; }
  await switchModel(v);
});

async function loadPersonas() {
  const d = await get('/personas');
  const sel = $('#sel-persona');
  sel.replaceChildren(...d.personas.map((p) => h('option', { value: p }, p)));
  sel.value = d.current;
}

$('#sel-persona').addEventListener('change', async (e) => {
  try {
    await post('/persona', { name: e.target.value });
    toast(`Persona is now ${e.target.value}. The conversation continues.`, 'ok');
  } catch (err) { toast(err.message, 'bad'); await loadPersonas(); }
});

async function loadVoices() {
  const sel = $('#sel-voice');
  try {
    const [list, saved] = await Promise.all([get('/voices'), get('/voice')]);
    S.voice = saved.voice && list.voices.includes(saved.voice) ? saved.voice : list.default;
    sel.replaceChildren(...list.voices.map((v) => h('option', { value: v }, v)), h('option', { value: '__add' }, 'Add a voice\u2026'));
    sel.value = S.voice;
    return list;
  } catch {
    sel.replaceChildren(h('option', { value: '' }, 'speech offline'));
    return null;
  }
}

$('#sel-voice').addEventListener('change', async (e) => {
  const v = e.target.value;
  if (v === '__add') { e.target.value = S.voice || ''; openVoices(); return; }
  await setVoice(v);
});

async function setVoice(name) {
  S.voice = name;
  try { await post('/voice', { name }); } catch (e) { toast(e.message, 'bad'); }
  $('#sel-voice').value = name;
  speak('Hello, this is how I sound now.');
}

// ---------- the conversation view -----------------------------------------------

const emptyState = () => log.replaceChildren(h('div', { class: 'empty' }, h('b', {}, 'Start a conversation'), 'Type a message, or hold the mic button (or the space bar) and talk.'));
const clearEmpty = () => { const e = $('.empty', log); if (e) e.remove(); };

function addUser(text) {
  clearEmpty();
  log.append(h('div', { class: 'msg user' }, h('div', { class: 'bubble', text })));
  scrollDown(true);
}

function newAssistant() {
  clearEmpty();
  const a = {
    root: h('div', { class: 'msg assistant' }),
    tools: h('div', { class: 'tools' }),
    reply: h('div', { class: 'reply' }),
    status: h('div', { class: 'status' }),
    meta: h('div', { class: 'meta' }),
    byId: new Map(),
  };
  a.root.append(h('div', { class: 'who', text: `${S.status?.agent || 'Ashley'} \u00b7 ${S.status?.model.name || ''}` }), a.tools, a.reply, a.status, a.meta);
  log.append(a.root);
  return a;
}

const setStatus = (a, text) => a.status.replaceChildren(h('span', { class: 'spin' }), h('span', { text }));

function toolIcon(name) {
  if (name === 'run_command') return 'terminal';
  if (name.startsWith('web_')) return 'globe';
  if (['remember', 'forget', 'todo'].includes(name)) return 'check';
  return 'file';
}

function prettyArgs(a) { try { return JSON.stringify(JSON.parse(a), null, 2); } catch { return a || ''; } }

function toolRow(name, what, args) {
  const stat = h('span', { class: 'tstat', text: 'running\u2026' });
  const resPre = h('pre', { text: '' });
  const el = h('details', { class: 'tool' },
    h('summary', {}, icon(toolIcon(name)), h('span', { class: 'tname', text: name }), h('span', { class: 'twhat', text: what || '' }), stat),
    h('div', { class: 'tbody' }, h('div', { class: 'lbl', text: 'Arguments' }), h('pre', { text: prettyArgs(args) }), h('div', { class: 'lbl', text: 'Result' }), resPre));
  return { el, stat, resPre };
}

function finishTool(t, result, failed, ms) {
  t.el.classList.add(failed ? 'fail' : 'ok');
  t.stat.textContent = failed ? 'failed' : ms == null ? 'done' : ms < 1000 ? ms + ' ms' : (ms / 1000).toFixed(1) + ' s';
  t.resPre.textContent = result || '(no output)';
}

function finalizeReply(a, text, info) {
  a.reply.innerHTML = md(text || '');
  a.status.replaceChildren();
  const bits = [];
  if (info.ms) bits.push((info.ms / 1000).toFixed(1) + ' s');
  const n = a.tools.querySelectorAll('.tool').length;
  if (n) bits.push(n + ' tool call' + (n === 1 ? '' : 's'));
  a.meta.replaceChildren(
    h('button', { type: 'button', class: 'icon', title: 'Speak this reply', 'aria-label': 'Speak this reply', onclick: () => speak(info.spoken || speakable(text || '')) }, icon('volume')),
    h('button', { type: 'button', class: 'icon', title: 'Copy', 'aria-label': 'Copy reply', onclick: () => copyText(text || '') }, icon('copy')),
    h('span', { text: bits.join(' \u00b7 ') }));
}

function renderHistory(turns) {
  log.replaceChildren();
  if (!turns.length) { emptyState(); return; }
  for (const t of turns) {
    if (t.role === 'user') { addUser(t.text); continue; }
    const a = newAssistant();
    for (const n of t.notes) a.tools.append(h('div', { class: 'note', text: n.length > 300 ? n.slice(0, 300) + '\u2026' : n }));
    for (const tool of t.tools) {
      const r = toolRow(tool.name, tool.what, tool.args);
      finishTool(r, tool.result, tool.failed, null);
      a.tools.append(r.el);
    }
    finalizeReply(a, t.text, {});
  }
  scrollDown(true);
}

async function loadHistory() {
  try { renderHistory((await get('/history')).turns); } catch { emptyState(); }
}

// ---------- sending a message ------------------------------------------------------

function setBusy(b) {
  S.busy = b;
  const send = $('#send');
  send.classList.toggle('stop', b);
  send.setAttribute('aria-label', b ? 'Stop' : 'Send');
  send.title = b ? 'Stop the running task' : 'Send';
  send.replaceChildren(icon(b ? 'stop' : 'send'));
  for (const id of ['sel-model', 'sel-persona', 'sel-voice']) $('#' + id).disabled = b;
}

function touched(name) {
  if (name === 'todo') refreshTodo();
  else if (name === 'remember' || name === 'forget') refreshNotes();
  else if (['write_file', 'edit_file', 'append_file', 'delete_file', 'move_file', 'run_command'].includes(name)) refreshFiles();
}

// A task that was stopped: tool rows still marked "running" were cut short.
function markStopped(a) {
  a.status.replaceChildren();
  for (const t of a.byId.values()) {
    if (!t.el.classList.contains('ok') && !t.el.classList.contains('fail')) { t.el.classList.add('fail'); t.stat.textContent = 'stopped'; t.resPre.textContent = '(stopped before it finished)'; }
  }
  a.reply.append(h('div', { class: 'note', text: '(stopped)' }));
}

function handleEvent(a, e) {
  switch (e.type) {
    case 'thinking': setStatus(a, e.round > 1 ? 'Working\u2026' : 'Thinking\u2026'); break;
    case 'usage': liveUsage(e.tokens, e.window); break;
    case 'text': a.tools.append(h('div', { class: 'note', text: e.text.length > 300 ? e.text.slice(0, 300) + '\u2026' : e.text })); break;
    case 'tool_start': {
      const t = toolRow(e.name, e.text, e.args);
      a.byId.set(e.id, t);
      a.tools.append(t.el);
      setStatus(a, `Running ${e.name}\u2026`);
      break;
    }
    case 'tool_end': {
      const t = a.byId.get(e.id);
      if (t) finishTool(t, e.result, e.failed, e.ms);
      touched(e.name);
      break;
    }
    case 'final':
      finalizeReply(a, e.reply, e);
      updateMeter(e.context);
      if ($('#opt-speak').checked) speak(e.spoken);
      return true;
    case 'error': a.status.replaceChildren(); a.reply.append(errBox(e.error)); return true;
    case 'stopped': markStopped(a); return true;
  }
  scrollDown();
  return false;
}

async function send(text) {
  text = text.trim();
  if (!text || S.busy) return;
  setBusy(true);
  clearToast();
  stopSpeaking();
  addUser(text);
  $('#input').value = '';
  autoGrow();
  const a = newAssistant();
  setStatus(a, 'Thinking\u2026');
  S.abort = new AbortController();
  let finished = false;
  try {
    const r = await fetch('/chat/stream', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: S.abort.signal,
      body: JSON.stringify({ text, voice: $('#opt-speak').checked }),
    });
    if (!r.ok || !r.body) throw new Error(parseErr(await r.text()) || r.statusText);
    const reader = r.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let i;
      while ((i = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, i).trim();
        buf = buf.slice(i + 1);
        if (line && handleEvent(a, JSON.parse(line))) finished = true;
      }
    }
    if (!finished) { a.status.replaceChildren(); a.reply.append(errBox('The connection closed before she finished.')); }
  } catch (e) {
    a.status.replaceChildren();
    if (e.name === 'AbortError') markStopped(a);
    else a.reply.append(errBox(e.message));
  } finally {
    S.abort = null;
    setBusy(false);
    refreshPanels();
    refreshStatus();
    $('#input').focus();
  }
}

$('#composer').addEventListener('submit', (e) => {
  e.preventDefault();
  if (S.busy) { S.abort?.abort(); return; }
  send($('#input').value);
});

const input = $('#input');
function autoGrow() { input.style.height = 'auto'; input.style.height = Math.min(180, input.scrollHeight) + 'px'; }
input.addEventListener('input', autoGrow);
input.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); $('#composer').requestSubmit(); }
});

// ---------- speaking -----------------------------------------------------------------

async function speak(text, voice) {
  stopSpeaking();
  if (!text) return;
  try {
    const r = await fetch('/tts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text, voice: voice || S.voice || undefined }) });
    if (!r.ok) throw new Error(parseErr(await r.text()) || 'speech failed');
    const audio = new Audio(URL.createObjectURL(await r.blob()));
    S.audio = audio;
    $('#mic').classList.add('speaking');
    audio.onended = audio.onerror = () => { if (S.audio === audio) stopSpeaking(); };
    await audio.play();
  } catch (e) {
    stopSpeaking();
    toast('Couldn\u2019t speak: ' + e.message, 'bad', 7000);
  }
}

function stopSpeaking() {
  const a = S.audio;
  S.audio = null;
  if (a) { a.pause(); try { URL.revokeObjectURL(a.src); } catch { /* ignore */ } }
  $('#mic').classList.remove('speaking');
}

// ---------- hold to talk ---------------------------------------------------------------

// The microphone is opened fresh for every recording and released afterwards. (Keeping one
// stream open between recordings could go silent on the second try, and it left the browser's
// "microphone in use" marker on all the time.)
const mic = { sess: null, rec: null, chunks: [], active: false, down: false, src: '', token: 0, t0: 0, peak: 0, raf: 0, label: '', last: '' };
const micBtn = $('#mic');

function micError(e) {
  if (!navigator.mediaDevices) return 'The microphone needs a secure address. Open this page as http://127.0.0.1:5000 or http://localhost:5000.';
  if (e.name === 'NotAllowedError') return 'Microphone access was blocked. Allow it for this site in the browser, then try again.';
  if (e.name === 'NotFoundError') return 'No microphone found.';
  return 'Microphone problem: ' + e.message;
}

async function openMic() {
  if (!navigator.mediaDevices) throw new Error('insecure');
  const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 } });
  const ctx = new (window.AudioContext || window.webkitAudioContext)();
  if (ctx.state === 'suspended') await ctx.resume().catch(() => {});
  const an = ctx.createAnalyser();
  an.fftSize = 1024;
  ctx.createMediaStreamSource(stream).connect(an);
  return { stream, ctx, an, buf: new Uint8Array(an.fftSize), label: stream.getAudioTracks()[0]?.label || 'microphone' };
}

function closeMic(s) {
  if (!s) return;
  s.stream.getTracks().forEach((t) => t.stop());
  s.ctx.close().catch(() => {});
}

function resetMicUi() {
  micBtn.classList.remove('rec', 'wait');
  $('#level').hidden = true;
  $('#level-fill').style.width = '0';
}

function showMicInfo() {
  $('#mic-name').textContent = mic.label ? 'Mic: ' + mic.label + mic.last : '';
}

function levelLoop() {
  if (!mic.active || !mic.rec) return;
  // Safety net: if a release was ever missed, never stay in "recording".
  if (!mic.down || performance.now() - mic.t0 > 120000) { stopTalk(true); return; }
  const s = mic.sess;
  s.an.getByteTimeDomainData(s.buf);
  let p = 0, sum = 0;
  for (const v of s.buf) { const d = (v - 128) / 128; sum += d * d; if (Math.abs(d) > p) p = Math.abs(d); }
  mic.peak = Math.max(mic.peak, p);
  $('#level-fill').style.width = Math.max(3, Math.min(100, Math.sqrt(sum / s.buf.length) * 500)) + '%';
  $('#level-time').textContent = ((performance.now() - mic.t0) / 1000).toFixed(1) + 's';
  mic.raf = requestAnimationFrame(levelLoop);
}

async function startTalk(src) {
  if (mic.active) return;
  if (S.busy) { toast('She is still working. Press Stop first.', 'bad'); return; }
  stopSpeaking();
  const token = ++mic.token;
  mic.active = true;
  mic.down = true;
  mic.src = src;
  micBtn.classList.add('wait'); // opening the microphone; turns red when recording really starts
  let sess = null;
  try {
    sess = await openMic();
    if (token !== mic.token || !mic.active) { closeMic(sess); return; } // let go before the microphone was ready
    mic.sess = sess;
    mic.label = sess.label;
    mic.peak = 0;
    const mime = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus', 'audio/mp4'].find((m) => window.MediaRecorder && MediaRecorder.isTypeSupported(m));
    const chunks = [];
    mic.chunks = chunks;
    mic.rec = new MediaRecorder(sess.stream, mime ? { mimeType: mime } : undefined);
    mic.rec.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
    mic.t0 = performance.now();
    mic.rec.start();
    micBtn.classList.remove('wait');
    micBtn.classList.add('rec');
    $('#level').hidden = false;
    showMicInfo();
    levelLoop();
  } catch (e) {
    closeMic(sess);
    if (token === mic.token) { mic.active = false; mic.down = false; resetMicUi(); }
    toast(micError(e), 'bad', 9000);
  }
}

function stopTalk(send) {
  mic.down = false;
  if (!mic.active) return;
  mic.active = false;
  cancelAnimationFrame(mic.raf);
  resetMicUi();
  const rec = mic.rec, sess = mic.sess, chunks = mic.chunks;
  mic.rec = null;
  mic.sess = null;
  if (!rec || rec.state === 'inactive') { closeMic(sess); return; } // released before recording began
  const dur = performance.now() - mic.t0;
  const peak = mic.peak;
  rec.onstop = () => {
    closeMic(sess);
    if (send) handleRecording(new Blob(chunks, { type: rec.mimeType || 'audio/webm' }), dur, peak);
  };
  rec.stop();
}

async function handleRecording(blob, dur, peak) {
  const level = Math.round(peak * 100);
  mic.last = ` · last recording ${(dur / 1000).toFixed(1)} s, level ${level}%`;
  showMicInfo();
  if (dur < 400) { toast('Hold the button while you speak.', '', 3000); return; }
  if (peak < 0.02) { toast(`No sound heard from “${mic.label}” (level ${level}%). Check the microphone.`, 'bad', 9000); return; }
  toast('Transcribing…', '', 0);
  try {
    const r = await fetch('/stt', { method: 'POST', headers: { 'Content-Type': blob.type }, body: blob });
    const t = await r.text();
    if (!r.ok) throw new Error(parseErr(t));
    const text = (JSON.parse(t).text || '').trim();
    if (!text) { toast('Didn’t catch that. Try again.', 'bad'); return; }
    clearToast();
    input.value = text;
    autoGrow();
    if ($('#opt-auto').checked) send(text); else input.focus();
  } catch (e) {
    toast('Speech-to-text failed: ' + e.message, 'bad', 9000);
  }
}

micBtn.addEventListener('pointerdown', (e) => {
  if (e.button !== 0) return;
  e.preventDefault();
  try { micBtn.setPointerCapture(e.pointerId); } catch { /* not every pointer can be captured */ }
  startTalk('pointer');
});
micBtn.addEventListener('pointerup', () => { if (mic.src === 'pointer') stopTalk(true); });
micBtn.addEventListener('pointercancel', () => { if (mic.src === 'pointer') stopTalk(false); });
micBtn.addEventListener('contextmenu', (e) => e.preventDefault());

function spaceTalkOk(t) {
  if (document.querySelector('dialog[open]')) return false;
  if (t === micBtn) return true;
  // The cursor goes back to the message box after every reply. With nothing typed there,
  // the space bar still talks; once you have typed something it is an ordinary space.
  if (t === input && input.value === '') return true;
  return !t.closest('input,textarea,select,button,summary,a,[contenteditable]');
}
document.addEventListener('keydown', (e) => {
  if (e.code === 'Space' && !e.repeat && !e.ctrlKey && !e.metaKey && !e.altKey && spaceTalkOk(e.target)) {
    e.preventDefault();
    startTalk('key');
  } else if (e.key === 'Escape') {
    if (mic.active) stopTalk(false); else if (S.audio) stopSpeaking();
  }
});
document.addEventListener('keyup', (e) => { if (e.code === 'Space' && mic.active && mic.src === 'key') stopTalk(true); });
window.addEventListener('blur', () => stopTalk(false)); // never leave the mic recording if focus is lost
document.addEventListener('visibilitychange', () => { if (document.hidden) stopTalk(false); });

// ---------- side panels -------------------------------------------------------------------

const none = (t) => h('div', { class: 'none', text: t });

async function refreshTodo() {
  try {
    const d = await get('/todo');
    $('#todo').replaceChildren(...(d.items.length
      ? d.items.map((i) => h('div', { class: 'row' + (i.done ? ' done' : '') }, i.done ? icon('check') : h('span', { class: 'circle' }), h('span', { text: i.text })))
      : [none('Nothing planned.')]));
  } catch { /* panel is optional */ }
}

async function refreshNotes() {
  try {
    const d = await get('/notes');
    $('#notes').replaceChildren(...(d.notes.length ? d.notes.map((n) => h('div', { class: 'row' }, h('span', { text: n }))) : [none('No notes yet.')]));
  } catch { /* panel is optional */ }
}

async function refreshFiles(path) {
  if (typeof path === 'string') S.panelPath = path;
  try {
    const d = await get('/files?path=' + encodeURIComponent(S.panelPath));
    S.panelPath = d.path;
    $('#crumb').replaceChildren(
      h('a', { onclick: () => refreshFiles('.') }, 'workspace'),
      ...(d.path === '.' ? [] : [' / ' + d.path]));
    const rows = [];
    if (d.parent != null) rows.push(h('div', { class: 'row link', onclick: () => refreshFiles(d.parent) }, icon('folder'), h('span', { text: '..' })));
    for (const e of d.entries) {
      rows.push(e.type === 'folder'
        ? h('div', { class: 'row link', onclick: () => refreshFiles(e.path) }, icon('folder'), h('span', { text: e.name }))
        : h('div', { class: 'row link', onclick: () => openFile(e.path) }, icon('file'), h('span', { text: e.name })));
    }
    $('#files').replaceChildren(...(rows.length ? rows : [none('Empty.')]));
  } catch { if (S.panelPath !== '.') refreshFiles('.'); }
}

const refreshPanels = () => { refreshTodo(); refreshNotes(); refreshFiles(); };

// ---------- dialogs -------------------------------------------------------------------------

const dlg = $('#dlg');
function openDlg(title, ...nodes) {
  $('#dlg-title').textContent = title;
  $('#dlg-body').replaceChildren(...nodes);
  if (!dlg.open) dlg.showModal();
}
const dlgError = (msg) => $('#dlg-body').prepend(errBox(msg));
$('#dlg-close').addEventListener('click', () => dlg.close());
dlg.addEventListener('click', (e) => { if (e.target === dlg) dlg.close(); });

async function openFile(path) {
  try {
    const f = await get('/file?path=' + encodeURIComponent(path));
    openDlg(f.path, f.binary ? h('div', { class: 'msgline', text: `Binary file, ${fmt(f.size)} bytes.` })
      : h('pre', { class: 'big', text: f.text + (f.truncated ? '\n\n[... file is longer; showing the first part]' : '') }));
  } catch (e) { toast(e.message, 'bad'); }
}

async function openModels() {
  openDlg('Models', h('div', { class: 'msgline', text: 'Checking which model servers are running\u2026' }));
  try { renderModels(await loadModels()); } catch (e) { openDlg('Models', errBox(e.message)); }
}

function renderModels(d) {
  const mine = d.models.map((m) => h('div', { class: 'item' + (m.name === d.current ? ' current' : '') },
    h('span', { class: 'dot ' + (m.reachable ? 'ok' : 'bad'), title: m.reachable ? 'Reachable' : 'Not reachable' }),
    h('div', { class: 'grow' },
      h('div', {}, h('b', { text: m.name }), ' ', m.local ? h('span', { class: 'pill', text: 'added here' }) : null, ' ', m.supportsTools ? null : h('span', { class: 'pill warn', text: 'no tools' })),
      h('div', { class: 'sub', text: `${m.id} \u00b7 ${m.provider} \u00b7 ${fmt(m.contextSize)} ctx${m.description ? ' \u00b7 ' + m.description : ''}` }),
      !m.reachable && m.launch ? h('div', { class: 'sub', text: 'Not running. Start: ' + m.launch }) : null),
    m.name === d.current ? h('span', { class: 'pill ok', text: 'in use' })
      : h('button', { type: 'button', class: 'small', onclick: async () => { await switchModel(m.name); renderModels(await loadModels()); } }, 'Use'),
    m.local && m.name !== d.current
      ? h('button', { type: 'button', class: 'small', onclick: async () => { try { await post('/models/remove', { name: m.name }); renderModels(await loadModels()); } catch (e) { dlgError(e.message); } } }, 'Remove')
      : null));

  const found = d.discovered.length
    ? d.discovered.map((m) => h('div', { class: 'item' },
      h('div', { class: 'grow' },
        h('div', {}, h('b', { text: m.id }), ' ', m.tools === false ? h('span', { class: 'pill warn', text: 'no tools' }) : null, ' ', m.thinking ? h('span', { class: 'pill', text: 'thinking' }) : null),
        h('div', { class: 'sub', text: `${m.parameters || ''} \u00b7 ${m.sizeGb} GB` })),
      h('button', {
        type: 'button', class: 'small',
        onclick: async () => {
          try {
            await post('/models/add', { model: m.id, name: m.id.replace(/:latest$/, ''), supportsTools: m.tools !== false, think: m.thinking ? false : null, baseUrl: m.baseUrl });
            renderModels(await loadModels());
          } catch (e) { dlgError(e.message); }
        },
      }, 'Add')))
    : [h('div', { class: 'msgline', text: 'Nothing else is installed in Ollama, or Ollama isn\u2019t running.' })];

  openDlg('Models',
    h('h4', {}, 'Your models'), ...mine,
    h('h4', {}, 'Installed in Ollama, not in your list'), ...found,
    h('p', { class: 'msgline', text: 'The 30B is started with host\\start-qwen30b.bat. Ollama models need Ollama running. Models in config\\models.json are edited on disk; models added here are saved in the memory folder.' }));
}

async function openVoices() {
  const list = await loadVoices();
  if (!list) { openDlg('Voices', errBox('The speech service isn\u2019t running. Start it with: cd F:\\AI-Agents\\voice ; docker compose up -d')); return; }

  const results = h('div', {});
  const q = h('input', { type: 'text', placeholder: 'Search the Piper catalog: amy, en_GB, de_DE, lessac\u2026', 'aria-label': 'Search voices' });
  const search = async () => {
    results.replaceChildren(h('div', { class: 'msgline', text: 'Searching\u2026' }));
    try {
      const d = await get('/voices/catalog?q=' + encodeURIComponent(q.value.trim()));
      results.replaceChildren(
        h('div', { class: 'msgline', text: d.total ? `${d.total} match${d.total === 1 ? '' : 'es'}${q.value.trim() ? '' : ' (English voices)'}; showing up to 40.` : 'No voices match.' }),
        ...d.voices.map((v) => {
          const add = h('button', { type: 'button', class: 'small primary' }, 'Add (about 60 MB)');
          add.addEventListener('click', async () => {
            add.disabled = true;
            add.textContent = 'Downloading\u2026';
            try { await post('/voices/add', { name: v.name }); await openVoices(); }
            catch (e) { add.disabled = false; add.textContent = 'Add (about 60 MB)'; dlgError(e.message); }
          });
          return h('div', { class: 'item' }, h('div', { class: 'grow' }, h('div', {}, h('b', { text: v.name })), h('div', { class: 'sub', text: `${v.language} \u00b7 ${v.quality}` })),
            v.installed ? h('span', { class: 'pill', text: 'installed' }) : add);
        }));
    } catch (e) { results.replaceChildren(errBox(e.message)); }
  };
  q.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); search(); } });

  openDlg('Voices',
    h('h4', {}, 'Installed'),
    ...list.voices.map((v) => h('div', { class: 'item' + (v === S.voice ? ' current' : '') },
      h('div', { class: 'grow' }, h('b', { text: v })),
      h('button', { type: 'button', class: 'small', onclick: () => speak('Hello, this is how I sound.', v) }, 'Play'),
      v === S.voice ? h('span', { class: 'pill ok', text: 'in use' }) : h('button', { type: 'button', class: 'small', onclick: async () => { await setVoice(v); await openVoices(); } }, 'Use'))),
    h('h4', {}, 'Add a voice'),
    h('div', { style: 'display:flex;gap:8px' }, q, h('button', { type: 'button', class: 'small', onclick: search }, 'Search')),
    results);
}

async function openChats() {
  try {
    const d = await get('/chats');
    openDlg('Saved chats',
      h('div', { style: 'margin-bottom:10px' }, h('button', { type: 'button', class: 'small primary', onclick: () => { dlg.close(); newChat(); } }, 'Start a new chat')),
      ...(d.chats.length ? d.chats.map((c) => h('div', { class: 'item' },
        h('div', { class: 'grow' }, h('b', { text: c.title }), h('div', { class: 'sub', text: `${new Date(c.time).toLocaleString('en-NZ')} \u00b7 ${c.messages} message${c.messages === 1 ? '' : 's'}` })),
        h('button', {
          type: 'button', class: 'small',
          onclick: async () => {
            try { await post('/chats/open', { id: c.id }); dlg.close(); await loadHistory(); refreshPanels(); refreshStatus(); }
            catch (e) { dlgError(e.message); }
          },
        }, 'Open'))) : [h('div', { class: 'msgline', text: 'No saved chats yet. When you start a new chat, the current one is saved here.' })]));
  } catch (e) { openDlg('Saved chats', errBox(e.message)); }
}

async function openInspect() {
  try {
    const d = await get('/inspect');
    const c = d.context;
    openDlg('What is sent to the model',
      h('div', { class: 'msgline' }, h('b', { text: `${fmt(c.used)} of ${fmt(c.window)} tokens` }), ` \u2014 system ${fmt(c.system)}, tools ${fmt(c.tools)}, notes ${fmt(c.notes)}, chat ${fmt(c.history)}.`),
      h('h4', {}, 'System prompt'), h('pre', { class: 'big', text: d.prompt.systemPrompt }),
      h('h4', {}, 'Tools offered'), h('div', { class: 'msgline', text: d.prompt.tools.join(', ') }),
      h('h4', {}, `Conversation (${d.prompt.messages.length} messages)`),
      ...d.prompt.messages.map((m) => h('div', { class: 'msgline' }, h('b', { text: m.role + (m.tool ? ' \u00b7 ' + m.tool : '') + ': ' }), (m.calls && m.calls.length ? '[calls ' + m.calls.join('; ') + '] ' : '') + m.content.slice(0, 400))));
  } catch (e) { openDlg('What is sent to the model', errBox(e.message)); }
}

async function newChat() {
  if (S.busy) { toast('Stop the running task first.', 'bad'); return; }
  try {
    await post('/chats/new');
    emptyState();
    S.ctx = null;
    refreshPanels();
    refreshStatus();
    input.focus();
  } catch (e) { toast(e.message, 'bad'); }
}

$('#btn-chats').addEventListener('click', openChats);
$('#btn-new').addEventListener('click', newChat);
$('#btn-inspect').addEventListener('click', openInspect);
$('#btn-models').addEventListener('click', openModels);
$('#btn-panel').addEventListener('click', () => {
  const layout = $('#layout');
  if (window.matchMedia('(max-width: 900px)').matches) layout.classList.toggle('showpanel');
  else { layout.classList.toggle('nopanel'); pref.set('nopanel', layout.classList.contains('nopanel')); }
});

// ---------- start ---------------------------------------------------------------------------------

async function init() {
  $('#opt-speak').checked = pref.get('speak', true);
  $('#opt-auto').checked = pref.get('auto', true);
  $('#opt-speak').addEventListener('change', (e) => { pref.set('speak', e.target.checked); if (!e.target.checked) stopSpeaking(); });
  $('#opt-auto').addEventListener('change', (e) => pref.set('auto', e.target.checked));
  if (pref.get('nopanel', false)) $('#layout').classList.add('nopanel');
  setBusy(false);

  await refreshStatus();
  await Promise.all([loadModels(), loadPersonas(), loadVoices()].map((p) => p.catch(() => {})));
  await loadHistory();
  refreshPanels();
  setInterval(() => { if (!S.busy && !document.hidden) refreshStatus(); }, 6000);
  input.focus();
}
init();
