namespace NatureFX.Tools {
	// The debug page's HTML (DebugPage.cs serves it at /). Self contained: no scripts or fonts from the internet.
	// Written without double quotes, so the C# verbatim string needs no escaping.
	internal static class Page {
		public const string Html = @"<!doctype html>
<html lang='ja'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>NatureFX debug</title>
<style>
:root { --bg:#14161a; --card:#1d2026; --line:#2c3038; --text:#e6e8eb; --dim:#9aa1ab; --accent:#4cc38a; --warn:#e5b454; --bad:#e5645a;
        --c1:#4c8dff; --c2:#4cc38a; --c3:#e5b454; --c4:#c678dd; --c5:#e5645a; --c6:#56b6c2; }
@media (prefers-color-scheme: light) { :root { --bg:#f3f4f6; --card:#ffffff; --line:#dde0e5; --text:#1d2026; --dim:#5f6670; } }
* { box-sizing:border-box; }
body { margin:0; padding:16px; background:var(--bg); color:var(--text); font:13px/1.45 system-ui, 'Segoe UI', sans-serif; }
h1 { font-size:16px; margin:0 0 12px; display:flex; gap:12px; align-items:baseline; flex-wrap:wrap; }
h1 small { color:var(--dim); font-weight:normal; }
h2 { font-size:13px; margin:0 0 8px; color:var(--dim); text-transform:uppercase; letter-spacing:.04em; }
.grid { display:grid; grid-template-columns:repeat(auto-fit, minmax(360px, 1fr)); gap:12px; }
.card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px; min-width:0; }
.wide { grid-column:1 / -1; }
.kpis { display:flex; gap:24px; flex-wrap:wrap; }
.kpi b { display:block; font-size:22px; font-variant-numeric:tabular-nums; }
.kpi span { color:var(--dim); }
table { width:100%; border-collapse:collapse; font-variant-numeric:tabular-nums; }
td { padding:3px 4px; border-bottom:1px solid var(--line); vertical-align:top; }
td.n { text-align:right; white-space:nowrap; }
td.bar { width:35%; }
.b { height:8px; border-radius:4px; background:var(--c1); min-width:1px; }
.part td:first-child { padding-left:18px; color:var(--dim); }
.stack { display:flex; height:18px; border-radius:4px; overflow:hidden; margin:8px 0; background:var(--line); }
.stack div { height:100%; }
.legend { display:flex; flex-wrap:wrap; gap:10px; color:var(--dim); }
.legend i { display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:4px; vertical-align:-1px; }
canvas { width:100%; height:160px; display:block; }
pre { margin:0; max-height:360px; overflow:auto; white-space:pre-wrap; word-break:break-all; font:12px/1.4 ui-monospace, Consolas, monospace; color:var(--dim); }
.note { color:var(--dim); font-size:12px; margin-top:6px; }
.err { color:var(--bad); }
button { background:var(--line); color:var(--text); border:0; border-radius:4px; padding:4px 10px; cursor:pointer; }
</style>
</head>
<body>
<h1>NatureFX debug <small id='state'>connecting...</small></h1>
<div class='grid'>
  <div class='card wide'>
    <div class='kpis'>
      <div class='kpi'><b id='fps'>-</b><span>fps</span></div>
      <div class='kpi'><b id='gpu'>-</b><span>GPU memory (MB) of the budget</span></div>
      <div class='kpi'><b id='ours'>-</b><span>NatureFX counted (MB)</span></div>
      <div class='kpi'><b id='res'>-</b><span>camera</span></div>
    </div>
    <div class='stack' id='stack'></div>
    <div class='legend' id='legend'></div>
    <div class='note'>The rest of the GPU memory is the game and HDRP (their own render targets are listed below as other render targets), and what is not counted here (DLSS).</div>
  </div>
  <div class='card wide'>
    <h2>GPU memory over time (MB)</h2>
    <canvas id='chart' width='1200' height='160'></canvas>
    <div class='note'>Kept while this page is open (the last 30 minutes). <button id='clear'>clear</button></div>
  </div>
  <div class='card'><h2>Own engine (NFXNative)</h2><table id='native'></table><div class='note' id='nativeNote'></div></div>
  <div class='card'><h2>Trace buffers</h2><table id='buffers'></table></div>
  <div class='card'><h2>NatureFX render targets and textures</h2><table id='targets'></table></div>
  <div class='card'><h2>Other render targets (game, HDRP)</h2><table id='other'></table></div>
  <div class='card'><h2>GPU time</h2><table id='timings'></table><div class='note' id='timingNote'></div></div>
  <div class='card'><h2>Status</h2><table id='status'></table></div>
  <div class='card wide'><h2>Log (NatureFX lines of kino/output.log) <button id='reload'>reload</button></h2><pre id='log'></pre></div>
</div>
<script>
const MB = 1048576;
const colors = ['var(--c1)', 'var(--c2)', 'var(--c3)', 'var(--c4)', 'var(--c5)', 'var(--c6)'];
let history = [];
const el = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
const mb = b => (b / MB).toFixed(1);

function rows(id, items, value, label) {
  const max = Math.max(1, ...items.map(value));
  el(id).innerHTML = items.map(i => `<tr class='${i.part ? 'part' : ''}'><td>${esc(label(i))}</td><td class='n'>${i.text !== undefined ? i.text : mb(value(i))}</td>` +
    `<td class='bar'><div class='b' style='width:${(100 * value(i) / max).toFixed(1)}%'></div></td></tr>`).join('') || '<tr><td>-</td></tr>';
}

function draw() {
  const c = el('chart'), g = c.getContext('2d');
  const w = c.width = c.clientWidth * devicePixelRatio, h = c.height = 160 * devicePixelRatio;
  g.clearRect(0, 0, w, h);
  if (history.length < 2) return;
  const style = getComputedStyle(document.body);
  const max = Math.max(...history.map(p => Math.max(p.used, p.budget))) * 1.05;
  const x = i => i / (history.length - 1) * w, y = v => h - v / max * h;
  g.lineWidth = 2 * devicePixelRatio;
  for (const [key, color] of [['budget', style.getPropertyValue('--warn')], ['used', style.getPropertyValue('--c1')], ['ours', style.getPropertyValue('--c2')]]) {
    g.strokeStyle = color; g.beginPath();
    history.forEach((p, i) => i ? g.lineTo(x(i), y(p[key])) : g.moveTo(x(i), y(p[key])));
    g.stroke();
  }
  g.fillStyle = style.getPropertyValue('--dim'); g.font = `${11 * devicePixelRatio}px system-ui`;
  g.fillText(`${Math.round(max)} MB   blue used, green NatureFX counted, yellow budget`, 6 * devicePixelRatio, 14 * devicePixelRatio);
}

function show(d) {
  if (d.error) { el('state').innerHTML = `<span class='err'>${esc(d.error)}</span>`; return; }
  el('state').textContent = 'live, ' + new Date().toLocaleTimeString();
  el('fps').textContent = d.fps.toFixed(0);
  el('gpu').textContent = `${d.gpuUsedMB} / ${d.gpuBudgetMB}`;
  el('res').textContent = d.resolution;
  // native: reserved pools count, their used parts are shown as parts
  const native = d.native.map((n, i) => ({ name: n.name, bytes: n.bytes, part: n.name.includes('(used)') }));
  const nativeTotal = native.filter(n => !n.part).reduce((a, n) => a + n.bytes, 0);
  const buffers = d.buffers.reduce((a, n) => a + n.bytes, 0);
  const targets = d.targets.reduce((a, n) => a + n.bytes, 0);
  const ours = nativeTotal + buffers + targets;
  el('ours').textContent = mb(ours);
  const groups = [['own engine', nativeTotal], ['trace buffers', buffers], ['NatureFX targets / textures', targets],
                  ['other render targets', d.otherTargets.bytes],
                  ['rest (game, HDRP, DLSS...)', Math.max(0, d.gpuUsedMB * MB - ours - d.otherTargets.bytes)]];
  const total = Math.max(1, d.gpuBudgetMB * MB, d.gpuUsedMB * MB);
  el('stack').innerHTML = groups.map((g, i) => `<div title='${g[0]} ${mb(g[1])} MB' style='width:${(100 * g[1] / total).toFixed(2)}%;background:${colors[i]}'></div>`).join('');
  el('legend').innerHTML = groups.map((g, i) => `<span><i style='background:${colors[i]}'></i>${g[0]} ${mb(g[1])} MB</span>`).join('');
  rows('native', native, n => n.bytes, n => n.name);
  el('nativeNote').textContent = d.nativeNote;
  rows('buffers', d.buffers.slice().sort((a, b) => b.bytes - a.bytes), n => n.bytes, n => n.name);
  rows('targets', d.targets, n => n.bytes, n => `${n.name} (${n.count})`);
  rows('other', [{ name: `all (${d.otherTargets.count})`, bytes: d.otherTargets.bytes }].concat(d.bigOtherTargets), n => n.bytes, n => n.name);
  rows('timings', d.timings.map(t => ({ name: t.name, ms: t.ms, part: t.part, text: t.ms.toFixed(2) + ' ms' })), t => t.ms, t => t.name);
  el('timingNote').textContent = d.timings.length ? `total ${d.timingTotal.toFixed(2)} ms` : 'GPU stage timings are not reported on this GPU / API';
  el('status').innerHTML = d.status.map(s => `<tr><td>${esc(s.name)}</td><td>${esc(s.value)}</td></tr>`).join('');
  history.push({ used: d.gpuUsedMB, budget: d.gpuBudgetMB, ours: ours / MB });
  if (history.length > 1800) history.shift();
  draw();
}

async function poll() {
  try {
    const r = await fetch('/stats.json', { cache: 'no-store' });
    show(await r.json());
  } catch (e) {
    el('state').innerHTML = `<span class='err'>no connection (is the game running with the debug page on?)</span>`;
  }
  setTimeout(poll, 1000);
}

async function log() {
  try { const r = await fetch('/log', { cache: 'no-store' }); const p = el('log'); p.textContent = await r.text(); p.scrollTop = p.scrollHeight; }
  catch (e) { el('log').textContent = '(no connection)'; }
}

el('clear').onclick = () => { history = []; draw(); };
el('reload').onclick = log;
addEventListener('resize', draw);
poll();
log();
setInterval(log, 10000);
</script>
</body>
</html>
";
	}
}
