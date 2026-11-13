// 效能量測（0.10.13 起；升級 .NET 11 前後比較用）。建議以發佈版執行：Start-E2EHost.ps1 -Published（同站台，前端與 API 都是 Release）。
//   P1 第一次開啟（無快取、擋掉 Service Worker）：到登入表單出現的時間、傳輸量、請求數。
//   P2 再次開啟（Service Worker 與 HTTP 快取都在）：到登入表單出現的時間。
//   P3 登入後在前端內換頁（Blazor.navigateTo）：每個畫面從換頁到 API 請求全部完成的時間。
//   P4 主要 API 的回應時間（在頁面裡以 fetch 量，含網路與 JSON；先暖身一次）。
//   P5 記憶體：伺服器工作集（MyProject.Web 行程）、瀏覽器 JS heap、WebAssembly 記憶體。
// 每一項跑 PERF_RUNS 次（預設 5）取中位數。PERF_SEED=1 時先灌入大量資料（2,000 筆專案、300 個分類），量大量資料的查詢與匯出。
// 結果寫到 E2E_OUT_DIR/perf-<PERF_LABEL>.json（PERF_LABEL 預設 run），主控台印出摘要。
import { execSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { apiJsonAsPageUser, createRecorder, databaseFile, execute, launchBrowser, login, newContext, outDir, pageAccessToken, query } from './common.mjs';
import { authenticatedRoutes } from './smoke-routes.mjs';

const runs = Number(process.env.PERF_RUNS ?? 5);
const label = process.env.PERF_LABEL ?? 'run';
const median = (values) => {
  const sorted = values.filter((x) => typeof x === 'number' && !Number.isNaN(x)).sort((a, b) => a - b);
  if (sorted.length === 0) return null;
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
};
const round = (x) => (x === null ? null : Math.round(x));

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
if (process.env.PERF_SEED === '1' && query("SELECT COUNT(*) AS N FROM Project WHERE Title LIKE 'PERF-%'")[0].N === 0) {
  // 同一個連線、同一個交易灌資料（common.mjs 的 execute 每次都開新連線，不能跨呼叫使用交易）。
  const db = new DatabaseSync(databaseFile);
  try {
    db.exec('BEGIN');
    const category = db.prepare(`INSERT INTO Category (Name, IsEnabled, CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted)
                                 VALUES (?, 1, datetime('now'), datetime('now'), lower(hex(randomblob(16))), 0)`);
    for (let i = 1; i <= 300; i++) {
      category.run(`PERF-分類-${String(i).padStart(3, '0')}`);
    }
    const statuses = ['未開始', '進行中', '已完成', '暫緩'];
    const priorities = ['低', '中', '高'];
    const project = db.prepare(`INSERT INTO Project (Title, Description, StartDate, EndDate, Status, Priority, CompletionPercentage, Owner, Categories, Teams,
                                                     CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted)
                                VALUES (?, ?, '2026-01-01 00:00:00', '2026-12-31 00:00:00', ?, ?, ?, ?, ?, NULL, datetime('now'), datetime('now'), lower(hex(randomblob(16))), 0)`);
    for (let i = 1; i <= 2000; i++) {
      project.run(`PERF-專案-${String(i).padStart(4, '0')}`, `效能量測用的第 ${i} 筆專案，說明文字稍微長一點，模擬實際資料。`,
        statuses[i % 4], priorities[i % 3], i % 101, `負責人${i % 37}`, `\nPERF-分類-${String((i % 300) + 1).padStart(3, '0')}\n`);
    }
    db.exec('COMMIT');
  } finally {
    db.close();
  }
}

const recorder = createRecorder(`perf-${label}-results.json`);
const browser = await launchBrowser();
const result = { label, runs, at: new Date().toISOString(), firstLoad: [], warmLoad: [], pages: {}, apis: {}, memory: {} };

/** 量到登入表單出現的時間；同時以 CDP 統計傳輸量與請求數。 */
async function measureLogin(page, cdp) {
  let bytes = 0;
  let requests = 0;
  if (cdp) {
    cdp.on('Network.loadingFinished', (e) => { bytes += e.encodedDataLength; requests++; });
  }
  const started = Date.now();
  await page.goto('/Auths/Login', { waitUntil: 'commit' });
  await page.locator('#account').waitFor({ timeout: 180000 });
  return { ms: Date.now() - started, bytes, requests };
}

try {
  // P1：第一次開啟。
  for (let i = 0; i < runs; i++) {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: process.env.E2E_BASE_URL ?? 'https://localhost:7144', serviceWorkers: 'block', viewport: { width: 1440, height: 900 }, locale: 'zh-TW' });
    const page = await context.newPage();
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
    result.firstLoad.push(await measureLogin(page, cdp));
    await context.close();
  }

  // P2：再次開啟（同一個瀏覽器設定檔：Service Worker 與 HTTP 快取都在）。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    await measureLogin(page, null);
    // 等 Service Worker 裝好（發佈版才會快取；開發模式不快取）。
    await page.waitForTimeout(5000);
    for (let i = 0; i < runs; i++) {
      const started = Date.now();
      await page.reload({ waitUntil: 'commit' });
      await page.locator('#account').waitFor({ timeout: 120000 });
      result.warmLoad.push({ ms: Date.now() - started });
    }
    await context.close();
  }

  // P3～P5：登入後。
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  await page.goto('/App');
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });

  // 進行中的 API 請求（不含長連線的通知推播）。
  const inflight = new Set();
  const isApi = (r) => /\/api\//.test(r.url()) && !/notifications\/(stream|push)/.test(r.url());
  page.on('request', (r) => { if (isApi(r)) inflight.add(r); });
  page.on('requestfinished', (r) => inflight.delete(r));
  page.on('requestfailed', (r) => inflight.delete(r));

  async function navigate(route) {
    inflight.clear();
    const started = Date.now();
    await page.evaluate((path) => window.Blazor.navigateTo(path), route);
    // 等 API 請求開始又全部結束，且之後 400 毫秒沒有新的請求。
    let quietSince = null;
    let lastBusy = started;
    while (Date.now() - started < 60000) {
      if (inflight.size > 0) {
        lastBusy = Date.now();
        quietSince = null;
      } else if (quietSince === null) {
        quietSince = Date.now();
      } else if (Date.now() - quietSince >= 400 && Date.now() - started >= 300) {
        break;
      }
      await page.waitForTimeout(25);
    }
    return lastBusy - started;
  }

  // 每次從個人資料頁出發（量 /Profile 本身時從首頁出發）；不量啟動頁 /（3 秒後自動轉頁）與錯誤頁、強制設定頁。
  const routes = authenticatedRoutes.filter((r) => !['/', '/Error', '/not-found', '/ChangePassword', '/TwoFactorSetup'].includes(r));
  for (const route of routes) {
    const times = [];
    for (let i = 0; i < Math.max(3, Math.min(runs, 5)); i++) {
      await navigate(route === '/Profile' ? '/App' : '/Profile');
      times.push(await navigate(route));
    }
    result.pages[route] = round(median(times));
  }

  // P4：主要 API。
  const token = await pageAccessToken(page);
  const apis = [
    ['分類查詢', 'POST', '/api/v1/Category/search', { pageIndex: 1, pageSize: 20 }],
    ['分類查詢（關鍵字）', 'POST', '/api/v1/Category/search', { pageIndex: 1, pageSize: 20, keyword: 'PERF-分類-1' }],
    ['專案查詢', 'POST', '/api/v1/Project/search', { pageIndex: 1, pageSize: 20 }],
    ['專案查詢（每頁 200）', 'POST', '/api/v1/Project/search', { pageIndex: 1, pageSize: 200 }],
    ['專案查詢（第 50 頁）', 'POST', '/api/v1/Project/search', { pageIndex: 50, pageSize: 20 }],
    ['專案匯出', 'POST', '/api/v1/Project/export', { pageIndex: 1, pageSize: 20 }],
    ['使用者查詢', 'POST', '/api/v1/users/search', { pageIndex: 1, pageSize: 20 }],
    ['稽核紀錄查詢', 'POST', '/api/v1/audit-logs/search', { pageIndex: 1, pageSize: 20 }],
    ['日誌查詢', 'POST', '/api/v1/logs/search', {}],
    ['外框選單', 'GET', '/api/v1/shell/menu', undefined],
    ['目前使用者', 'GET', '/api/v1/Auth/me', undefined],
    ['首頁儀表板', 'GET', '/api/v1/dashboard', undefined],
  ];
  for (const [name, method, path, body] of apis) {
    const times = [];
    let status = null;
    let bytes = null;
    for (let i = 0; i <= runs + 2; i++) {
      const measured = await page.evaluate(async ({ path, method, body, token }) => {
        const started = performance.now();
        const response = await fetch(path, { method, headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body) });
        const buffer = await response.arrayBuffer();
        return { ms: performance.now() - started, status: response.status, bytes: buffer.byteLength };
      }, { path, method, body, token });
      status = measured.status;
      bytes = measured.bytes;
      if (i > 0) times.push(measured.ms); // 第一次是暖身
    }
    result.apis[name] = { ms: round(median(times)), status, bytes };
  }

  // 存檔（新增一筆分類再修改，量兩個動作；記下狀態碼）。
  {
    const created = [];
    const updated = [];
    const statuses = new Set();
    for (let i = 0; i < runs; i++) {
      const name = `PERF-存檔-${label}-${Date.now()}-${i}`;
      let started = Date.now();
      const create = await apiJsonAsPageUser(page, '/api/v1/Category', { method: 'POST', body: { name, isEnabled: true } });
      created.push(Date.now() - started);
      statuses.add(`新增 ${create.status}`);
      const id = query('SELECT Id FROM Category WHERE Name = ?', name)[0]?.Id;
      if (id) {
        const current = await apiJsonAsPageUser(page, `/api/v1/Category/${id}`);
        started = Date.now();
        const update = await apiJsonAsPageUser(page, `/api/v1/Category/${id}`, { method: 'PUT', body: { ...current.json?.data, description: `修改 ${i}` } });
        updated.push(Date.now() - started);
        statuses.add(`修改 ${update.status}`);
      }
    }
    result.apis['分類新增'] = { ms: round(median(created)), status: [...statuses].some((x) => x.startsWith('新增') && !x.endsWith(' 200')) ? [...statuses].join('、') : 200 };
    result.apis['分類修改'] = { ms: round(median(updated)), status: [...statuses].some((x) => x.startsWith('修改') && !x.endsWith(' 200')) ? [...statuses].join('、') : 200 };
  }

  // P5：記憶體。
  try {
    const ws = execSync('powershell -NoProfile -Command "(Get-Process MyProject.Web | Measure-Object WorkingSet64 -Sum).Sum"').toString().trim();
    result.memory.serverWorkingSetMb = round(Number(ws) / 1024 / 1024);
  } catch { result.memory.serverWorkingSetMb = null; }
  const cdp = await context.newCDPSession(page);
  await cdp.send('Performance.enable');
  const metrics = (await cdp.send('Performance.getMetrics')).metrics;
  result.memory.jsHeapUsedMb = round((metrics.find((m) => m.name === 'JSHeapUsedSize')?.value ?? 0) / 1024 / 1024);
  result.memory.wasmMemoryMb = await page.evaluate(() => {
    const module = window.Blazor?.runtime?.Module;
    const heap = module?.HEAPU8 ?? module?.HEAP8;
    return heap ? Math.round(heap.length / 1024 / 1024) : null;
  }).catch(() => null);
  await context.close();
} finally {
  await browser.close();
}

const summary = {
  firstLoadMs: round(median(result.firstLoad.map((x) => x.ms))),
  firstLoadKb: round(median(result.firstLoad.map((x) => x.bytes)) / 1024),
  firstLoadRequests: round(median(result.firstLoad.map((x) => x.requests))),
  warmLoadMs: round(median(result.warmLoad.map((x) => x.ms))),
};
result.summary = summary;
writeFileSync(join(outDir, `perf-${label}.json`), JSON.stringify(result, null, 2));
recorder.add('P1', '第一次開啟', summary.firstLoadMs !== null, `${summary.firstLoadMs} 毫秒、${summary.firstLoadKb} KB、${summary.firstLoadRequests} 個請求`);
recorder.add('P2', '再次開啟', summary.warmLoadMs !== null, `${summary.warmLoadMs} 毫秒`);
recorder.add('P3', '前端換頁', Object.keys(result.pages).length > 0, Object.entries(result.pages).map(([k, v]) => `${k} ${v}`).join('、'));
recorder.add('P4', '主要 API', Object.values(result.apis).every((x) => x.status === undefined || x.status === 200), Object.entries(result.apis).map(([k, v]) => `${k} ${v.ms}ms${v.status && v.status !== 200 ? `（HTTP ${v.status}）` : ''}`).join('、'));
recorder.add('P5', '記憶體', true, `伺服器 ${result.memory.serverWorkingSetMb} MB、JS heap ${result.memory.jsHeapUsedMb} MB、WASM ${result.memory.wasmMemoryMb} MB`);
process.exit(recorder.save() ? 0 : 1);
