// WASM 遷移 M6（0.10.5）的實機情境：日誌檢視、日誌等級、系統例外、AI 對話紀錄、Token 用量、AI 提示詞頁在 WebAssembly 執行，
// AI 分析改走 NDJSON 串流。AI 情境用真實模型（User Secrets 的金鑰，只在網站執行時讀取）：逐字出現、追問、連結被中和、匯出 PDF、
// 停止產生保留部分內容、關窗取消並在 AI 對話紀錄看得到、日誌 AI 分析；另外是各頁的操作、匯出、稽核與非管理員 403。
// 參數 quota：只跑「超過用量上限」情境（主機要以 AiQuotaSettings__PerUserDailyTwd=1 啟動，見 README）。
// 結果寫到 E2E_OUT_DIR/m06-results.json（quota 模式為 m06-quota-results.json）。
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  apiAsPageUser, createRecorder, createUser, execute, isWasmPage, launchBrowser, login, newContext, outDir, query, screenshot, waitForPredicate } from './common.mjs';
import { clickToolbar, closeToasts, confirm, waitForToast, watchErrors } from './crud-ui.mjs';
import { fillPicker, seedAiData, seedTag } from './m06-views.mjs';
import { freezeTimes } from './m05-views.mjs';

const quotaMode = process.argv[2] === 'quota';
const recorder = createRecorder(quotaMode ? 'm06-quota-results.json' : 'm06-results.json');
const day = '2026-11-01';
const userPassword = 'User!E2e2026x';

const aiModal = (page) => page.locator('.exception-ai-modal');
const logAiModal = (page) => page.locator('.log-ai-modal');

async function step(id, title, action) {
  try {
    const actual = await action();
    recorder.add(id, title, true, actual ?? '通過');
  } catch (error) {
    // 失敗當下的畫面與「哪個元素擋住了點擊」（0.10.11 起），方便判斷是程式還是腳本的問題。
    const message = String(error?.message ?? error);
    const blocker = /<[^>]+> (?:from <[^>]+> subtree )?intercepts pointer events/.exec(message)?.[0] ?? '';
    await currentPage?.screenshot({ path: join(outDir, 'screenshots', `${id}-失敗當下.png`) }).catch(() => {});
    recorder.add(id, title, false, message.split('\n')[0] + (blocker ? `；擋住的元素：${blocker.slice(0, 200)}` : ''));
  }
}

let currentPage = null;

/**
 * 關掉 AI 對話窗（0.10.11 起）：先把滑鼠移開 —— 上一步按過的工具列按鈕還停著滑鼠時，它的提示框會蓋在對話窗右上角的關閉鈕上
 * （真人把滑鼠移到關閉鈕時提示框會先消失，只有腳本會遇到）。
 */
async function closeModal(page, modal) {
  await modal.locator('.ant-modal-title').hover().catch(() => page.mouse.move(5, 5));
  await page.waitForTimeout(500);
  if (await page.locator('.ant-tooltip:not(.ant-tooltip-hidden)').isVisible().catch(() => false)) {
    // 提示框還在（Playwright 的滑鼠移動不一定觸發與真人相同的離開事件）：直接對關閉鈕送出點擊；真人操作另於最終巡檢時手動確認。
    await modal.locator('.ant-modal-close').dispatchEvent('click');
    return;
  }

  await modal.locator('.ant-modal-close').click();
}


function check(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function auditCount(action, since) {
  return query('SELECT COUNT(*) AS N FROM AuditLog WHERE Action = ? AND OccurredAt >= ?', action, since)[0].N;
}

function utcNow() {
  return new Date().toISOString().replace('T', ' ').slice(0, 19);
}

/** 打開系統例外頁並篩出種子資料，再打開某一列的明細窗。 */
async function openException(page, message) {
  await page.goto('/system-exceptions');
  await page.waitForSelector('.exception-log-table', { timeout: 60000 });
  await page.fill('.exception-log-search-input', seedTag);
  await clickToolbar(page, '查詢');
  await waitForPredicate(page, () => document.querySelectorAll('.exception-log-table .ant-table-row').length === 3, null, { timeout: 30000 });
  await page.locator('.exception-log-table .ant-table-row', { hasText: message }).first().locator('button', { hasText: '查看' }).click();
  await page.waitForSelector('.exception-log-modal .ant-modal-content', { state: 'visible', timeout: 15000 });
}

async function startExceptionAi(page) {
  const button = page.locator('.exception-log-detail-actions button');
  await waitForPredicate(page, () => {
    const b = document.querySelector('.exception-log-detail-actions button');
    return b && !b.disabled;
  }, null, { timeout: 15000 });
  await button.click();
  await aiModal(page).locator('.ant-modal-content').waitFor({ state: 'visible', timeout: 15000 });
}

/** 每 200ms 取一次產生中文字的長度，直到停止產生的按鈕消失（完成）。回傳看到的不同長度。 */
async function watchStreaming(page, scope, { timeout = 600000 } = {}) {
  const lengths = [];
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const state = await page.evaluate((css) => {
      const root = document.querySelector(css);
      if (!root) {
        return null;
      }

      const generating = [...root.querySelectorAll('button')].some((b) => b.textContent.includes('停止產生') && b.offsetParent !== null);
      const reports = root.querySelectorAll('[class$="-report"]');
      const text = reports.length ? reports[reports.length - 1].textContent : '';
      return { generating, length: text.length };
    }, scope);
    if (state && state.length > 0 && lengths[lengths.length - 1] !== state.length) {
      lengths.push(state.length);
    }

    if (state && !state.generating && state.length > 0) {
      return lengths;
    }

    await page.waitForTimeout(200);
  }

  throw new Error(`等不到 AI 回覆完成（看到的長度：${lengths.join(',')}）`);
}

async function waitForFirstText(page, scope, minimum = 1) {
  await waitForPredicate(page, ([css, min]) => {
    const root = document.querySelector(css);
    const reports = root ? root.querySelectorAll('[class$="-report"]') : [];
    return reports.length > 0 && reports[reports.length - 1].textContent.length >= min;
  }, [scope, minimum], { timeout: 600000 });
}

async function sendFollowUp(page, text) {
  const textarea = aiModal(page).locator('.exception-ai-composer textarea');
  await textarea.fill(text);
  await textarea.press('Tab');
  await aiModal(page).locator('.exception-ai-composer button', { hasText: '送' }).click();
}

async function download(page, trigger) {
  const [file] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), trigger()]);
  const path = await file.path();
  return { name: file.suggestedFilename(), bytes: readFileSync(path) };
}

async function runAi(browser) {
  const context = await newContext(browser);
  const page = await context.newPage();
  currentPage = page;
  const errors = watchErrors(page);
  await login(page);
  freezeTimes();
  const since = utcNow();

  await step('M6-A1', '例外 AI 分析：WASM 頁面、回覆逐字出現', async () => {
    await openException(page, '資料庫被鎖定');
    check(await isWasmPage(page), '系統例外頁不是 WASM');
    await startExceptionAi(page);
    const lengths = await watchStreaming(page, '.exception-ai-modal');
    check(lengths.length >= 3, `只看到 ${lengths.length} 種長度，沒有逐段出現`);
    await page.locator('.exception-ai-entry-label', { hasText: 'AI 分析報告' }).first().waitFor({ timeout: 15000 });
    await screenshot(page, 'm06-a1-例外AI分析');
    return `看到 ${lengths.length} 種長度，最後 ${lengths[lengths.length - 1]} 字`;
  });

  await step('M6-A2', '例外 AI 追問：第 1 輪回覆出現在對話裡', async () => {
    await sendFollowUp(page, '請用一句話總結最可能的原因。');
    await page.locator('.exception-ai-entry-label', { hasText: 'AI 回覆（追問 1）' }).waitFor({ timeout: 600000 });
    const status = await aiModal(page).locator('.exception-ai-composer-status').textContent();
    check(status.includes('已追問 1'), `追問輪數沒有更新：${status}`);
    return status.trim();
  });

  await step('M6-A3', 'AI 回覆裡的 javascript: 連結被中和', async () => {
    await sendFollowUp(page, '這是顯示測試：請只回覆下面這一行 Markdown，原樣輸出、不要加任何說明：[點我看說明](javascript:alert(1))');
    await page.locator('.exception-ai-entry-label', { hasText: 'AI 回覆（追問 2）' }).waitFor({ timeout: 600000 });
    const report = aiModal(page).locator('.exception-ai-entry-assistant .exception-ai-report').last();
    const html = await report.innerHTML();
    const dangerous = await aiModal(page).locator('a[href^="javascript" i]').count();
    check(dangerous === 0, `還有 ${dangerous} 個 javascript: 連結`);
    return html.includes('點我看說明') ? '模型照做，連結已被移除網址' : '模型沒有照做，畫面上沒有 javascript: 連結';
  });

  await step('M6-A4', '例外 AI 匯出 PDF（伺服器產生、稽核一筆）', async () => {
    const file = await download(page, () => aiModal(page).locator('button.toolbar-icon-button', { hasText: '產生 PDF 報告並下載' }).click());
    check(file.bytes.subarray(0, 4).toString() === '%PDF', '不是 PDF');
    check(/^MyProject\.Web-ai-exception-report-\d{8}-\d{6}\.pdf$/.test(file.name), `檔名不對：${file.name}`);
    await page.waitForTimeout(500);
    check(auditCount('ExceptionLog.AiExportPdf', since) === 1, 'AiExportPdf 稽核不是一筆');
    return `${file.name}，${file.bytes.length} bytes`;
  });

  await step('M6-A5', '例外 AI 稽核：初始分析 1 筆、追問 2 筆，內容不含對話', async () => {
    const rows = query("SELECT Action, Detail FROM AuditLog WHERE Action IN ('ExceptionLog.AiAnalyze','ExceptionLog.AiFollowUp') AND OccurredAt >= ? ORDER BY Id", since);
    check(rows.length === 3, `稽核 ${rows.length} 筆`);
    check(rows[0].Detail.startsWith('初始分析') && rows[2].Detail.startsWith('追問第 2 輪'), `detail 不對：${rows.map((x) => x.Detail).join(' | ')}`);
    check(!rows.some((x) => x.Detail.includes('總結')), 'detail 夾帶了追問內容');
    return rows.map((x) => x.Detail.split('；')[0]).join('、');
  });

  await step('M6-A6', '關閉對話窗＝取消，AI 對話紀錄看得到「失敗／取消」', async () => {
    await closeModal(page, aiModal(page));
    await aiModal(page).locator('.ant-modal-content').waitFor({ state: 'hidden', timeout: 15000 });
    const before = query("SELECT COUNT(*) AS N FROM AiCallLog WHERE Success = 0 AND Operation = 'AI 例外分析' AND ContentFile IS NOT NULL")[0].N;
    await startExceptionAi(page);
    await waitForFirstText(page, '.exception-ai-modal', 1);
    await closeModal(page, aiModal(page));
    await waitForToast(page, '已取消本次 AI 分析');
    let after = before;
    for (let i = 0; i < 30 && after === before; i++) {
      await page.waitForTimeout(500);
      after = query("SELECT COUNT(*) AS N FROM AiCallLog WHERE Success = 0 AND Operation = 'AI 例外分析' AND ContentFile IS NOT NULL")[0].N;
    }

    check(after === before + 1, '伺服器沒有記下被取消的呼叫');
    const reason = query("SELECT FailureReason FROM AiCallLog WHERE Success = 0 AND ContentFile IS NOT NULL ORDER BY Id DESC LIMIT 1")[0].FailureReason;
    await closeToasts(page);
    await page.goto('/ai-call-logs');
    await page.waitForSelector('.ai-call-log-toolbar', { timeout: 60000 });
    await waitForPredicate(page, () => document.querySelectorAll('.ant-table-row').length >= 4, null, { timeout: 30000 });
    const firstRow = await page.locator('.ant-table-row').first().textContent();
    check(firstRow.includes('AI 例外分析'), `最新一列不是這次呼叫：${firstRow}`);
    return `FailureReason=${reason}`;
  });

  await step('M6-A7', '停止產生：保留已產生的部分並標示已中止', async () => {
    await openException(page, '寄信逾時');
    await startExceptionAi(page);
    await waitForFirstText(page, '.exception-ai-modal', 40);
    await aiModal(page).locator('.exception-ai-generating button', { hasText: '停止產生' }).click();
    await waitForToast(page, '已停止產生，保留已產生的內容');
    const text = await aiModal(page).locator('.exception-ai-entry-assistant .exception-ai-report').last().textContent();
    check(text.includes('本內容於產生途中由使用者停止'), '沒有「已中止」註記');
    await screenshot(page, 'm06-a7-停止產生');
    await closeModal(page, aiModal(page));
    return `保留 ${text.length} 字`;
  });

  await step('M6-A8', '日誌 AI 分析：逐字出現、完成通知、匯出 PDF', async () => {
    await closeToasts(page);
    // 先等開頁的第一次查詢回來：它會把起訖時間設成伺服器套用的值，太早填會被蓋掉（同 m06-views）。
    const initialSearch = page.waitForResponse((r) => r.url().includes('/api/v1/logs/search'), { timeout: 60000 });
    await page.goto('/logs');
    await page.waitForSelector('.log-view-table', { timeout: 60000 });
    await initialSearch;
    await page.waitForTimeout(500);
    check(await isWasmPage(page), '日誌檢視頁不是 WASM');
    await fillPicker(page, '.log-view-toolbar-left', 0, `${day} 09:00:00`);
    await fillPicker(page, '.log-view-toolbar-left', 1, `${day} 10:00:00`);
    await page.locator('.log-view-text-filters input').first().fill(seedTag);
    await clickToolbar(page, '查詢');
    await waitForPredicate(page, () => document.querySelectorAll('.log-view-table .ant-table-row').length === 5, null, { timeout: 30000 });
    await page.locator('button.toolbar-icon-button', { hasText: 'AI 分析' }).click();
    await logAiModal(page).locator('.ant-modal-content').waitFor({ state: 'visible', timeout: 15000 });
    const lengths = await watchStreaming(page, '.log-ai-modal');
    check(lengths.length >= 3, `只看到 ${lengths.length} 種長度`);
    await waitForToast(page, 'AI 分析完成（分析了 5 筆）', 30000);
    const file = await download(page, () => logAiModal(page).locator('button.toolbar-icon-button', { hasText: '匯出 PDF 報告並下載' }).click());
    check(file.bytes.subarray(0, 4).toString() === '%PDF', '不是 PDF');
    await page.waitForTimeout(500);
    check(auditCount('LogViewer.AiAnalyze', since) === 1 && auditCount('LogViewer.AiAnalyzeExportPdf', since) === 1, '日誌 AI 稽核筆數不對');
    await screenshot(page, 'm06-a8-日誌AI分析');
    await closeModal(page, logAiModal(page));
    return `${lengths.length} 種長度，${file.name}`;
  });

  recorder.add('M6-A9', 'AI 情境期間沒有未處理的瀏覽器錯誤', errors.length === 0, errors.length === 0 ? '0 個' : errors.slice(0, 3).join(' | '));
  await context.close();
}

async function runQuota(browser) {
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  // 今天已用 5 元（上限 1 元）：AI 分析在送出前就被擋，不會真的呼叫模型。
  const supportId = query("SELECT Id FROM MyUser WHERE Account = 'support'")[0].Id;
  execute(
    `INSERT INTO TokenUsageLog (OccurredAt, Operation, CallKind, Provider, Model, Success, ElapsedMilliseconds, InputCount, OutputCount, TotalCount,
       CostUsd, CostTwd, CostExchangeRate, CostLongContext, Account, UserId)
     VALUES (datetime('now', 'localtime'), 'AI 例外分析', 'Chat', 'OpenAI', 'gpt-e2e', 1, 100, 10, 10, 20, 0.2, 5, 32.5, 0, 'support', ?)`, supportId);
  const calls = query('SELECT COUNT(*) AS N FROM AiCallLog')[0].N;

  await step('M6-Q1', '超過每人每日用量上限：對話窗顯示上限訊息、沒有呼叫模型', async () => {
    await openException(page, '資料庫被鎖定');
    await startExceptionAi(page);
    await aiModal(page).locator('.exception-ai-failed').waitFor({ timeout: 60000 });
    const message = await aiModal(page).locator('.exception-ai-failed-message').textContent();
    check(message.includes('AI 用量上限'), `訊息不對：${message}`);
    check(query('SELECT COUNT(*) AS N FROM AiCallLog')[0].N === calls, '居然呼叫了模型');
    await screenshot(page, 'm06-q1-超過用量上限');
    return message.trim();
  });

  await context.close();
}

seedAiData();
if (query('SELECT Id FROM MyUser WHERE Account = ?', 'e2e-m6-user').length === 0) {
  createUser('e2e-m6-user', userPassword);
}

const browser = await launchBrowser();
try {
  if (quotaMode) {
    await runQuota(browser);
  } else {
    await runAi(browser);
  }
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
