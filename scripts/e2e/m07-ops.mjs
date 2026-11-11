// WASM 遷移 M7（0.10.6）的實機情境：排程作業、系統健康、系統備份、資料保留、資料庫用量、系統參數、公告管理、首頁儀表板在 WebAssembly 執行。
// 立即執行排程看進度自動更新、建立並以一次性票證下載備份、修改參數立即生效、公告在同一分頁立即出現與在另一分頁出現、首頁小工具依權限顯示、
// 非管理員直打 M7 的 API 得 403、舊的 Cookie 備份下載網址已退役，最後跑全部路由的煙霧測試。
// 結果寫到 E2E_OUT_DIR/m07-results.json 與 smoke-results.json。
import { readFileSync } from 'node:fs';
import { apiAsPageUser, createRecorder, isWasmPage, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { clickToolbar, closeToasts, confirm, waitForToast, watchErrors } from './crud-ui.mjs';
import { freezeTimes } from './m05-views.mjs';
import { seedOpsData, seedTag, userAccount, userPassword } from './m07-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m07-results.json');
const smoke = createRecorder('smoke-results.json');

async function step(id, title, action) {
  try {
    recorder.add(id, title, true, (await action()) ?? '通過');
  } catch (error) {
    recorder.add(id, title, false, String(error?.message ?? error).split('\n')[0]);
  }
}

function check(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function utcNow() {
  return new Date().toISOString().replace('T', ' ').slice(0, 19);
}

function audits(action, since) {
  return query('SELECT TargetId, Detail FROM AuditLog WHERE Action = ? AND OccurredAt >= ? ORDER BY Id', action, since);
}

/** 以網頁登入換 Token 後呼叫 API，回傳狀態碼與 JSON。 */
async function apiJson(page, path, { method = 'GET', body } = {}) {
  return page.evaluate(async ({ path, method, body }) => {
    const bridge = await fetch('/api/v1/Auth/bridge', { method: 'POST', headers: { 'X-MyProject-Client': '1' } });
    const token = (await bridge.json()).data.accessToken;
    const response = await fetch(path, {
      method,
      headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* 檔案或空白 */ }
    return { status: response.status, json, length: text.length };
  }, { path, method, body });
}

async function download(page, trigger) {
  const [file] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), trigger()]);
  return { name: file.suggestedFilename(), bytes: readFileSync(await file.path()) };
}

seedOpsData();

const browser = await launchBrowser();
try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const errors = watchErrors(page);
  await login(page);
  freezeTimes();
  const since = utcNow();

  await step('M7-P1', '排程作業：立即執行後狀態自動更新（不重新整理）', async () => {
    await page.goto('/scheduled-jobs');
    await page.waitForSelector('.scheduled-job-table .ant-table-row', { timeout: 60000 });
    check(await isWasmPage(page), '排程作業頁不是 WASM');
    const before = (await apiJson(page, '/api/v1/scheduled-jobs/NotificationRetention/runs')).json.data.length;
    const jobs = (await apiJson(page, '/api/v1/scheduled-jobs')).json.data;
    const job = jobs.items.find((x) => x.name === 'NotificationRetention');
    check(job, '找不到 NotificationRetention');
    const row = page.locator('.scheduled-job-table .ant-table-row', { hasText: job.displayName }).first();
    const rowBefore = await row.innerText();
    await row.locator('td.ant-table-cell-fix-right button', { hasText: '立即執行' }).click();
    await confirm(page, '立即執行');
    let after = before;
    for (let i = 0; i < 40 && after === before; i++) {
      await page.waitForTimeout(500);
      after = (await apiJson(page, '/api/v1/scheduled-jobs/NotificationRetention/runs')).json.data.length;
    }

    check(after === before + 1, '作業沒有執行');
    await page.waitForFunction(([text]) => {
      const rows = [...document.querySelectorAll('.scheduled-job-table .ant-table-row')];
      return rows.some((r) => r.innerText !== text && r.innerText.includes(text.split('\n')[0]));
    }, [rowBefore], { timeout: 20000 });
    check(audits('Job.Trigger', since).some((x) => x.Detail === 'job=NotificationRetention'), '沒有 Job.Trigger 稽核');
    await screenshot(page, 'm07-p1-排程執行後');
    return `${job.displayName}：執行紀錄 ${before} → ${after}，畫面自動更新`;
  });

  await step('M7-P2', '系統備份：立即備份 → 清單自動出現 → 以一次性票證下載（第二次 404）', async () => {
    await closeToasts(page);
    await page.goto('/backups');
    await page.waitForSelector('.backup-table .ant-table-row', { timeout: 60000 });
    check(await isWasmPage(page), '備份頁不是 WASM');
    await clickToolbar(page, '立即備份');
    await confirm(page, '立即備份');
    await page.waitForFunction(() => document.querySelectorAll('.backup-table .ant-table-row').length >= 3, null, { timeout: 120000 });
    const newest = page.locator('.backup-table .ant-table-row').first();
    const name = (await newest.innerText()).match(/backup-\d{8}-\d{6}(-\d+)?\.zip/)[0];
    const file = await download(page, () => newest.locator('button', { hasText: '下載' }).click());
    check(file.bytes.subarray(0, 2).toString() === 'PK', '下載的不是 zip');
    check(file.name === name, `檔名不對：${file.name}`);
    const ticketUrl = (await apiJson(page, `/api/v1/backups/${name}/ticket`, { method: 'POST' })).json.data.url;
    const first = await apiJson(page, ticketUrl);
    const second = await apiJson(page, ticketUrl);
    check(first.status === 200 && second.status === 404, `票證兩次：${first.status}、${second.status}`);
    const downloads = audits('Backup.Download', since);
    check(downloads.length === 2, `下載稽核 ${downloads.length} 筆`);
    const old = await apiAsPageUser(page, `/api/backups/${name}/download`);
    check(old === 404, `舊網址還在：${old}`);
    return `${name}，${file.bytes.length} bytes；票證第二次 404；舊網址 404`;
  });

  await step('M7-P3', '系統參數：修改系統名稱立即生效、還原後恢復', async () => {
    await closeToasts(page);
    const overview = (await apiJson(page, '/api/v1/system-parameters')).json.data;
    const item = overview.items.find((x) => x.definition.key === 'SystemSettings:SystemInformation:SystemName');
    check(item, '找不到系統名稱參數');
    const save = await apiJson(page, '/api/v1/system-parameters/save', {
      method: 'POST', body: { key: 'SystemSettings:SystemInformation:SystemName', value: 'E2E 平台', expectedStamp: item.override?.concurrencyStamp ?? null },
    });
    check(save.status === 200, `儲存失敗：${save.status} ${JSON.stringify(save.json)}`);
    await page.goto('/App');
    await page.waitForSelector('.home-welcome', { timeout: 60000 });
    const title = await page.locator('.home-title').innerText();
    check(title.includes('E2E 平台'), `首頁標題沒變：${title}`);
    const after = (await apiJson(page, '/api/v1/system-parameters')).json.data;
    const changed = after.items.find((x) => x.definition.key === 'SystemSettings:SystemInformation:SystemName');
    const reset = await apiJson(page, '/api/v1/system-parameters/reset', {
      method: 'POST', body: { key: 'SystemSettings:SystemInformation:SystemName', expectedStamp: changed.override.concurrencyStamp },
    });
    check(reset.status === 200, `還原失敗：${reset.status}`);
    check(audits('SystemParameter.Update', since).length === 1 && audits('SystemParameter.Reset', since).length === 1, '參數稽核筆數不對');
    return `首頁標題「${title}」，還原成功`;
  });

  let announcementTitle = `${seedTag} 實機新增 ${Date.now()}`;
  await step('M7-P4', '公告：新增後同一個分頁的橫幅立即出現', async () => {
    await page.goto('/announcements');
    await page.waitForSelector('.ant-table-row', { timeout: 60000 });
    check(await isWasmPage(page), '公告頁不是 WASM');
    await clickToolbar(page, '新增');
    const dialog = page.locator('.ant-modal-content:visible');
    await dialog.locator('.ant-form-item', { hasText: '標題' }).locator('input').fill(announcementTitle);
    await dialog.locator('.ant-form-item', { hasText: '內容' }).locator('textarea').fill('實機驗證用的公告。');
    await dialog.locator('button', { hasText: /^\s*儲\s*存\s*$/ }).click();
    await waitForToast(page, '已新增公告');
    await page.locator('.announcement-banner', { hasText: announcementTitle }).first().waitFor({ timeout: 5000 });
    check(audits('Announcement.Create', since).length === 1, '新增稽核不是一筆');
    await screenshot(page, 'm07-p4-公告立即出現');
    return '5 秒內出現在本頁上方';
  });

  await step('M7-P5', '公告：另一個分頁（一般使用者）切回時看得到', async () => {
    const userContext = await newContext(browser);
    const userPage = await userContext.newPage();
    await login(userPage, { account: userAccount, password: userPassword });
    await userPage.goto('/Profile');
    await userPage.locator('.announcement-banner', { hasText: announcementTitle }).first().waitFor({ timeout: 30000 });
    await userContext.close();
    return '一般使用者的頁面上方出現新公告';
  });

  await step('M7-P6', '首頁小工具依權限：管理員有排程作業，一般使用者沒有', async () => {
    await page.goto('/App');
    await page.waitForSelector('.home-welcome', { timeout: 60000 });
    check(await isWasmPage(page), '首頁不是 WASM');
    await page.waitForTimeout(1500);
    const adminText = await page.locator('.home-welcome').innerText();
    const userContext = await newContext(browser);
    const userPage = await userContext.newPage();
    await login(userPage, { account: userAccount, password: userPassword });
    await userPage.goto('/App');
    await userPage.waitForSelector('.home-welcome', { timeout: 60000 });
    await userPage.waitForTimeout(1500);
    const userText = await userPage.locator('.home-welcome').innerText();
    const status = await apiAsPageUser(userPage, '/api/v1/dashboard/scheduled-jobs');
    await userContext.close();
    check(adminText.includes('排程作業') && adminText.includes('我的帳號'), '管理員首頁少了小工具');
    check(!userText.includes('前往排程作業') && userText.includes('我的帳號'), '一般使用者的首頁小工具不對');
    check(status === 403, `一般使用者讀排程小工具：${status}`);
    return '管理員 3 個小工具；一般使用者看不到排程作業（API 403）';
  });

  await step('M7-P7', '系統健康、資料保留、資料庫用量頁在 WASM 開得起來', async () => {
    for (const [route, selector] of [['/system-health', '.system-health-page'], ['/data-retention', '.data-retention-table .ant-table-row'], ['/database-usage', '.ant-table-row']]) {
      await page.goto(route);
      await page.waitForSelector(selector, { timeout: 90000 });
      check(await isWasmPage(page), `${route} 不是 WASM`);
    }

    return '三頁都是 WASM';
  });

  await context.close();

  await step('M7-P8', '非管理員直打 M7 的 API 一律 403', async () => {
    const userContext = await newContext(browser);
    const userPage = await userContext.newPage();
    await login(userPage, { account: userAccount, password: userPassword });
    const calls = [
      ['GET', '/api/v1/scheduled-jobs'], ['POST', '/api/v1/scheduled-jobs/NotificationRetention/trigger'],
      ['GET', '/api/v1/system-health'], ['POST', '/api/v1/system-health/test-notification'],
      ['GET', '/api/v1/backups'], ['POST', '/api/v1/backups/run'], ['POST', '/api/v1/backups/backup-20261101-090000.zip/ticket'],
      ['GET', '/api/v1/data-retention'], ['GET', '/api/v1/database-usage'],
      ['GET', '/api/v1/system-parameters'], ['POST', '/api/v1/system-parameters/reset', { key: 'SystemSettings:SystemInformation:SystemName' }],
      ['GET', '/api/v1/announcements'], ['POST', '/api/v1/announcements', { title: 'x', content: 'x' }],
    ];
    const wrong = [];
    for (const [method, path, body] of calls) {
      const status = await apiAsPageUser(userPage, path, { method, body });
      if (status !== 403) {
        wrong.push(`${method} ${path} → ${status}`);
      }
    }

    await userContext.close();
    check(wrong.length === 0, wrong.join('；'));
    return `${calls.length} 個端點都是 403`;
  });

  recorder.add('M7-P9', '操作期間沒有未處理的瀏覽器錯誤', errors.length === 0, errors.length === 0 ? '0 個' : errors.slice(0, 3).join(' | '));
  await runSmoke(browser, smoke);
} finally {
  await browser.close();
}

const okSmoke = smoke.save();
process.exit(recorder.save() && okSmoke ? 0 : 1);
