// WASM 遷移 M10（0.10.9）：移除 Server 模式後的實機驗證。
// N1：36 個路由全部開過一次，整段過程沒有 WebSocket，也沒有任何 /_blazor 請求（SignalR 電路）。
// N2：整頁請求回的 HTML 只有 WASM 的外殼（有 blazor.web.js 與 WebAssembly 根元件標記，沒有重連畫面與 Server 根元件標記）。
// N3：/_blazor/negotiate 不存在（Server 互動模式的 SignalR 端點已拿掉）。
// N4：登入後的頁面、匿名頁都以 WebAssembly 執行。
// 另附 36 路由煙霧測試（R01～R36）。結果寫到 E2E_OUT_DIR/m10-results.json 與 smoke-results.json。
import { baseUrl, createRecorder, isWasmPage, launchBrowser, login, newContext } from './common.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m10-results.json');
const smoke = createRecorder('smoke-results.json');
const browser = await launchBrowser();

/** 對主機直接發請求（不經瀏覽器，不執行 JS），取原始回應。 */
async function raw(path, { method = 'GET' } = {}) {
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
  const response = await fetch(new URL(path, baseUrl), { method, redirect: 'manual' });
  return { status: response.status, text: await response.text() };
}

try {
  // N2：整頁請求的 HTML。
  const pages = ['/projects', '/Auths/Login', '/not-found-at-all'];
  const htmlChecks = [];
  for (const path of pages) {
    const { status, text } = await raw(path);
    const hasLoader = text.includes('_framework/blazor.web.js');
    const hasWasmMarker = /<!--Blazor:\{[^}]*"type":"webassembly"/.test(text);
    const hasServerMarker = /<!--Blazor:\{[^}]*"type":"server"/.test(text);
    const hasReconnect = /components-reconnect|ReconnectModal/i.test(text);
    htmlChecks.push({ path, status, ok: hasLoader && hasWasmMarker && !hasServerMarker && !hasReconnect,
      detail: `${path}：HTTP ${status}，blazor.web.js=${hasLoader}，WebAssembly 根元件=${hasWasmMarker}，Server 根元件=${hasServerMarker}，重連畫面=${hasReconnect}` });
  }
  recorder.add('N2', '整頁請求只回 WASM 外殼', htmlChecks.every((x) => x.ok), htmlChecks.map((x) => x.detail).join('；'));

  // N3：SignalR 端點。
  const negotiate = await raw('/_blazor/negotiate?negotiateVersion=1', { method: 'POST' });
  recorder.add('N3', '/_blazor/negotiate 不存在', negotiate.status !== 200,
    `POST /_blazor/negotiate：HTTP ${negotiate.status}${negotiate.status === 200 ? '（Server 互動模式仍在）' : ''}`);

  // N4：登入後頁面與匿名頁都以 WebAssembly 執行。
  const context = await newContext(browser);
  const page = await context.newPage();
  await page.goto('/Auths/Login');
  await page.locator('#account').waitFor({ timeout: 60000 });
  const anonymousWasm = await isWasmPage(page);
  await login(page);
  await page.goto('/projects');
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
  const signedInWasm = await isWasmPage(page);
  recorder.add('N4', '登入頁與登入後頁面都以 WebAssembly 執行', anonymousWasm && signedInWasm,
    `登入頁 WebAssembly=${anonymousWasm}；/projects WebAssembly=${signedInWasm}`);
  await context.close();

  // N1：36 個路由，整段過程監聽 WebSocket 與 /_blazor 請求。
  const sockets = [];
  const blazorRequests = [];
  let requestCount = 0;
  await runSmoke(browser, smoke, {
    onContext: (ctx) => {
      ctx.on('request', (request) => {
        requestCount++;
        if (new URL(request.url()).pathname.startsWith('/_blazor')) {
          blazorRequests.push(request.url());
        }
      });
      ctx.on('page', (p) => p.on('websocket', (socket) => sockets.push(socket.url())));
    },
  });
  recorder.add('N1', '36 個路由沒有 WebSocket 也沒有 /_blazor 請求', sockets.length === 0 && blazorRequests.length === 0 && requestCount > 0,
    `監聽到請求 ${requestCount} 個；WebSocket ${sockets.length} 條${sockets.length ? `（${[...new Set(sockets)].join('、')}）` : ''}；/_blazor 請求 ${blazorRequests.length} 個${blazorRequests.length ? `（${[...new Set(blazorRequests)].slice(0, 3).join('、')}）` : ''}`);
} finally {
  await browser.close();
}

const ok = recorder.save();
const okSmoke = smoke.save();
process.exit(ok && okSmoke ? 0 : 1);
