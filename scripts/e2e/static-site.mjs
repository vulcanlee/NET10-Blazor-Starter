// M11 實機用的靜態站模擬（0.10.10 起；開發機沒有能用的 IIS／IIS Express 時，雙站台實機用它提供前端）：
// 照前端 web.config 的規則提供發佈輸出：wwwroot 底下的實體檔、瀏覽器接受 br 且有 .br 副本時送預壓縮版、
// _framework 找不到回 404、其他網址回 wwwroot/index.html；安全標頭與 CSP 直接取自發佈出來的 web.config 的 customHeaders；
// 快取：預設 no-cache，_framework 帶指紋的檔案一年 immutable；/Auths/ResetPassword 加 no-referrer 與 no-store。
// ⚠️ 只模擬 web.config 的規則，不能取代上線前在真的 IIS（URL Rewrite 模組）驗證一次。
// 用法：node scripts/e2e/static-site.mjs <前端發佈根目錄（web.config 所在）> <port>
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize } from 'node:path';

const [root, port] = process.argv.slice(2);
const site = join(root, 'wwwroot');
const webConfig = readFileSync(join(root, 'web.config'), 'utf8');
const customHeaders = {};
const block = /<customHeaders>([\s\S]*?)<\/customHeaders>/.exec(webConfig)?.[1] ?? '';
for (const m of block.matchAll(/<add\s+name="([^"]+)"\s+value="([^"]*)"/g)) {
  customHeaders[m[1]] = m[2].replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
}
const types = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json',
  '.wasm': 'application/wasm', '.dll': 'application/octet-stream', '.webcil': 'application/octet-stream', '.dat': 'application/octet-stream',
  '.blat': 'application/octet-stream', '.pdb': 'application/octet-stream', '.png': 'image/png', '.ico': 'image/x-icon', '.svg': 'image/svg+xml',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.map': 'application/json',
};

createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  let path = decodeURIComponent(url.pathname);
  for (const [k, v] of Object.entries(customHeaders)) res.setHeader(k, v);
  let file = normalize(join(site, path));
  if (!file.startsWith(site)) { res.writeHead(400).end(); return; }
  const isFile = existsSync(file) && statSync(file).isFile();
  if (!isFile) {
    if (path.startsWith('/_framework/')) { res.writeHead(404).end(); return; }
    file = join(site, 'index.html');
    path = '/index.html';
  }
  const ext = extname(file);
  res.setHeader('Content-Type', types[ext] ?? 'application/octet-stream');
  const fingerprinted = path.startsWith('/_framework/') && /\.[a-z0-9]{8,}\.[a-z0-9]+$/i.test(path);
  res.setHeader('Cache-Control', fingerprinted ? 'public, max-age=31536000, immutable' : 'no-cache');
  if (/^\/Auths\/ResetPassword/i.test(decodeURIComponent(url.pathname))) {
    res.setHeader('Referrer-Policy', 'no-referrer');
    res.setHeader('Cache-Control', 'no-store');
  } else {
    res.setHeader('Referrer-Policy', 'strict-origin-when-cross-origin');
  }
  let body = file;
  if (/\bbr\b/.test(req.headers['accept-encoding'] ?? '') && existsSync(`${file}.br`)) {
    body = `${file}.br`;
    res.setHeader('Content-Encoding', 'br');
    res.setHeader('Vary', 'Accept-Encoding');
  }
  res.writeHead(200);
  res.end(req.method === 'HEAD' ? undefined : readFileSync(body));
}).listen(Number(port), 'localhost', () => console.log(`靜態站模擬：http://localhost:${port} → ${site}（customHeaders ${Object.keys(customHeaders).length} 個）`));
