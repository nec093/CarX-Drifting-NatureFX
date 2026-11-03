namespace NatureFX.Tools {
	// The debug page's HTML (DebugPage.cs serves it at /). Self contained but for its fonts: Noto Sans JP and Noto Sans
	// Mono from Google Fonts (an installed copy first; offline, Windows' own Japanese fonts).
	// Written without double quotes, so the C# verbatim string needs no escaping. Plain on purpose: a dense,
	// monospaced tool page (tables, rules, charts), colour only for warnings. Tabs down the left (overview,
	// performance, memory, scene, developer, log), each a grid of panels as wide as the window allows, so little
	// scrolling; the developer tab holds the switches and tools that were the F8 window's Developer tab (DevControls).
	internal static class Page {
		public const string Html = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>NatureFX debug</title>
<link rel='preconnect' href='https://fonts.googleapis.com'>
<link rel='preconnect' href='https://fonts.gstatic.com' crossorigin>
<link rel='stylesheet' href='https://fonts.googleapis.com/css2?family=Noto+Sans+JP:wght@400;700&family=Noto+Sans+Mono:wght@400;700&display=swap'>
<style>
:root { --bg:#111; --fg:#d4d4d4; --dim:#808080; --rule:#333; --head:#1a1a1a; --bar:#5a5a5a; --sel:#262626; --warn:#d7ba7d; --bad:#f48771; --a:#9cdcfe; --b:#b5cea8; --c:#d7ba7d; --on:#4ec9b0; }
@media (prefers-color-scheme: light) { :root { --bg:#fff; --fg:#1e1e1e; --dim:#6e6e6e; --rule:#d0d0d0; --head:#f3f3f3; --bar:#b0b0b0; --sel:#e8e8e8; --warn:#9a6700; --bad:#c42b1c; --a:#0451a5; --b:#267f00; --c:#9a6700; --on:#007a65; } }
* { box-sizing:border-box; }
html, body { margin:0; height:100%; background:var(--bg); color:var(--fg); }
body { font:12.5px/1.5 'Noto Sans JP', 'Noto Sans', 'BIZ UDPGothic', 'Yu Gothic UI', Meiryo, sans-serif; font-variant-numeric:tabular-nums; display:grid; grid-template-columns:120px 1fr; grid-template-rows:auto 1fr; }
#bar { grid-column:1 / 3; position:sticky; top:0; background:var(--bg); border-bottom:1px solid var(--rule); padding:6px 12px; display:flex; flex-wrap:wrap; gap:4px 16px; z-index:2; }
#bar b { font-weight:normal; color:var(--dim); }
#bar a { color:var(--dim); }
#bar { align-items:center; }
#bar button { padding:0 8px; line-height:16px; }
#pause.on { color:var(--bg); background:var(--warn); border-color:var(--warn); }
#tabs { border-right:1px solid var(--rule); padding:8px 0; position:sticky; top:31px; align-self:start; }
#tabs button { display:block; width:100%; text-align:left; border:0; background:none; color:var(--dim); padding:4px 12px; font:inherit; cursor:pointer; }
#tabs button:hover { color:var(--fg); }
#tabs button.cur { color:var(--fg); background:var(--sel); border-left:2px solid var(--a); padding-left:10px; }
main { min-width:0; padding:0 12px 24px; }
.tab { display:none; }
.tab.cur { display:grid; grid-template-columns:repeat(auto-fill, minmax(480px, 1fr)); gap:12px; align-items:start; padding-top:12px; }
.wide { grid-column:1 / -1; }
section { min-width:0; }
h2 { font-size:12px; font-weight:bold; margin:0; padding:2px 4px; background:var(--head); border:1px solid var(--rule); border-bottom:0; }
table { width:100%; border-collapse:collapse; table-layout:fixed; border:1px solid var(--rule); }
td { padding:1px 4px; border-top:1px solid var(--rule); vertical-align:top; overflow-wrap:anywhere; }
td.k { width:42%; color:var(--dim); }
td.k.i { padding-left:16px; }
td.v.n { text-align:right; white-space:nowrap; width:12ch; }
td.g { width:24%; }
td.g div { height:7px; margin-top:4px; background:var(--bar); }
.scroll { max-height:420px; overflow:auto; border-bottom:1px solid var(--rule); }
.scroll table { border-bottom:0; }
#events, #spikes { table-layout:auto; }
#spikes td, #events td { white-space:nowrap; }
#spikes td.s, #events td.s { white-space:normal; }
#spikes td.n, #events td.n { text-align:right; }
#spikes td.up, #events td.up { color:var(--warn); }
#spikes tr.head td, #events tr.head td { color:var(--dim); }
tr.l1 td.v { color:var(--warn); }
tr.l2 td.v { color:var(--bad); }
.off { color:var(--dim); }
canvas { display:block; width:100%; height:180px; border:1px solid var(--rule); border-top:0; }
.tools { display:flex; gap:12px; align-items:center; padding:2px 4px; border:1px solid var(--rule); border-top:0; color:var(--dim); }
.tools input[type=text] { font:inherit; background:var(--bg); color:var(--fg); border:1px solid var(--rule); padding:0 4px; width:24ch; }
button { font:inherit; background:var(--head); color:var(--fg); border:1px solid var(--rule); padding:1px 8px; cursor:pointer; }
button:hover { border-color:var(--dim); }
pre { margin:0; padding:4px; height:calc(100vh - 120px); overflow:auto; border:1px solid var(--rule); border-top:0; white-space:pre-wrap; overflow-wrap:anywhere; }
.err { color:var(--bad); }
.ctl { border:1px solid var(--rule); border-top:0; }
.row { display:grid; grid-template-columns:1fr auto; gap:2px 8px; padding:4px; border-top:1px solid var(--rule); align-items:center; }
.row:first-child { border-top:0; }
.row .help, .row .state { grid-column:1 / 3; color:var(--dim); }
.row .state { color:var(--fg); overflow-wrap:anywhere; }
.row label { cursor:pointer; }
.row.chg { box-shadow:inset 3px 0 0 var(--warn); background:color-mix(in srgb, var(--warn) 8%, transparent); }
.row.chg > :first-child { color:var(--warn); font-weight:bold; }
.row .def { grid-column:1 / 3; color:var(--dim); display:flex; gap:8px; align-items:center; }
.row .def button { padding:0 6px; }
.row .def button[hidden] { display:none; }
.row .pn { color:var(--dim); font-weight:normal; margin-left:6px; font-size:90%; }
section .ghelp { padding:4px; color:var(--dim); border:1px solid var(--rule); border-top:0; }
#lang { margin-left:auto; }
/* Noto Sans JP for the texts (its digits tabular), Noto Sans Mono for values typed in, tables' numbers and the log */
.n, td.v, pre, input[type=number], .srow .dv, #bar span span { font-family:'Noto Sans Mono', 'Cascadia Mono', Consolas, 'BIZ UDGothic', monospace; }
input, button, select { font:inherit; }
/* ---- the settings tab: the F8 window's tabs on the left, one tab's cards on the right, a row a setting */
#tab-settings.cur { display:grid; grid-template-columns:200px minmax(0, 920px); gap:16px; align-items:start; padding-top:12px; }
.snav { position:sticky; top:44px; border:1px solid var(--rule); }
.snav button { display:flex; justify-content:space-between; width:100%; text-align:left; border:0; border-top:1px solid var(--rule); background:none; color:var(--dim); padding:5px 8px; cursor:pointer; }
.snav button:first-child { border-top:0; }
.snav button:hover { color:var(--fg); }
.snav button.cur { color:var(--fg); background:var(--sel); box-shadow:inset 2px 0 0 var(--a); }
.snav .n { color:var(--warn); }
.spane { min-width:0; display:grid; gap:12px; }
.stop { display:flex; gap:12px; align-items:center; flex-wrap:wrap; }
.stop input[type=search] { flex:1; min-width:24ch; background:var(--bg); color:var(--fg); border:1px solid var(--rule); padding:3px 6px; }
.stop .cnt { color:var(--warn); }
.stop .hid { color:var(--dim); }
.srow[data-av='0'] { opacity:.5; }
.scard { border:1px solid var(--rule); }
.shead { background:var(--head); padding:4px 8px; font-weight:bold; display:flex; gap:8px; align-items:center; }
.shead .tabname { color:var(--dim); font-weight:normal; }
.srow { display:grid; grid-template-columns:minmax(0, 1fr) 330px 150px; gap:8px; align-items:center; padding:3px 8px; border-top:1px solid var(--rule); min-height:28px; }
.srow .lab { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.srow .lab.tip { text-decoration:underline dotted var(--dim); text-underline-offset:3px; cursor:help; }
.srow .ctrl { display:flex; justify-content:flex-end; align-items:center; gap:6px; }
.srow .ctrl input[type=range] { width:190px; }
.srow input.num { width:10ch; background:var(--sel); color:var(--fg); border:1px solid var(--rule); padding:1px 4px; }
input[type=range] { accent-color:var(--a); }
.srow .dv { color:var(--dim); text-align:right; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
.srow .dv button { padding:0 6px; color:var(--warn); border-color:var(--warn); }
.srow.chg { box-shadow:inset 3px 0 0 var(--warn); background:color-mix(in srgb, var(--warn) 7%, transparent); }
.srow.chg .lab { color:var(--warn); }
.srow.chg .dv .d { display:none; }
.srow:not(.chg) .dv button { display:none; }
.q { color:var(--dim); border:1px solid var(--rule); border-radius:50%; width:15px; height:15px; display:inline-flex; align-items:center; justify-content:center; font-weight:normal; font-size:10px; cursor:help; }
.sempty { color:var(--dim); padding:12px; border:1px solid var(--rule); }
#tip { position:fixed; z-index:9; max-width:440px; background:var(--head); color:var(--fg); border:1px solid var(--bar); padding:6px 8px; pointer-events:none; white-space:pre-line; display:none; box-shadow:0 2px 8px rgba(0,0,0,.35); }
#tip .tn { color:var(--dim); display:block; margin-top:4px; }
.sw { appearance:none; width:30px; height:15px; border:1px solid var(--dim); border-radius:8px; position:relative; cursor:pointer; margin:0; }
.sw::after { content:''; position:absolute; top:2px; left:2px; width:9px; height:9px; border-radius:50%; background:var(--dim); transition:left .1s; }
.sw:checked { border-color:var(--on); }
.sw:checked::after { left:17px; background:var(--on); }
.sl { display:flex; gap:6px; align-items:center; }
.sl input { width:180px; }
.sl span { min-width:6ch; text-align:right; }
.sl input.num { width:9ch; background:var(--sel); color:var(--fg); border:1px solid var(--rule); font:inherit; padding:1px 4px; }
.chips button { margin-left:2px; color:var(--dim); }
.chips button.cur { color:var(--fg); border-color:var(--a); }
.kv { grid-column:1 / 3; display:grid; grid-template-columns:24ch 1fr; gap:8px; }
.kv span:first-child { color:var(--dim); }
</style>
</head>
<body>
<div id='bar'>
  <span id='conn'>[connecting]</span>
  <span><b>fps</b> <span id='fps'>-</span></span>
  <span><b>frame</b> <span id='frame'>-</span></span>
  <span><b>engine gpu</b> <span id='egpu'>-</span></span>
  <span><b>gpu busy</b> <span id='busy'>-</span></span>
  <span><b>vram</b> <span id='gpu'>-</span></span>
  <span><b>nfx</b> <span id='ours'>-</span></span>
  <span><b>t</b> <span id='t'>-</span></span>
  <button id='pause' title='stop reading the game (the page keeps what it has)'>pause</button>
  <span><a href='/stats.json' target='_blank'>stats.json</a></span>
  <button id='lang' title='English / 日本語'>EN / JP</button>
</div>
<nav id='tabs'></nav>
<div id='tip'></div>
<main>
  <div class='tab' id='tab-overview'>
    <section class='wide' id='s-chart'>
      <h2>gpu memory, MB (<span style='color:var(--a)'>used</span> / <span style='color:var(--c)'>budget</span> / <span style='color:var(--b)'>nfx counted</span>)</h2>
      <canvas id='chart'></canvas>
      <div class='tools'><span id='range'>-</span><button id='clear'>clear</button></div>
    </section>
    <section class='wide' id='s-load-chart'>
      <h2>load (<span style='color:var(--a)'>frame ms</span> / <span style='color:var(--b)'>own engine GPU ms</span>, left; <span style='color:var(--c)'>GPU busy %</span>, right)</h2>
      <canvas id='chart2'></canvas>
    </section>
  </div>
  <div class='tab' id='tab-performance'>
    <section class='wide' id='s-spikes'>
      <h2>slow frames (longer than 25 ms or 1.6 x the average; NatureFX's main thread parts in that frame, ms)</h2>
      <div class='scroll'><table id='spikes'></table></div>
    </section>
  </div>
  <div class='tab' id='tab-memory'>
    <section class='wide' id='s-events'>
      <h2>events (memory at each step after a scene load; deltas against the step before, MB)</h2>
      <div class='scroll'><table id='events'></table></div>
    </section>
  </div>
  <div class='tab' id='tab-scene'></div>
  <div class='tab' id='tab-settings'></div>
  <div class='tab' id='tab-developer'></div>
  <div class='tab' id='tab-log'>
    <section class='wide' id='s-log'>
      <h2>log (NatureFX lines of kino/output.log)</h2>
      <div class='tools'><label>filter <input type='text' id='filter'></label><label><input type='checkbox' id='follow' checked> follow</label><button id='reload'>reload</button><span id='logInfo'></span></div>
      <pre id='log'></pre>
    </section>
  </div>
</main>
<script>
const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', ""'"": '&#39;' }[c]));
let hist = [], logText = '', built = '', marks = [], ctlBuilt = '', paused = false;
// the samples: one a second; { off: true } where the game did not answer (drawn as a red band, the lines broken there)
const live = () => hist.filter(p => !p.off);

// the tabs, and which tab each of the game's sections goes to (anything new: scene)
const TABS = [['overview', 'overview'], ['performance', 'performance'], ['memory', 'memory'], ['scene', 'scene'], ['settings', 'settings'], ['developer', 'developer'], ['log', 'log']];
const HOME = { perf: 'overview', load: 'overview', 'gpu-time': 'overview', cpu: 'performance', timings: 'performance',
  split: 'memory', 'engine-memory': 'memory', buffers: 'memory', targets: 'memory', 'other-targets': 'memory', 'game-textures': 'memory',
  meshes: 'memory', 'switched-textures': 'memory' };
const LONG = { 'game-textures': 1, 'other-targets': 1, targets: 1, 'switched-textures': 1, hdrp: 1 };

function pick(name) {
  for (const [id] of TABS) {
    $('tab-' + id).classList.toggle('cur', id === name);
    const b = $('tb-' + id); if (b) b.classList.toggle('cur', id === name);
  }
  try { localStorage.setItem('nfxTab', name); } catch (e) {}
  if (name === 'overview') { chart(); chart2(); }
  if (name === 'log') showLog();
}
// the language of the tabs and the settings tab (EN / JP, kept in this browser); the texts from /meta.json
let LANG = 'en', META = null;
try { LANG = localStorage.getItem('nfxLang') || ((navigator.language || '').startsWith('ja') ? 'ja' : 'en'); } catch (e) {}
const T = k => (LANG === 'ja' && META && META.ja.ui && META.ja.ui[k]) || ({ settingsTitle: 'settings (the F8 window\'s, saved as they change)', typeHint: 'a value outside a slider\'s range: type it in the box',
  filter: 'filter', changedOnly: 'changed only', default: 'default', resetToDefault: 'reset', changed: 'changed', other: 'Other settings (no plain F8 control)',
  changedCount: 'changed from the defaults', changedTab: 'Changed', search: 'search every setting (name, description)',
  noMatch: 'nothing matches', noneChanged: 'every setting is at its default', on: 'on', off: 'off', hiddenCount: 'hidden: what they need is off' })[k] || k;
function tabLabels() { document.documentElement.lang = LANG; for (const [id, label] of TABS) { const b = $('tb-' + id); if (b) b.textContent = T(id) === id ? label : T(id); } $('lang').textContent = LANG === 'ja' ? '日本語 / EN' : 'EN / 日本語'; }
$('tabs').innerHTML = TABS.map(([id, label]) => `<button id='tb-${id}'>${label}</button>`).join('');
for (const [id] of TABS) $('tb-' + id).onclick = () => pick(id);
$('lang').onclick = () => { LANG = LANG === 'ja' ? 'en' : 'ja'; try { localStorage.setItem('nfxLang', LANG); } catch (e) {} ctlKeys.settings = ''; tabLabels(); if (lastControls) controls(lastControls); };
fetch('/meta.json', { cache: 'no-store' }).then(r => r.json()).then(m => { META = m; ctlKeys.settings = ''; tabLabels(); if (lastControls) controls(lastControls); }).catch(() => {});
tabLabels();
let first = 'overview';
try { first = localStorage.getItem('nfxTab') || 'overview'; } catch (e) {}
if (!TABS.some(t => t[0] === first)) first = 'overview';
pick(first);

function events() {
  const keys = [['gpu', 'gpu'], ['native', 'engine'], ['buffers', 'trace buf'], ['targets', 'nfx targets'], ['other', 'other RT'], ['textures', 'game tex'], ['meshes', 'meshes']];
  const MB = 1048576;
  let html = `<tr class='head'><td>#</td><td>t (s)</td><td class='s'>step</td>` + keys.map(k => `<td class='n'>${k[1]}</td>`).join('') + '</tr>';
  marks.forEach((m, i) => {
    const p = i > 0 ? marks[i - 1] : null;
    html += `<tr><td>${i + 1}</td><td>${m.t}</td><td class='s'>${esc(m.label)}</td>` + keys.map(k => {
      const v = m[k[0]] / MB;
      if (!p) return `<td class='n'>${v.toFixed(0)}</td>`;
      const dv = (m[k[0]] - p[k[0]]) / MB;
      const big = Math.abs(dv) >= 32;
      return `<td class='n${dv >= 32 ? ' up' : ''}'>${v.toFixed(0)} ${big ? (dv > 0 ? '+' : '') + dv.toFixed(0) : ''}</td>`;
    }).join('') + '</tr>';
  });
  $('events').innerHTML = marks.length ? html : `<tr><td class='k'>no steps yet: they are recorded while the debug page is on (load a map)</td></tr>`;
}

function table(sec) {
  const bars = sec.rows.filter(r => r[2] >= 0);
  const max = Math.max(1e-9, ...bars.map(r => r[2]));
  const hasBar = bars.length > 0;
  const rows = sec.rows.map(r => {
    const [k, v, bar, indent, level] = r;
    const numeric = bar >= 0;
    const cls = v === 'off' ? 'off' : '';
    return `<tr class='l${level}'><td class='k${indent ? ' i' : ''}'>${esc(k)}</td>` +
      `<td class='v${numeric ? ' n' : ''} ${cls}'>${esc(v)}</td>` +
      (hasBar ? `<td class='g'>${numeric ? `<div style='width:${(100 * bar / max).toFixed(1)}%'></div>` : ''}</td>` : '') + '</tr>';
  }).join('');
  const body = `<table>${rows || `<tr><td class='k'>-</td></tr>`}</table>`;
  return `<h2>${esc(sec.title)}</h2>` + (LONG[sec.id] ? `<div class='scroll'>${body}</div>` : body);
}

// ---- the developer tab: the game's controls (DevControls), built once, their values kept up to date except the one
// being handled
function send(id, v) { fetch(`/set?id=${encodeURIComponent(id)}&v=${v}`, { cache: 'no-store' }).catch(() => {}); }

// a setting's texts in the page's language: label, help, the card's name and help
function text(c) {
  const pick = l => META && META[l] && META[l].settings && META[l].settings[c.name];
  const t = (LANG === 'ja' && pick('ja')) || pick('en') || {};
  return { label: t.label || c.label, help: t.help || c.help || '' };
}
function groupText(name) {
  if (name === 'Other') return { name: T('other'), help: '' };
  const pick = l => META && META[l] && META[l].groups && META[l].groups[name];
  const t = (LANG === 'ja' && pick('ja')) || pick('en') || {};
  return { name: t.name || name, help: t.help || '' };
}
const fmt = v => { const n = +v; return Math.abs(n) >= 100 ? n.toFixed(0) : (+n.toFixed(4)).toString(); };
function defText(c) {
  if (c.def === undefined) return '';
  if (c.kind === 'switch') return c.def > 0.5 ? T('on') : T('off');
  if (c.kind === 'chips') return c.options[Math.round(c.def)] ?? fmt(c.def);
  return fmt(c.def);
}
const isChanged = c => c.def !== undefined && Math.abs(c.value - c.def) > 1e-4 * Math.max(1, Math.abs(c.def));

let lastControls = null;
function controls(list) {
  lastControls = list;
  for (const tab of ['developer', 'settings']) build(tab, list.filter(c => (c.tab || 'developer') === tab));
  let changed = 0;
  for (const c of list) {
    const e = $('c-' + c.id), st = $('st-' + c.id), n = $('cn-' + c.id), row = $('r-' + c.id);
    if (st) st.textContent = c.text || '';
    if (row && c.def !== undefined) {
      const ch = isChanged(c);
      changed += ch ? 1 : 0;
      row.classList.toggle('chg', ch);
      row.dataset.chg = ch ? '1' : '';
      row.dataset.av = c.avail === 0 ? '0' : '1';
      const r = $('rs-' + c.id); if (r) r.hidden = !ch;
    }
    if (n && n !== document.activeElement) n.value = fmt(c.value);
    if (!e || e === document.activeElement && (c.kind === 'slider' || c.kind === 'number')) continue;
    if (c.kind === 'switch') e.checked = c.value > 0.5;
    if (c.kind === 'slider') e.value = c.value;
    if (c.kind === 'number') e.value = fmt(c.value);
    if (c.kind === 'chips') e.querySelectorAll('button').forEach(b => b.classList.toggle('cur', +b.dataset.i === Math.round(c.value)));
  }
  const cc = $('setCount'); if (cc) cc.textContent = changed ? `${changed} ${T('changedCount')}` : '';
  filterSettings();
}

// ---- the settings tab
let setView = 'changed', setQuery = '';
try { setView = localStorage.getItem('nfxSetView') || 'changed'; } catch (e) {}
const tabOf = c => c.group === 'Other' ? 'Other' : c.group.split(' / ')[0];
const cardOf = c => c.group === 'Other' ? '' : c.group.split(' / ').slice(1).join(' / ');

// the tooltip: a setting's description where the pointer is (data-tip on the element)
function tipAt(ev) {
  const t = ev.target.closest && ev.target.closest('[data-tip]'), tip = $('tip');
  if (!t || !t.dataset.tip) { tip.style.display = 'none'; return; }
  tip.innerHTML = esc(t.dataset.tip) + (t.dataset.tn ? `<span class='tn'>${esc(t.dataset.tn)}</span>` : '');
  tip.style.display = 'block';
  const w = tip.offsetWidth, h = tip.offsetHeight;
  let x = ev.clientX + 14, y = ev.clientY + 16;
  if (x + w > innerWidth - 8) x = Math.max(8, innerWidth - w - 8);
  if (y + h > innerHeight - 8) y = Math.max(8, ev.clientY - h - 10);
  tip.style.left = x + 'px'; tip.style.top = y + 'px';
}
document.addEventListener('mousemove', tipAt);
document.addEventListener('scroll', () => $('tip').style.display = 'none', true);

function buildSettings(host, list) {
  const tabs = [];
  for (const c of list) { const t = tabOf(c); if (!tabs.includes(t)) tabs.push(t); }
  const tabName = t => t === 'Other' ? T('other') : (groupText(t + ' / ').name.split(' / ')[0] || t);
  // a tab's Japanese name: from the first card's translated group name
  const tabJa = {};
  for (const c of list) { const t = tabOf(c); if (!tabJa[t] && c.group !== 'Other') tabJa[t] = groupText(c.group).name.split(' / ')[0]; }
  const nav = `<button data-v='changed'><span>${esc(T('changedTab'))}</span><span class='n' id='sn-changed'></span></button>` +
    tabs.map(t => `<button data-v='${esc(t)}'><span>${esc(t === 'Other' ? T('other') : tabJa[t] || t)}</span><span class='n' id='sn-${esc(t)}'></span></button>`).join('');
  const cards = [];
  for (const c of list) { let k = cards.find(x => x.group === c.group); if (!k) cards.push(k = { group: c.group, tab: tabOf(c), items: [] }); k.items.push(c); }
  const body = cards.map(k => {
    const gt = groupText(k.group);
    const parts = gt.name.split(' / ');
    const cardName = k.group === 'Other' ? T('other') : (parts.slice(1).join(' / ') || parts[0]);
    const q = gt.help ? ` <span class='q' data-tip='${esc(gt.help)}'>?</span>` : '';
    return `<div class='scard' data-tab='${esc(k.tab)}'><div class='shead'><span>${esc(cardName)}</span>${q}<span class='tabname'>${esc(k.group === 'Other' ? '' : parts[0])}</span></div>` +
      k.items.map(c => {
        const t = text(c);
        const tipAttr = t.help ? ` data-tip='${esc(t.help)}' data-tn='${esc(c.name || '')}'` : ` data-tip='' `;
        let ctrl = '';
        if (c.kind === 'switch') ctrl = `<input type='checkbox' class='sw' id='c-${c.id}'>`;
        else if (c.kind === 'slider') ctrl = `<input type='range' id='c-${c.id}' min='${c.min}' max='${c.max}' step='${c.step}'><input type='number' class='num' id='cn-${c.id}' step='any'>`;
        else if (c.kind === 'number') ctrl = `<input type='number' class='num' id='c-${c.id}' step='${c.step > 0 ? c.step : 'any'}'>`;
        else if (c.kind === 'chips') ctrl = `<span class='chips' id='c-${c.id}'>` + c.options.map((o, i) => `<button data-i='${i}'>${esc(o)}</button>`).join('') + `</span>`;
        const dv = c.def !== undefined ? `<span class='d'>${esc(T('default'))} ${esc(defText(c))}</span><button id='rs-${c.id}' title='${esc(T('resetToDefault'))}'>&#8634; ${esc(defText(c))}</button>` : '';
        const key = (t.label + ' ' + (c.name || '') + ' ' + c.label + ' ' + t.help + ' ' + k.group + ' ' + gt.name).toLowerCase();
        return `<div class='srow' id='r-${c.id}' data-k='${esc(key)}'><span class='lab${t.help ? ' tip' : ''}'${tipAttr}>${esc(t.label)}</span><span class='ctrl'>${ctrl}</span><span class='dv'>${dv}</span></div>`;
      }).join('') + `</div>`;
  }).join('');
  host.innerHTML = `<nav class='snav'>${nav}</nav><div class='spane'><div class='stop'><input type='search' id='setFilter' placeholder='${esc(T('search'))}'>` +
    `<span class='cnt' id='setCount'></span><span class='hid' id='setHidden'></span></div>${body}<div class='sempty' id='setEmpty' hidden></div></div>`;
  host.querySelectorAll('.snav button').forEach(b => b.onclick = () => { setView = b.dataset.v; setQuery = ''; $('setFilter').value = ''; try { localStorage.setItem('nfxSetView', setView); } catch (e) {} filterSettings(); });
  $('setFilter').value = setQuery;
  $('setFilter').oninput = () => { setQuery = $('setFilter').value; filterSettings(); };
}

// which rows show: the chosen tab, the changed ones, or what the search finds in every tab
function filterSettings() {
  const host = $('tab-settings');
  if (!host || !$('setFilter')) return;
  const q = setQuery.trim().toLowerCase();
  let shown = 0, hidden = 0;
  const counts = {};
  host.querySelectorAll('.srow').forEach(r => { if (r.classList.contains('chg')) { const t = r.closest('.scard').dataset.tab; counts[t] = (counts[t] || 0) + 1; } });
  host.querySelectorAll('.scard').forEach(card => {
    let any = false;
    card.querySelectorAll('.srow').forEach(r => {
      // a setting whose prerequisite is off (the own engine's part, the switch above it) is not shown
      // (the changed list shows them all, the unusable ones faded)
      const usable = r.dataset.av !== '0';
      const want = q ? r.dataset.k.includes(q) : setView === 'changed' ? r.classList.contains('chg') : card.dataset.tab === setView;
      const on = want && (usable || setView === 'changed' && !q);
      hidden += want && !on ? 1 : 0;
      r.style.display = on ? '' : 'none'; any = any || on; shown += on ? 1 : 0;
    });
    card.style.display = any ? '' : 'none';
  });
  let total = 0;
  for (const k in counts) total += counts[k];
  host.querySelectorAll('.snav button').forEach(b => {
    b.classList.toggle('cur', !q && b.dataset.v === setView);
    const n = b.dataset.v === 'changed' ? total : counts[b.dataset.v] || 0;
    const e = b.querySelector('.n'); if (e) e.textContent = n ? n : '';
  });
  const hc = $('setHidden'); if (hc) hc.textContent = hidden ? `${hidden} ${T('hiddenCount')}` : '';
  const empty = $('setEmpty');
  empty.hidden = shown > 0;
  empty.textContent = q ? T('noMatch') : setView === 'changed' ? T('noneChanged') : '';
}

// a tab's controls, built again only when the list (or the language) changes
const ctlKeys = {};
function build(tab, list) {
  const key = list.map(c => c.id).join(',');
  const host = $('tab-' + tab);
  if (!host || key === ctlKeys[tab]) return;
  ctlKeys[tab] = key;
  if (tab === 'settings') {
    buildSettings(host, list);
  } else {
    const groups = [];
    for (const c of list) { let g = groups.find(x => x.name === c.group); if (!g) groups.push(g = { name: c.group, items: [] }); g.items.push(c); }
    host.innerHTML = groups.map(g => `<section><h2>${esc(g.name)}</h2><div class='ctl'>` + g.items.map(c => {
      const help = c.help ? `<div class='help'>${esc(c.help)}</div>` : '';
      const state = `<div class='state' id='st-${c.id}'></div>`;
      const row = `<div class='row' id='r-${c.id}'>`;
      if (c.kind === 'switch') return `${row}<label for='c-${c.id}'>${esc(c.label)}</label><input type='checkbox' class='sw' id='c-${c.id}'>${help}</div>`;
      if (c.kind === 'slider') return `${row}<span>${esc(c.label)}</span><span class='sl'><input type='range' id='c-${c.id}' min='${c.min}' max='${c.max}' step='${c.step}'><input type='number' class='num' id='cn-${c.id}' step='any'></span>${help}</div>`;
      if (c.kind === 'chips') return `${row}<span>${esc(c.label)}</span><span class='chips' id='c-${c.id}'>` + c.options.map((o, i) => `<button data-i='${i}'>${esc(o)}</button>`).join('') + `</span>${help}</div>`;
      if (c.kind === 'button') return `${row}<span></span><button id='c-${c.id}'>${esc(c.label)}</button>${help}${state}</div>`;
      return `${row}<div class='kv'><span>${esc(c.label)}</span><span id='st-${c.id}'></span></div></div>`;
    }).join('') + `</div></section>`).join('');
  }
  for (const c of list) {
    const e = $('c-' + c.id); if (!e) continue;
    const n = $('cn-' + c.id);
    if (c.kind === 'switch') e.onchange = () => send(c.id, e.checked ? 1 : 0);
    if (c.kind === 'slider') {
      e.oninput = () => { if (n) n.value = fmt(e.value); };
      e.onchange = () => send(c.id, e.value);
      if (n) n.onchange = () => { if (n.value !== '' && isFinite(+n.value)) { e.value = n.value; send(c.id, +n.value); } };
    }
    if (c.kind === 'number') e.onchange = () => { if (e.value !== '' && isFinite(+e.value)) send(c.id, +e.value); };
    if (c.kind === 'chips') e.querySelectorAll('button').forEach(b => b.onclick = () => { send(c.id, b.dataset.i); e.querySelectorAll('button').forEach(x => x.classList.toggle('cur', x === b)); });
    if (c.kind === 'button') e.onclick = () => send(c.id, 1);
    const r = $('rs-' + c.id); if (r) r.onclick = () => send(c.id, c.def);
  }
}

function show(d) {
  if (d.error) { $('conn').innerHTML = `<span class='err'>[error] ${esc(d.error)}</span>`; return; }
  $('conn').textContent = '[live]';
  $('fps').textContent = d.fps.toFixed(1);
  $('frame').textContent = `${(d.frameMs || 0).toFixed(1)} ms`;
  $('egpu').textContent = `${(d.nfxGpuMs || 0).toFixed(2)} ms`;
  $('busy').textContent = d.gpuBusy >= 0 ? `${d.gpuBusy} %` : '-';
  $('gpu').textContent = `${d.gpuUsedMB} / ${d.gpuBudgetMB} MB`;
  $('ours').textContent = `${d.oursMB} MB`;
  $('t').textContent = `${d.time}s`;
  // the section boxes once (their tabs and ids stay), their contents every time
  const ids = d.sections.map(s => s.id).join(',');
  if (ids !== built) {
    built = ids;
    document.querySelectorAll('section.gen').forEach(e => e.remove());
    for (const s of d.sections) {
      const e = document.createElement('section');
      e.id = 's-' + s.id; e.className = 'gen' + (LONG[s.id] ? ' wide' : '');
      $('tab-' + (HOME[s.id] || 'scene')).appendChild(e);
    }
  }
  for (const s of d.sections) { const e = $('s-' + s.id); if (e) e.innerHTML = table(s); }
  if (d.controls) controls(d.controls);
  hist.push({ t: d.time, u: d.gpuUsedMB, b: d.gpuBudgetMB, n: d.oursMB, f: d.frameMs || 0, e: d.nfxGpuMs || 0, g: d.gpuBusy });
  marks = d.marks || [];
  events();
  const sp = d.spikes || [];
  $('spikes').innerHTML = sp.length
    ? `<tr class='head'><td>t (s)</td><td class='n'>frame ms</td><td class='n'>GC</td><td class='s'>NatureFX parts that frame</td></tr>` +
      sp.slice().reverse().map(x => `<tr><td>${x.t}</td><td class='n'>${x.ms}</td><td class='n${x.gc > 0 ? ' up' : ''}'>${x.gc}</td><td class='s'>${esc(x.top)}</td></tr>`).join('')
    : `<tr><td class='k'>none since the page opened</td></tr>`;
  if (hist.length > 3600) hist.shift();
  chart();
  chart2();
}

function chart() {
  const c = $('chart'), g = c.getContext('2d'), r = devicePixelRatio || 1;
  if (!c.clientWidth) return;
  const w = c.width = c.clientWidth * r, h = c.height = c.clientHeight * r;
  const css = getComputedStyle(document.documentElement), col = n => css.getPropertyValue(n).trim();
  g.clearRect(0, 0, w, h);
  $('range').textContent = hist.length ? `${hist.length} s` : '-';
  if (hist.length < 2) return;
  const top = Math.max(1, ...live().map(p => Math.max(p.u, p.b))) * 1.05;
  const step = Math.pow(10, Math.floor(Math.log10(top / 4)));
  const grid = [1, 2, 5].map(m => m * step).find(s => top / s <= 6) || step * 10;
  g.font = `${10 * r}px Consolas, monospace`; g.lineWidth = 1;
  for (let v = 0; v <= top; v += grid) {
    const y = h - v / top * h;
    g.strokeStyle = col('--rule'); g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke();
    g.fillStyle = col('--dim'); g.fillText(String(Math.round(v)), 3 * r, y - 2 * r);
  }
  const x = i => i / (hist.length - 1) * w, y = v => h - v / top * h;
  // the events' times, as numbered lines
  g.fillStyle = col('--dim'); g.strokeStyle = col('--dim'); g.lineWidth = 1;
  const lv = live(); if (lv.length < 2) return;
  const t0 = lv[0].t, t1 = lv[lv.length - 1].t;
  marks.forEach((m, i) => {
    if (m.t < t0 || m.t > t1 || t1 <= t0) return;
    const mx = (m.t - t0) / (t1 - t0) * w;
    g.setLineDash([3 * r, 3 * r]); g.beginPath(); g.moveTo(mx, 0); g.lineTo(mx, h); g.stroke(); g.setLineDash([]);
    g.fillText(String(i + 1), mx + 2 * r, 24 * r + (i % 3) * 11 * r);
  });
  gaps(g, w, h, x, col);
  for (const [k, n] of [['b', '--c'], ['u', '--a'], ['n', '--b']]) {
    g.strokeStyle = col(n); g.lineWidth = 1.5 * r; g.beginPath();
    let pen = false;
    hist.forEach((p, i) => { if (p.off) { pen = false; return; } pen ? g.lineTo(x(i), y(p[k])) : g.moveTo(x(i), y(p[k])); pen = true; });
    g.stroke();
  }
}

// the seconds the game did not answer: a red band each, 'no connection' over the first of a run
function gaps(g, w, h, x, col) {
  const r = devicePixelRatio || 1, bw = Math.max(2 * r, w / Math.max(1, hist.length - 1));
  g.save(); g.fillStyle = col('--bad'); g.globalAlpha = 0.25;
  hist.forEach((p, i) => { if (p.off) g.fillRect(x(i) - bw / 2, 0, bw, h); });
  g.globalAlpha = 1; g.font = `${10 * r}px Consolas, monospace`;
  hist.forEach((p, i) => { if (p.off && (i === 0 || !hist[i - 1].off)) g.fillText('no connection', x(i) + 3 * r, h - 4 * r); });
  g.restore();
}

function chart2() {
  const c = $('chart2'), g = c.getContext('2d'), r = devicePixelRatio || 1;
  if (!c.clientWidth) return;
  const w = c.width = c.clientWidth * r, h = c.height = c.clientHeight * r;
  const css = getComputedStyle(document.documentElement), col = n => css.getPropertyValue(n).trim();
  g.clearRect(0, 0, w, h);
  if (hist.length < 2) return;
  const top = Math.max(16.7, ...live().map(p => Math.max(p.f, p.e))) * 1.1;
  g.font = `${10 * r}px Consolas, monospace`; g.lineWidth = 1;
  for (const v of [8.3, 16.7, 33.3, 50, 100].filter(v => v <= top)) {
    const y = h - v / top * h;
    g.strokeStyle = col('--rule'); g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke();
    g.fillStyle = col('--dim'); g.fillText(`${v} ms`, 3 * r, y - 2 * r);
  }
  const x = i => i / (hist.length - 1) * w;
  gaps(g, w, h, x, col);
  const line = (key, color, scale) => {
    g.strokeStyle = col(color); g.lineWidth = 1.5 * r; g.beginPath();
    let started = false;
    hist.forEach((p, i) => {
      if (p.off) { started = false; return; }
      const v = p[key];
      if (v === undefined || v < 0) return;
      const y = h - scale(v) * h;
      started ? g.lineTo(x(i), y) : g.moveTo(x(i), y);
      started = true;
    });
    g.stroke();
  };
  line('g', '--c', v => v / 100);
  line('f', '--a', v => v / top);
  line('e', '--b', v => v / top);
  g.fillStyle = col('--dim'); g.fillText('100 %', w - 40 * r, 12 * r);
}

function showLog() {
  const f = $('filter').value.toLowerCase();
  const lines = logText.split('\n').filter(l => !f || l.toLowerCase().includes(f));
  const p = $('log');
  p.textContent = lines.join('\n');
  $('logInfo').textContent = `${lines.length} lines`;
  if ($('follow').checked) p.scrollTop = p.scrollHeight;
}

async function poll() {
  if (!paused) {
    try { const r = await fetch('/stats.json', { cache: 'no-store' }); show(await r.json()); }
    catch (e) {
      $('conn').innerHTML = `<span class='err'>[no connection]</span>`;
      hist.push({ off: true });
      if (hist.length > 3600) hist.shift();
      chart(); chart2();
    }
  }
  setTimeout(poll, 1000);
}

async function loadLog() {
  try { const r = await fetch('/log', { cache: 'no-store' }); logText = await r.text(); }
  catch (e) { logText = '(no connection)'; }
  showLog();
}

$('clear').onclick = () => { hist = []; chart(); };
$('reload').onclick = loadLog;
$('filter').oninput = showLog;
addEventListener('resize', () => { chart(); chart2(); });
poll();
loadLog();
setInterval(() => { if (!paused) loadLog(); }, 5000);
$('pause').onclick = () => { paused = !paused; $('pause').classList.toggle('on', paused); $('pause').textContent = paused ? 'paused' : 'pause'; };
</script>
</body>
</html>
";
	}
}
