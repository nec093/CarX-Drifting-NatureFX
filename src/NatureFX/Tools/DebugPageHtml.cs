namespace NatureFX.Tools {
	// The debug page's HTML (DebugPage.cs serves it at /). Self contained: no scripts or fonts from the internet.
	// Written without double quotes, so the C# verbatim string needs no escaping. Plain on purpose: a dense,
	// monospaced tool page (tables, rules, one chart), colour only for warnings.
	internal static class Page {
		public const string Html = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>NatureFX debug</title>
<style>
:root { --bg:#111; --fg:#d4d4d4; --dim:#808080; --rule:#333; --head:#1a1a1a; --bar:#5a5a5a; --warn:#d7ba7d; --bad:#f48771; --a:#9cdcfe; --b:#b5cea8; --c:#d7ba7d; }
@media (prefers-color-scheme: light) { :root { --bg:#fff; --fg:#1e1e1e; --dim:#6e6e6e; --rule:#d0d0d0; --head:#f3f3f3; --bar:#b0b0b0; --warn:#9a6700; --bad:#c42b1c; --a:#0451a5; --b:#267f00; --c:#9a6700; } }
* { box-sizing:border-box; }
html, body { margin:0; background:var(--bg); color:var(--fg); }
body { font:12px/1.4 Consolas, 'Cascadia Mono', ui-monospace, Menlo, monospace; padding:0 12px 24px; }
#bar { position:sticky; top:0; background:var(--bg); border-bottom:1px solid var(--rule); padding:6px 0; display:flex; flex-wrap:wrap; gap:4px 16px; z-index:1; }
#bar b { font-weight:normal; color:var(--dim); }
#nav { padding:6px 0; border-bottom:1px solid var(--rule); display:flex; flex-wrap:wrap; gap:2px 12px; }
#nav a { color:var(--dim); text-decoration:none; }
#nav a:hover { color:var(--fg); text-decoration:underline; }
#cols { display:grid; grid-template-columns:1fr; gap:0 24px; }
@media (min-width:1100px) { #cols { grid-template-columns:1fr 1fr; } }
section { min-width:0; margin-top:14px; }
h2 { font-size:12px; font-weight:bold; margin:0; padding:2px 4px; background:var(--head); border:1px solid var(--rule); border-bottom:0; }
table { width:100%; border-collapse:collapse; table-layout:fixed; border:1px solid var(--rule); }
td { padding:1px 4px; border-top:1px solid var(--rule); vertical-align:top; overflow-wrap:anywhere; }
td.k { width:38%; color:var(--dim); }
td.k.i { padding-left:16px; }
td.v.n { text-align:right; white-space:nowrap; width:14ch; }
td.g { width:28%; }
td.g div { height:7px; margin-top:4px; background:var(--bar); }
tr.l1 td.v { color:var(--warn); }
tr.l2 td.v { color:var(--bad); }
.off { color:var(--dim); }
canvas { display:block; width:100%; height:180px; border:1px solid var(--rule); border-top:0; }
.tools { display:flex; gap:12px; align-items:center; padding:2px 4px; border:1px solid var(--rule); border-top:0; color:var(--dim); }
.tools input[type=text] { font:inherit; background:var(--bg); color:var(--fg); border:1px solid var(--rule); padding:0 4px; width:24ch; }
button { font:inherit; background:var(--head); color:var(--fg); border:1px solid var(--rule); padding:0 6px; cursor:pointer; }
pre { margin:0; padding:4px; height:420px; overflow:auto; border:1px solid var(--rule); border-top:0; white-space:pre-wrap; overflow-wrap:anywhere; }
.err { color:var(--bad); }
</style>
</head>
<body>
<div id='bar'>
  <span id='conn'>[connecting]</span>
  <span><b>fps</b> <span id='fps'>-</span></span>
  <span><b>gpu</b> <span id='gpu'>-</span></span>
  <span><b>nfx</b> <span id='ours'>-</span></span>
  <span><b>t</b> <span id='t'>-</span></span>
  <span><label><input type='checkbox' id='pause'> pause</label></span>
  <span><a href='/stats.json' target='_blank' style='color:var(--dim)'>stats.json</a></span>
</div>
<div id='nav'></div>
<section id='s-chart'>
  <h2>gpu memory, MB (<span style='color:var(--a)'>used</span> / <span style='color:var(--c)'>budget</span> / <span style='color:var(--b)'>nfx counted</span>)</h2>
  <canvas id='chart'></canvas>
  <div class='tools'><span id='range'>-</span><button id='clear'>clear</button></div>
</section>
<div id='cols'></div>
<section id='s-log'>
  <h2>log (NatureFX lines of kino/output.log)</h2>
  <div class='tools'><label>filter <input type='text' id='filter'></label><label><input type='checkbox' id='follow' checked> follow</label><button id='reload'>reload</button><span id='logInfo'></span></div>
  <pre id='log'></pre>
</section>
<script>
const $ = id => document.getElementById(id);
const esc = s => String(s).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
let hist = [], logText = '', built = '';

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
  return `<h2>${esc(sec.title)}</h2><table>${rows || `<tr><td class='k'>-</td></tr>`}</table>`;
}

function show(d) {
  if (d.error) { $('conn').innerHTML = `<span class='err'>[error] ${esc(d.error)}</span>`; return; }
  $('conn').textContent = '[live]';
  $('fps').textContent = d.fps.toFixed(1);
  $('gpu').textContent = `${d.gpuUsedMB} / ${d.gpuBudgetMB} MB`;
  $('ours').textContent = `${d.oursMB} MB`;
  $('t').textContent = `${d.time}s`;
  // the section boxes once (their order and ids stay), their contents every time
  const ids = d.sections.map(s => s.id).join(',');
  if (ids !== built) {
    built = ids;
    $('cols').innerHTML = d.sections.map(s => `<section id='s-${s.id}'></section>`).join('');
    $('nav').innerHTML = ['chart'].concat(d.sections.map(s => s.id), ['log']).map(id => `<a href='#s-${id}'>${id}</a>`).join('');
  }
  for (const s of d.sections) { const e = $('s-' + s.id); if (e) e.innerHTML = table(s); }
  hist.push({ u: d.gpuUsedMB, b: d.gpuBudgetMB, n: d.oursMB });
  if (hist.length > 3600) hist.shift();
  chart();
}

function chart() {
  const c = $('chart'), g = c.getContext('2d'), r = devicePixelRatio || 1;
  const w = c.width = c.clientWidth * r, h = c.height = c.clientHeight * r;
  const css = getComputedStyle(document.documentElement), col = n => css.getPropertyValue(n).trim();
  g.clearRect(0, 0, w, h);
  $('range').textContent = hist.length ? `${hist.length} s` : '-';
  if (hist.length < 2) return;
  const top = Math.max(...hist.map(p => Math.max(p.u, p.b))) * 1.05;
  const step = Math.pow(10, Math.floor(Math.log10(top / 4)));
  const grid = [1, 2, 5].map(m => m * step).find(s => top / s <= 6) || step * 10;
  g.font = `${10 * r}px Consolas, monospace`; g.lineWidth = 1;
  for (let v = 0; v <= top; v += grid) {
    const y = h - v / top * h;
    g.strokeStyle = col('--rule'); g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke();
    g.fillStyle = col('--dim'); g.fillText(String(Math.round(v)), 3 * r, y - 2 * r);
  }
  const x = i => i / (hist.length - 1) * w, y = v => h - v / top * h;
  for (const [k, n] of [['b', '--c'], ['u', '--a'], ['n', '--b']]) {
    g.strokeStyle = col(n); g.lineWidth = 1.5 * r; g.beginPath();
    hist.forEach((p, i) => i ? g.lineTo(x(i), y(p[k])) : g.moveTo(x(i), y(p[k])));
    g.stroke();
  }
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
  if (!$('pause').checked) {
    try { const r = await fetch('/stats.json', { cache: 'no-store' }); show(await r.json()); }
    catch (e) { $('conn').innerHTML = `<span class='err'>[no connection]</span>`; }
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
addEventListener('resize', chart);
poll();
loadLog();
setInterval(() => { if (!$('pause').checked) loadLog(); }, 5000);
</script>
</body>
</html>
";
	}
}
