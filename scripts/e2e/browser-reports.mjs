// 前端錯誤與事件回報的實機驗證（0.10.36 起，日誌與例外處理 PRD LOG-23）。用法見同資料夾的 README.md。
// 需要 Start-E2EHost.ps1 的隔離主機：查資料庫（E2E_DB_FILE）與日誌檔（同一個隔離資料夾的 logs）。
//
// B1 登入頁發生的錯誤先存在瀏覽器，登入後送出，記成「登入前」
// B2 登入後主動回報的錯誤：瀏覽器產生的追蹤碼就是例外紀錄的最後追蹤碼，背景資訊在堆疊檔
// B3 每個 API 回應都帶 X-Trace-Code
// B4 有網路卻連不上伺服器：一段中斷記一筆 Warning
// B5 瀏覽器離線：一段中斷記一筆 Information（中斷期間重新整理也接得上）
// B6 資源載入失敗：Warning
// B7 內容安全政策擋下的內容：Warning
// B8 WASM 啟動失敗：存在瀏覽器，之後成功啟動並登入才送出
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { createRecorder, databaseFile, launchBrowser, login, newContext, query, screenshot, waitForPredicate } from './common.mjs';

const recorder = createRecorder('browser-reports-results.json');
// 每次執行的訊息都不同：同一個錯誤會合併計次，重跑時才不會讀到上一次的那一列。
const runId = Date.now().toString(36);
const logDir = process.env.E2E_LOG_DIR ?? join(dirname(dirname(databaseFile)), 'logs', 'MyProject.Web');
const exceptionDir = process.env.E2E_EXCEPTION_DIR ?? join(dirname(dirname(databaseFile)), 'Exception');

// 應用程式日誌（不含 NLog 自己的 *-nlog-internal.log）。
function allLogText() {
  if (!existsSync(logDir)) {
    return '';
  }

  return readdirSync(logDir).filter((x) => x.includes('-logfile-') && x.endsWith('.log')).sort()
    .map((x) => readFileSync(join(logDir, x), 'utf8')).join('\n');
}

// 只看這次執行開始之後寫的日誌（重跑時不會讀到上一次的）。
const logStart = allLogText().length;

function logText() {
  return allLogText().slice(logStart);
}

async function waitFor(check, timeout = 90000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const value = check();
    if (value) {
      return value;
    }

    if (Date.now() > deadline) {
      return null;
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}

function browserRows(message) {
  return query("SELECT Message, Operation, Source, Account, LastTraceId, StackTraceFile FROM ExceptionLog WHERE Source = '瀏覽器' AND Message LIKE ?", `%${message}%`);
}

function stackText(row) {
  if (!row?.StackTraceFile) {
    return '';
  }

  const file = existsSync(row.StackTraceFile) ? row.StackTraceFile : join(exceptionDir, row.StackTraceFile);
  return existsSync(file) ? readFileSync(file, 'utf8') : '';
}

async function outbox(page) {
  return page.evaluate(() => JSON.parse(localStorage.getItem('app.clientReports.v1') || '{"items":[]}').items);
}

const browser = await launchBrowser();
try {
  const context = await newContext(browser);
  const page = await context.newPage();

  // B1：登入頁（匿名）發生的錯誤。
  await page.goto('/Auths/Login');
  await page.locator('#account').waitFor({ timeout: 60000 });
  await page.evaluate((id) => setTimeout(() => { throw new Error(`e2e-before-signin-${id}`); }), runId);
  await waitForPredicate(page, (id) => JSON.parse(localStorage.getItem('app.clientReports.v1') || '{"items":[]}').items.some((x) => x.message.includes(`e2e-before-signin-${id}`)), runId);
  await login(page);
  const before = await waitFor(() => browserRows(`e2e-before-signin-${runId}`)[0]);
  recorder.add('B1', '登入前的錯誤登入後送出，記成「登入前」', before?.Operation === 'Browser script error before sign-in. Kind={Kind}' && before?.Account === 'support',
    JSON.stringify(before && { operation: before.Operation, account: before.Account }));

  // B2：登入後主動回報（與 .NET 端 ClientErrorCapture 同一條路），追蹤碼與背景資訊。
  await page.locator('.sidebar').waitFor({ timeout: 60000 });
  const code = await page.evaluate((id) => window.appClientErrors.report('caught', `e2e-after-signin-${id}`, 'at E2E.Test()', 'E2E'), runId);
  const after = await waitFor(() => browserRows(`e2e-after-signin-${runId}`)[0]);
  const stack = stackText(after);
  recorder.add('B2', '瀏覽器的追蹤碼就是例外紀錄的追蹤碼，堆疊檔有背景資訊',
    after?.LastTraceId === code && after?.Operation === 'Browser script error. Kind={Kind}' && stack.includes('Browser context:') && stack.includes('Breadcrumbs'),
    JSON.stringify({ code, lastTraceId: after?.LastTraceId, hasContext: stack.includes('Browser context:'), hasBreadcrumbs: stack.includes('Breadcrumbs') }));

  // B3：每個回應都帶追蹤碼。
  const header = await page.evaluate(async () => (await fetch('/api/v1/shell/info')).headers.get('X-Trace-Code'));
  recorder.add('B3', '回應帶 X-Trace-Code', /^[0-9A-HJKMNP-TV-Z]{8}$/.test(header ?? ''), String(header));

  // B4：有網路卻連不上伺服器（攔下所有 API），恢復後記一筆 Warning。
  await page.route('**/api/v1/**', (route) => route.abort('connectionrefused'));
  await page.evaluate(() => window.Blazor.navigateTo('/Profile'));
  await waitForPredicate(page, () => localStorage.getItem('app.networkEpisode.v1') !== null);
  await page.unroute('**/api/v1/**');
  await page.evaluate(() => window.Blazor.navigateTo('/'));
  const unreachable = await waitFor(() => /WARN\|\d+\|MyProject\.Web\.Diagnostics\.BrowserEventReporter\|Browser could not reach the server\.[^\n]*Account=support/.exec(logText()));
  recorder.add('B4', '連不上伺服器記一筆 Warning', unreachable !== null, unreachable ? unreachable[0].slice(0, 200) : '找不到日誌');

  // B5：瀏覽器離線（中斷期間重新整理也接得上），恢復後記一筆 Information。
  await context.setOffline(true);
  await page.evaluate(() => window.Blazor.navigateTo('/Profile'));
  await waitForPredicate(page, () => localStorage.getItem('app.networkEpisode.v1') !== null);
  await context.setOffline(false);
  await page.reload();
  const offline = await waitFor(() => /INFO\|\d+\|MyProject\.Web\.Diagnostics\.BrowserEventReporter\|Browser was offline\.[^\n]*Account=support/.exec(logText()));
  recorder.add('B5', '瀏覽器離線記一筆 Information', offline !== null, offline ? offline[0].slice(0, 200) : '找不到日誌');

  // B6：資源載入失敗。
  await page.locator('.sidebar').waitFor({ timeout: 60000 });
  await page.evaluate((id) => {
    const image = document.createElement('img');
    image.src = `/images/e2e-missing-${id}.png?v=1`;
    document.body.append(image);
  }, runId);
  const resource = await waitFor(() => /WARN\|\d+\|MyProject\.Web\.Diagnostics\.BrowserEventReporter\|Browser failed to load a resource\. Element=img, Url=\/images\/e2e-missing-[0-9a-z]+\.png,/.exec(logText()));
  recorder.add('B6', '資源載入失敗記 Warning（不含查詢字串）', resource !== null, resource ? resource[0].slice(0, 200) : '找不到日誌');

  // B7：內容安全政策擋下行內腳本。
  await page.evaluate(() => {
    const script = document.createElement('script');
    script.textContent = 'window.e2eInline = 1;';
    document.body.append(script);
  });
  const csp = await waitFor(() => /WARN\|\d+\|MyProject\.Web\.Diagnostics\.BrowserEventReporter\|Browser blocked content by the security policy\. Directive=script-src[^\n]*BlockedUrl=inline/.exec(logText()));
  recorder.add('B7', 'CSP 擋下的內容記 Warning', csp !== null, csp ? csp[0].slice(0, 200) : '找不到日誌');
  await screenshot(page, 'browser-reports-signed-in');
  await context.close();

  // B8：WASM 啟動失敗（擋下 .NET 執行環境），之後正常啟動並登入才送出。
  const bootContext = await newContext(browser);
  const bootPage = await bootContext.newPage();
  await bootPage.route('**/_framework/dotnet.native*', (route) => route.abort('failed'));
  await bootPage.goto('/Auths/Login');
  const queued = await waitForPredicate(bootPage, () => JSON.parse(localStorage.getItem('app.clientReports.v1') || '{"items":[]}').items.find((x) => x.kind === 'boot'), null, { timeout: 60000 })
    .catch(() => null);
  await screenshot(bootPage, 'browser-reports-boot-failed');
  await bootPage.unroute('**/_framework/dotnet.native*');
  await login(bootPage);
  // 同一個訊息可能已經合併成一列（堆疊檔只留第一次發生的），所以看日誌檔的這一行。
  const boot = queued ? await waitFor(() => /ERROR\|\d+\|MyProject\.Web\.Diagnostics\.BrowserErrorReporter\|Browser script error before sign-in\. Kind=boot\|/.exec(logText())) : null;
  recorder.add('B8', '啟動失敗存在瀏覽器，登入後送出（kind=boot、登入前）', boot !== null,
    JSON.stringify({ queued: queued?.message ?? null, logged: boot !== null, left: (await outbox(bootPage)).length }));
  await bootContext.close();
} finally {
  await browser.close();
}

process.exitCode = recorder.save() ? 0 : 1;
