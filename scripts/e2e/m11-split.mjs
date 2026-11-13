// WASM 遷移 M11（0.10.10）：前端獨立靜態站台＋API 主機的實機驗證。
// 預設以雙站台執行：前端 E2E_BASE_URL（提供發佈後的靜態檔，依 web.config 的規則與標頭）、API E2E_API_URL（ClientSettings:BaseUrl 指向前端）。
//   S1 雙站台登入：前端從靜態站載入，所有 /api 請求都打到 API 主機（跨來源、經過 CORS），沒有任何 /api 請求落在前端網址。
//   S2 CORS：允許的來源有 Access-Control-Allow-Origin；非允許的來源（預檢與實際請求）沒有，瀏覽器實際擋下。
//   S3 靜態站：深層網址回 index.html、安全標頭與 CSP、index.html 不快取、/Auths/ResetPassword 不帶 Referer 不快取、_framework 以 Brotli 傳送。
//   S4 API 主機不提供前端：非 /api 路徑 404。
//   S5 Google 登入（參數 google；主機要以假的 GoogleOAuthSettings__Enabled／ClientId／ClientSecret 啟動，按鈕才會出現）：按鈕導到 API 主機的 /Auths/Google/Login，再轉到 Google，回呼網址是 API 主機。
//   S6 冷啟動：全新瀏覽器（無快取）開登入頁到表單出現的時間與 _framework 傳輸量；第二次（有快取）。
//   S7 全部路由沒有 CSP 違規與頁面錯誤（＋36 路由煙霧）。
// 參數 `hosted`：同站台（API 主機直接提供前端），只跑 S1、S3 的同站台版、S6、S7。
// 結果寫到 E2E_OUT_DIR/m11-results.json 與 smoke-results.json。
import { apiUrl, baseUrl, createRecorder, execute, launchBrowser, login, newContext, screenshot } from './common.mjs';
import { runSmoke } from './smoke-routes.mjs';

const hosted = process.argv[2] === 'hosted';
const fakeGoogle = process.argv.includes('google');
// 截圖不可出現開發機設定的個人信箱：隔離資料庫的 support 改用測試地址（同 m05-views 的 freezeTimes）。
execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const recorder = createRecorder('m11-results.json');
const smoke = createRecorder('smoke-results.json');
const browser = await launchBrowser();
const frontOrigin = new URL(baseUrl).origin;
const apiOrigin = new URL(apiUrl).origin;
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

async function raw(url, init = {}) {
  const response = await fetch(url, { redirect: 'manual', ...init });
  return { status: response.status, headers: response.headers, text: await response.text() };
}

function header(r, name) {
  return r.headers.get(name) ?? '';
}

try {
  // S1：登入並開幾頁，看 /api 請求打到哪裡。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    const apiHosts = new Set();
    let apiOnFront = 0;
    page.on('request', (request) => {
      const url = new URL(request.url());
      if (url.pathname.startsWith('/api/')) {
        apiHosts.add(url.origin);
        if (url.origin === frontOrigin && !hosted) {
          apiOnFront++;
        }
      }
    });
    await login(page);
    for (const route of ['/projects', '/myusers', '/Profile']) {
      await page.goto(route);
      await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
      await page.waitForTimeout(1500);
    }
    await screenshot(page, 'S1-雙站台登入後');
    const expected = hosted ? frontOrigin : apiOrigin;
    recorder.add('S1', hosted ? '同站台：登入與換頁，API 與前端同一個網址' : '雙站台：登入與換頁，API 請求全部打到 API 主機',
      apiHosts.size === 1 && apiHosts.has(expected) && apiOnFront === 0,
      `前端 ${frontOrigin}；API 請求的來源：${[...apiHosts].join('、') || '無'}；落在前端網址的 /api 請求 ${apiOnFront} 個`);
    await context.close();
  }

  if (!hosted) {
    // S2：CORS。
    const allowed = await raw(`${apiOrigin}/api/v1/shell/culture`, { headers: { Origin: frontOrigin } });
    const preflightAllowed = await raw(`${apiOrigin}/api/v1/projects/search`, {
      method: 'OPTIONS', headers: { Origin: frontOrigin, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'authorization,content-type' },
    });
    const evil = 'http://evil.e2e.test';
    const denied = await raw(`${apiOrigin}/api/v1/shell/culture`, { headers: { Origin: evil } });
    const preflightDenied = await raw(`${apiOrigin}/api/v1/projects/search`, {
      method: 'OPTIONS', headers: { Origin: evil, 'Access-Control-Request-Method': 'POST', 'Access-Control-Request-Headers': 'authorization,content-type' },
    });
    // 瀏覽器實際擋下：在一個假的來源頁面（由 Playwright 攔截提供）呼叫 API。
    const context = await newContext(browser);
    const page = await context.newPage();
    await page.route(`${evil}/**`, (route) => route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>evil</body></html>' }));
    await page.goto(`${evil}/`);
    const browserResult = await page.evaluate(async (url) => {
      try {
        const r = await fetch(url);
        return `HTTP ${r.status}（沒被擋）`;
      } catch (e) {
        return `被擋下：${e.message}`;
      }
    }, `${apiOrigin}/api/v1/shell/culture`);
    await context.close();
    const allowOrigin = header(allowed, 'access-control-allow-origin');
    const ok = allowOrigin === frontOrigin && header(preflightAllowed, 'access-control-allow-origin') === frontOrigin
      && /authorization/i.test(header(preflightAllowed, 'access-control-allow-headers'))
      && !header(denied, 'access-control-allow-origin') && !header(preflightDenied, 'access-control-allow-origin')
      && browserResult.startsWith('被擋下') && !header(allowed, 'access-control-allow-credentials');
    recorder.add('S2', 'CORS：只允許前端網址', ok,
      `允許來源 GET：ACAO=${allowOrigin}；允許來源預檢：HTTP ${preflightAllowed.status}、ACAO=${header(preflightAllowed, 'access-control-allow-origin')}、允許標頭=${header(preflightAllowed, 'access-control-allow-headers')}；`
      + `非允許來源 GET：HTTP ${denied.status}、ACAO=${header(denied, 'access-control-allow-origin') || '無'}；非允許來源預檢：HTTP ${preflightDenied.status}、ACAO=${header(preflightDenied, 'access-control-allow-origin') || '無'}；`
      + `瀏覽器在 ${evil} 呼叫 API：${browserResult}；Allow-Credentials=${header(allowed, 'access-control-allow-credentials') || '無'}`);

    // S4：API 主機不提供前端。
    const apiPage = await raw(`${apiOrigin}/projects`);
    const apiRoot = await raw(`${apiOrigin}/`);
    recorder.add('S4', '雙站台時 API 主機不提供前端頁面', apiPage.status === 404 && apiRoot.status === 404,
      `GET ${apiOrigin}/projects：HTTP ${apiPage.status}；GET ${apiOrigin}/：HTTP ${apiRoot.status}`);
  }

  // S3：靜態站（雙站台）或 API 主機（同站台）的回應。
  {
    const deep = await raw(`${frontOrigin}/projects`);
    const index = await raw(`${frontOrigin}/`);
    const reset = await raw(`${frontOrigin}/Auths/ResetPassword?token=abc`);
    const csp = header(index, 'content-security-policy');
    const settings = await raw(`${frontOrigin}/appsettings.json`);
    const htmlOk = deep.status === 200 && /<div id="app"/.test(deep.text) && /blazor\.webassembly/.test(deep.text);
    const cspOk = /default-src 'self'/.test(csp) && /'wasm-unsafe-eval'/.test(csp) && (hosted || csp.includes(apiOrigin));
    const headersOk = /nosniff/i.test(header(index, 'x-content-type-options')) && (/deny|sameorigin/i.test(header(index, 'x-frame-options')) || /frame-ancestors/.test(csp))
      && header(index, 'referrer-policy') !== '';
    const noCache = /no-cache|no-store|max-age=0/i.test(header(index, 'cache-control'));
    const resetOk = /no-referrer/i.test(header(reset, 'referrer-policy')) && /no-store/i.test(header(reset, 'cache-control'));
    // _framework 以 Brotli 傳送（瀏覽器會送 Accept-Encoding: br）。
    const context = await newContext(browser);
    const page = await context.newPage();
    const encodings = new Map();
    page.on('response', async (response) => {
      const url = new URL(response.url());
      if (url.pathname.includes('/_framework/') && /\.(wasm|js)$/.test(url.pathname)) {
        encodings.set(url.pathname.split('/').pop(), (await response.headerValue('content-encoding')) ?? '無');
      }
    });
    await page.goto('/Auths/Login');
    await page.locator('#account').waitFor({ timeout: 90000 });
    await context.close();
    const brCount = [...encodings.values()].filter((x) => x === 'br').length;
    const settingsJson = (() => { try { return JSON.parse(settings.text); } catch { return null; } })();
    recorder.add('S3', hosted ? '同站台：深層網址、安全標頭與 CSP、重設密碼頁標頭' : '靜態站 web.config：SPA 回退、安全標頭與 CSP、快取、Brotli、重設密碼頁標頭',
      htmlOk && cspOk && headersOk && noCache && resetOk && (hosted || brCount > 0) && settingsJson !== null,
      `/projects：HTTP ${deep.status}、是 index.html=${htmlOk}；CSP：${csp || '無'}；X-Content-Type-Options=${header(index, 'x-content-type-options')}、X-Frame-Options=${header(index, 'x-frame-options') || '無'}、Referrer-Policy=${header(index, 'referrer-policy')}；`
      + `index.html Cache-Control=${header(index, 'cache-control') || '無'}；/Auths/ResetPassword：Referrer-Policy=${header(reset, 'referrer-policy')}、Cache-Control=${header(reset, 'cache-control')}；`
      + `_framework 的 wasm／js ${encodings.size} 個，其中 br ${brCount} 個；appsettings.json ApiBaseUrl=${settingsJson?.ApiBaseUrl ?? '（讀不到）'}`);
  }

  // S5：Google 登入的導向（假的用戶端設定，只看導到哪裡；不讓瀏覽器真的連到 Google）。
  // 瀏覽器：攔下前端導往 API 主機 /Auths/Google/Login 的那一步（Playwright 攔不到重新導向後的請求，所以在這裡停下）；
  // 腳本：直接請求同一個網址（不跟隨重新導向）讀 302 的目的地，檢查 Google 的回呼網址。
  if (fakeGoogle) {
    const context = await newContext(browser);
    const page = await context.newPage();
    const startHost = hosted ? frontOrigin : apiOrigin;
    let start = '';
    await page.route(`${startHost}/Auths/Google/Login*`, (route) => {
      start = route.request().url();
      return route.fulfill({ status: 200, contentType: 'text/html', body: '<html><body>stop</body></html>' });
    });
    await page.goto('/Auths/Login');
    await page.locator('#account').waitFor({ timeout: 60000 });
    await page.locator('.google-login-button, button:has-text("Google"), a:has-text("Google")').first().click();
    await page.waitForURL((url) => url.pathname.startsWith('/Auths/Google/Login'), { timeout: 30000 }).catch(() => {});
    await context.close();
    const redirect = start ? await raw(start) : null;
    const location = redirect ? header(redirect, 'location') : '';
    const redirectUri = location.startsWith('https://accounts.google.com/') ? new URL(location).searchParams.get('redirect_uri') ?? '' : '';
    recorder.add('S5', 'Google 登入從前端導到 API 主機，再轉 Google，回呼網址是 API 主機',
      start.startsWith(`${startHost}/Auths/Google/Login`) && /[?&]binding=/.test(start) && redirect?.status === 302 && redirectUri.startsWith(`${startHost}/signin-google`),
      `起點：${start.replace(/binding=[^&]+/, 'binding=…') || '沒有'}；API 回 HTTP ${redirect?.status ?? '—'} 導到 ${location.replace(/\?.*$/, '') || '—'}；Google 回呼網址：${redirectUri || '—'}`);
  }

  // S6：冷啟動（全新瀏覽器，無快取）與第二次（有快取）。
  {
    const coldBrowser = await launchBrowser();
    try {
      const context = await newContext(coldBrowser);
      const page = await context.newPage();
      const measure = async () => {
        const started = Date.now();
        await page.goto('/Auths/Login');
        await page.locator('#account').waitFor({ timeout: 120000 });
        const ms = Date.now() - started;
        const sizes = await page.evaluate(() => performance.getEntriesByType('resource')
          .filter((x) => x.name.includes('/_framework/'))
          .reduce((a, x) => ({ transfer: a.transfer + x.transferSize, decoded: a.decoded + x.decodedBodySize, count: a.count + 1 }), { transfer: 0, decoded: 0, count: 0 }));
        return { ms, ...sizes };
      };
      const cold = await measure();
      const warm = await measure();
      const mb = (b) => (b / 1024 / 1024).toFixed(1);
      recorder.add('S6', '冷啟動下載量與時間（記錄）', cold.count > 0,
        `第一次（無快取）：${cold.ms} ms、_framework ${cold.count} 個檔、傳輸 ${mb(cold.transfer)} MB（解壓後 ${mb(cold.decoded)} MB）；`
        + `第二次（有快取）：${warm.ms} ms、傳輸 ${mb(warm.transfer)} MB`);
      await context.close();
    } finally {
      await coldBrowser.close();
    }
  }

  // S7：全部路由沒有 CSP 違規（以瀏覽器的 securitypolicyviolation 事件收集，含違規的元素）。
  // 已知且排除：AntDesign 頁籤在 HTML 上加的 ondragover="event.preventDefault();"（拖曳頁籤用；本專案頁籤不開放拖曳，點選走 Blazor 事件，被擋不影響功能）。
  {
    const violations = [];
    let knownTabs = 0;
    await runSmoke(browser, smoke, {
      onContext: (ctx) => {
        ctx.exposeBinding('__reportCsp', ({ page: p }, v) => {
          if (v.directive === 'script-src-attr' && /class="ant-tabs-tab[^"]*"[^>]*ondragover="event\.preventDefault\(\);"/.test(v.element)) {
            knownTabs++;
            return;
          }
          violations.push(`${new URL(p.url()).pathname}：${v.directive} ${v.blocked} ${v.element.slice(0, 120)}`);
        });
        ctx.addInitScript(() => document.addEventListener('securitypolicyviolation', (e) => {
          const t = e.target;
          window.__reportCsp({ directive: e.violatedDirective, blocked: e.blockedURI, element: t && t.outerHTML ? t.outerHTML.slice(0, 400) : String(t) });
        }));
      },
    });
    recorder.add('S7', '36 個路由沒有 CSP 違規', violations.length === 0,
      `${violations.length ? `${violations.length} 則：${[...new Set(violations)].slice(0, 5).join('；')}` : '沒有'}（已知排除：AntDesign 頁籤的 ondragover ${knownTabs} 則）`);
  }
} finally {
  await browser.close();
}

const ok = recorder.save();
const okSmoke = smoke.save();
process.exit(ok && okSmoke ? 0 : 1);
