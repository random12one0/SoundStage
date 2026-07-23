// A small status page, served on the LAN so progress can be checked from a phone.
//
// Read-only and bound to this machine's own addresses — it serves one JSON file and one page, and
// has no way to change anything. It exists so "is it done yet?" doesn't require walking to the PC.
const http = require('http');
const fs = require('fs');
const path = require('path');

const DIR = __dirname;
const PORT = 8790;

const PAGE = `<!doctype html><html><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Soundstage — build status</title>
<style>
  :root{color-scheme:dark}
  *{box-sizing:border-box}
  body{margin:0;background:#0d1219;color:#e6eaee;
       font:16px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",system-ui,sans-serif;
       padding:22px 18px 60px;-webkit-font-smoothing:antialiased}
  h1{margin:0 0 2px;font-size:21px;letter-spacing:-.01em}
  .sub{color:#8b98a5;font-size:13px;margin-bottom:20px}
  .card{background:#151b23;border:1px solid #232d38;border-radius:14px;padding:16px 18px;margin-bottom:14px}
  .state{display:flex;align-items:center;gap:10px;font-size:17px;font-weight:600}
  .dot{width:10px;height:10px;border-radius:50%;flex:none}
  .working .dot{background:#f5a524;box-shadow:0 0 10px #f5a524;animation:p 1.6s ease-in-out infinite}
  .done .dot{background:#37e0cf;box-shadow:0 0 10px #37e0cf}
  .failed .dot{background:#ff7a85;box-shadow:0 0 10px #ff7a85}
  @keyframes p{0%,100%{opacity:1}50%{opacity:.35}}
  .note{color:#8b98a5;font-size:14px;margin-top:8px}
  ul{list-style:none;margin:0;padding:0}
  li{display:flex;gap:10px;padding:7px 0;border-bottom:1px solid #ffffff0d;font-size:14.5px}
  li:last-child{border-bottom:0}
  li .m{flex:none;width:18px;text-align:center}
  li.d .m{color:#37e0cf}
  li.w .m{color:#f5a524}
  li.p{color:#7a8794}
  .lab{font-size:11px;letter-spacing:.16em;text-transform:uppercase;color:#68757f;margin-bottom:10px}
  .t{font-variant-numeric:tabular-nums;color:#68757f;font-size:12px;margin-top:18px;text-align:center}
</style></head><body>
<h1>Soundstage</h1>
<div class="sub">Build status &middot; refreshes every 5s</div>
<div class="card"><div class="state" id="state"><span class="dot"></span><span id="stateText">Loading…</span></div>
  <div class="note" id="note"></div></div>
<div class="card"><div class="lab">Work</div><ul id="items"></ul></div>
<div class="t" id="stamp"></div>
<script>
async function tick(){
  try{
    const r = await fetch('status.json?t=' + Date.now());
    const s = await r.json();
    const el = document.getElementById('state');
    el.className = 'state ' + (s.state || 'working');
    document.getElementById('stateText').textContent = s.headline || '';
    document.getElementById('note').textContent = s.note || '';
    document.getElementById('items').innerHTML = (s.items || []).map(function(i){
      const cls = i.done ? 'd' : (i.active ? 'w' : 'p');
      const mark = i.done ? '&#10003;' : (i.active ? '&#9679;' : '&#183;');
      return '<li class="' + cls + '"><span class="m">' + mark + '</span><span>' + i.text + '</span></li>';
    }).join('');
    document.getElementById('stamp').textContent = 'updated ' + (s.updated || '');
  }catch(e){
    document.getElementById('stateText').textContent = 'Cannot reach the PC';
  }
}
tick(); setInterval(tick, 5000);
</script></body></html>`;

http.createServer((req, res) => {
  const url = (req.url || '/').split('?')[0];
  if (url === '/status.json') {
    fs.readFile(path.join(DIR, 'status.json'), (err, data) => {
      res.writeHead(err ? 404 : 200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(err ? '{}' : data);
    });
    return;
  }
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(PAGE);
}).listen(PORT, '0.0.0.0', () => console.log('status page on ' + PORT));
