// WASM 遷移 M6（0.10.5）的操作情境（不呼叫 AI）：日誌等級套用與還原、提示詞存新版本與切回內建預設、系統例外排序／匯出／刪除、
// AI 對話紀錄深連結與匯出、Token 用量匯出與明細窗連到 AI 對話紀錄、日誌匯出與快速區間、非管理員直打 M6 的 API 得 403，最後跑全部路由的煙霧測試。
// 結果寫到 E2E_OUT_DIR/m06-ops-results.json 與 smoke-results.json。
import { readFileSync } from 'node:fs';
import {
  apiAsPageUser, createRecorder, createUser, execute, isWasmPage, launchBrowser, login, newContext, query, screenshot,
} from './common.mjs';
import { clickToolbar, closeToasts, confirm, waitForToast, watchErrors } from './crud-ui.mjs';
import { fillPicker, seedAiData, seedTag } from './m06-views.mjs';
import { freezeTimes } from './m05-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

/**
 * 等到符合的元素剛好 n 個（0.10.11 起改在 Node 端輪詢）：page.waitForFunction 輪詢時要在頁面裡 eval，
 * 會被前端的 CSP（script-src 沒有 'unsafe-eval'）擋下。
 */
async function waitForCount(page, selector, n, timeout = 30000) {
  const deadline = Date.now() + timeout;
  while (await page.locator(selector).count() !== n) {
    if (Date.now() > deadline) {
      throw new Error(`等不到 ${selector} 剛好 ${n} 個（目前 ${await page.locator(selector).count()} 個）`);
    }
    await page.waitForTimeout(200);
  }
}

const recorder = createRecorder('m06-ops-results.json');
const smoke = createRecorder('smoke-results.json');
const userAccount = 'e2e-m6-user';
const userPassword = 'User!E2e2026x';

async function step(id, title, action) {
  try {
    const actual = await action();
    recorder.add(id, title, true, actual ?? '通過');
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

async function download(page, trigger) {
  const [file] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), trigger()]);
  return { name: file.suggestedFilename(), bytes: readFileSync(await file.path()) };
}

async function rows(page, css) {
  return page.locator(`${css} .ant-table-row`).allInnerTexts();
}

seedAiData();
if (query('SELECT Id FROM MyUser WHERE Account = ?', userAccount).length === 0) {
  createUser(userAccount, userPassword);
}

// Token 用量第一筆接到 AI 對話紀錄第 2 筆，明細窗才有「查看這次呼叫」按鈕。
execute("UPDATE TokenUsageLog SET CallId = '00000000-0000-4000-8000-000000000002' WHERE CallId = '00000000-0000-4000-9000-000000000002'");

const browser = await launchBrowser();
try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const errors = watchErrors(page);
  await login(page);
  freezeTimes();
  const since = utcNow();

  await step('M6-O1', '日誌等級：套用 Warn 再還原，頂部通知與稽核各一筆', async () => {
    await page.goto('/log-level-setting');
    await page.waitForSelector('.log-level-choices', { timeout: 60000 });
    check(await isWasmPage(page), '日誌等級頁不是 WASM');
    await page.locator('.log-level-choice', { hasText: /^\s*warn/i }).locator('input').check();
    await clickToolbar(page, '套用');
    await page.locator('.ant-message-notice', { hasText: '已套用日誌等級' }).waitFor({ timeout: 15000 });
    await clickToolbar(page, '還原為系統預設等級');
    await page.locator('.ant-message-notice', { hasText: '已還原為系統預設等級' }).waitFor({ timeout: 15000 });
    const apply = audits('LogLevel.Apply', since);
    const restore = audits('LogLevel.Restore', since);
    check(apply.length === 1 && restore.length === 1, `稽核 ${apply.length}/${restore.length}`);
    return apply[0].Detail;
  });

  await step('M6-O2', '提示詞：存成第 3 版、切回內建預設（稽核 version=3、version=default）', async () => {
    await page.goto('/prompt-templates');
    await page.waitForSelector('.prompt-table', { timeout: 60000 });
    await page.locator('.prompt-main button', { hasText: '編輯' }).first().click();
    const dialog = page.locator('.ant-modal-content:visible');
    await dialog.locator('textarea').first().fill('E2E 第三版：請先列出錯誤次數最多的三種訊息。');
    await dialog.locator('textarea').first().press('Tab');
    await dialog.locator('button', { hasText: '存成新版本' }).click();
    await waitForToast(page, '已存成第 3 版');
    await closeToasts(page);
    await page.locator('.prompt-main .prompt-table .ant-table-row', { hasText: '內建預設' }).locator('button', { hasText: '設為作用中' }).click();
    await confirm(page, '切換');
    await waitForToast(page, '已改用');
    const update = audits('Prompt.Update', since);
    const activate = audits('Prompt.Activate', since);
    check(update.length === 1 && update[0].Detail.includes('version=3'), `Prompt.Update：${JSON.stringify(update)}`);
    check(activate.length === 1 && activate[0].Detail.includes('version=default'), `Prompt.Activate：${JSON.stringify(activate)}`);
    check(query("SELECT CreatedByAccount FROM PromptTemplate WHERE Version = 3")[0].CreatedByAccount === 'support', '建立者不是 support');
    return `${update[0].Detail}、${activate[0].Detail}`;
  });

  await step('M6-O3', '系統例外：依次數排序生效、匯出 CSV 同序、刪除一列', async () => {
    await closeToasts(page);
    await page.goto('/system-exceptions');
    await page.waitForSelector('.exception-log-table', { timeout: 60000 });
    await page.fill('.exception-log-search-input', seedTag);
    await clickToolbar(page, '查詢');
    await waitForCount(page, '.exception-log-table .ant-table-row', 3);
    await page.locator('.exception-log-table th', { hasText: '次數' }).click();
    await page.waitForTimeout(1500);
    const ascending = (await rows(page, '.exception-log-table')).map((x) => x.includes('寄信逾時') ? 120 : x.includes('資料庫被鎖定') ? 12 : 3);
    check(JSON.stringify(ascending) === '[3,12,120]', `次數遞增排序不對：${ascending}`);
    const file = await download(page, () => clickToolbar(page, '匯出目前查詢結果（CSV）'));
    const text = file.bytes.toString('utf8');
    check(text.indexOf('儲存專案時狀態不正確') < text.indexOf('寄信逾時'), 'CSV 順序與畫面不同');
    await page.locator('.exception-log-table .ant-table-row', { hasText: '寄信逾時' }).locator('button', { hasText: '刪除' }).click();
    await confirm(page, '刪除');
    await waitForToast(page, '刪除成功');
    check(audits('ExceptionLog.Delete', since).length === 1, '刪除稽核不是一筆');
    check(audits('ExceptionLog.Export', since).length === 1, '匯出稽核不是一筆');
    return `${file.name}；排序 ${ascending.join('<')}`;
  });

  await step('M6-O4', 'AI 對話紀錄：深連結自動開明細窗、匯出 CSV、合併 PDF', async () => {
    await closeToasts(page);
    await page.goto('/ai-call-logs?callId=00000000-0000-4000-8000-000000000002');
    await page.waitForSelector('.ant-modal-content:visible', { timeout: 60000 });
    check(await isWasmPage(page), 'AI 對話紀錄頁不是 WASM');
    await page.waitForTimeout(800);
    await screenshot(page, 'm06-o4-深連結明細窗');
    await page.locator('.ant-modal-close').last().click();
    await page.locator('.ai-call-log-toolbar-left input[placeholder^="關聯說明"]').fill(seedTag);
    await clickToolbar(page, '查詢');
    await waitForCount(page, '.ant-table-row', 3);
    const csv = await download(page, () => clickToolbar(page, '匯出目前查詢結果（CSV，不含內文）'));
    check(csv.bytes.toString('utf8').includes('送出時間（本地）'), 'CSV 標頭不對');
    // 合併 PDF：成功時直接下載、沒有通知（與 Server 版相同）。
    const pdf = await download(page, () => clickToolbar(page, '匯出目前查詢結果（PDF，最多 50 筆）'));
    check(pdf.bytes.subarray(0, 4).toString() === '%PDF', '合併 PDF 不是 PDF');
    const merged = audits('AiCallLog.Export', since).find((x) => x.Detail.startsWith('format=pdf-merged'));
    check(merged && merged.Detail.includes('source=query') && merged.Detail.includes('rows=3'), `合併 PDF 稽核：${JSON.stringify(merged)}`);
    check(audits('AiCallLog.View', since).length >= 1, '明細窗沒有查看稽核');
    return `${csv.name}、${pdf.name}；${merged.Detail}`;
  });

  await step('M6-O5', 'Token 用量：匯出 CSV 與 PDF、明細窗連到 AI 對話紀錄', async () => {
    await closeToasts(page);
    await page.goto('/token-usage');
    await page.waitForSelector('.token-usage-toolbar', { timeout: 60000 });
    check(await isWasmPage(page), 'Token 用量頁不是 WASM');
    await page.fill('.token-usage-account-input', 'e2e-ai');
    await clickToolbar(page, '查詢');
    await page.waitForSelector('.ant-tabs-tab:has-text("明細（3）")', { timeout: 30000 });
    const csv = await download(page, () => clickToolbar(page, '匯出目前查詢結果（CSV）'));
    const pdf = await download(page, () => clickToolbar(page, '下載全部頁籤的 PDF 報表'));
    check(pdf.bytes.subarray(0, 4).toString() === '%PDF', 'PDF 不是 PDF');
    await page.locator('.ant-tabs-tab', { hasText: '明細（3）' }).click();
    await page.locator('.ant-tabs-tabpane-active .ant-table-row', { hasText: '10:00:00' }).locator('button', { hasText: '詳細' }).click();
    await page.waitForSelector('.ant-modal-content:visible', { timeout: 15000 });
    await page.locator('.ant-modal-content:visible button.toolbar-icon-button', { hasText: '查看這次呼叫' }).click();
    await page.waitForURL(/ai-call-logs\?callId=00000000-0000-4000-8000-000000000002/i, { timeout: 30000 });
    await page.waitForSelector('.ant-modal-content:visible', { timeout: 60000 });
    const exports = audits('TokenUsage.Export', since);
    check(exports.length === 2, `匯出稽核 ${exports.length} 筆`);
    return `${csv.name}、${pdf.name}；${exports.map((x) => x.Detail).join(' | ')}`;
  });

  await step('M6-O6', '日誌檢視：快速區間「今天」由伺服器給起訖、匯出 .log 含 BOM', async () => {
    await closeToasts(page);
    await page.goto('/logs');
    await page.waitForSelector('.log-view-table', { timeout: 60000 });
    await page.locator('.log-view-quick-ranges button', { hasText: '今天' }).click();
    await page.waitForTimeout(2000);
    const start = await page.locator('.log-view-toolbar-left .ant-picker input').nth(0).inputValue();
    const today = query("SELECT date('now', 'localtime') AS D")[0].D;
    check(start === `${today} 00:00:00`, `今天的起點不對：${start}`);
    await fillPicker(page, '.log-view-toolbar-left', 0, '2026-11-01 09:00:00');
    await fillPicker(page, '.log-view-toolbar-left', 1, '2026-11-01 10:00:00');
    await page.locator('.log-view-text-filters input').first().fill(seedTag);
    await clickToolbar(page, '查詢');
    await waitForCount(page, '.log-view-table .ant-table-row', 5);
    const file = await download(page, () => clickToolbar(page, '匯出本次查詢結果（原始 nlog 格式）'));
    check(file.bytes[0] === 0xef && file.bytes[1] === 0xbb && file.bytes[2] === 0xbf, '沒有 BOM');
    check(file.bytes.toString('utf8').split('\n').filter((x) => x.includes(seedTag)).length === 5, '匯出筆數不對');
    const export_ = audits('LogViewer.Export', since);
    check(export_.length === 1 && export_[0].Detail.includes('rows=5'), `匯出稽核：${JSON.stringify(export_)}`);
    return `${file.name}；今天起點 ${start}`;
  });

  await context.close();

  await step('M6-O7', '非管理員直打 M6 的 API 一律 403（不洩漏資料）', async () => {
    const userContext = await newContext(browser);
    const userPage = await userContext.newPage();
    await login(userPage, { account: userAccount, password: userPassword });
    const calls = [
      ['GET', '/api/v1/exception-logs/options'], ['POST', '/api/v1/exception-logs/search', {}], ['GET', '/api/v1/exception-logs/1'],
      ['POST', '/api/v1/exception-logs/1/ai', { conversationId: '00000000-0000-4000-8000-000000000009', messages: [] }],
      ['POST', '/api/v1/exception-logs/clear-all'],
      ['POST', '/api/v1/logs/search', {}], ['POST', '/api/v1/logs/ai', {}], ['GET', '/api/v1/logs/options'],
      ['GET', '/api/v1/log-level'], ['POST', '/api/v1/log-level/apply', { level: 'Trace' }],
      ['POST', '/api/v1/ai-call-logs/search', {}], ['GET', '/api/v1/ai-call-logs/1'], ['POST', '/api/v1/ai-call-logs/clear-all'],
      ['POST', '/api/v1/token-usage/dashboard', {}], ['GET', '/api/v1/token-usage/1'], ['POST', '/api/v1/token-usage/clear-all'],
      ['GET', '/api/v1/prompt-templates'], ['POST', '/api/v1/prompt-templates/LogAnalysis/activate', { version: null }],
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
    check(query('SELECT COUNT(*) AS N FROM ExceptionLog')[0].N > 0 && query('SELECT COUNT(*) AS N FROM AiCallLog')[0].N > 0, '資料被清掉了');
    return `${calls.length} 個端點都是 403`;
  });

  recorder.add('M6-O8', '操作期間沒有未處理的瀏覽器錯誤', errors.length === 0, errors.length === 0 ? '0 個' : errors.slice(0, 3).join(' | '));

  await runSmoke(browser, smoke);
} finally {
  await browser.close();
}

const okSmoke = smoke.save();
process.exit(recorder.save() && okSmoke ? 0 : 1);
