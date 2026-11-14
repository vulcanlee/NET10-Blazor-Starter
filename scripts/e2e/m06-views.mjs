// M6 外觀比對用的固定畫面（0.10.5 起，WASM 遷移 M6）：在隔離資料庫佈置固定的例外、AI 對話紀錄、Token 用量與提示詞版本，
// 另寫一個固定日期（2026-11-01）的日誌檔，拍下日誌檢視、日誌等級、系統例外、AI 對話紀錄、Token 用量、AI 提示詞頁。
// 改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。資料只佈置一次，同一個資料庫可以重跑。
import { mkdirSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { databaseFile, execute, launchBrowser, login, newContext, query, screenshot, waitForPredicate } from './common.mjs';
import { freezeTimes } from './m05-views.mjs';

export const seedTag = 'E2E種子';
const day = '2026-11-01';

/** 日誌檔：與網站自己的日誌同一個資料夾，檔名換成固定日期。 */
export function seedLogFile() {
  const logRoot = join(dirname(dirname(databaseFile)), 'logs');
  const folder = readdirSync(logRoot, { withFileTypes: true }).find(entry => entry.isDirectory())?.name ?? 'MyProject.Web';
  const directory = join(logRoot, folder);
  mkdirSync(directory, { recursive: true });
  const line = (time, level, logger, message, exception = '') =>
    `${day} ${time}|E2E-TRACE-${time.replaceAll(':', '').slice(0, 6)}|${level}|7|${logger}|${seedTag} ${message}|${exception}`;
  const lines = [
    line('09:00:01.1000', 'INFO', 'MyProject.Web.Program', '網站啟動完成'),
    line('09:00:05.2000', 'DEBUG', 'MyProject.Business.Services.DataAccess.ProjectService', '查詢專案 12 筆'),
    line('09:01:10.3000', 'WARN', 'MyProject.Web.Auth.LoginService', '帳號 e2e-wang 密碼錯誤（第 2 次）'),
    line('09:02:20.4000', 'ERROR', 'MyProject.Web.Controllers.ProjectController', '儲存專案失敗',
      'System.InvalidOperationException: 測試用例外\n   at MyProject.Web.Controllers.ProjectController.Save()'),
    line('09:03:30.5000', 'INFO', 'MyProject.Web.Jobs.BackupJob', '備份完成，大小 1.2 MB'),
  ];
  writeFileSync(join(directory, `${folder}-logfile-${day}.log`), lines.join('\n') + '\n', 'utf8');
}

/** 佈置固定資料。 */
export function seedAiData() {
  seedLogFile();
  if (query('SELECT Id FROM ExceptionLog WHERE Message LIKE ?', `${seedTag}%`).length > 0) {
    return;
  }

  // 系統例外（堆疊檔不存在：明細窗顯示說明文字）。
  const exception = (n, type, message, source, page, account, count, last) => execute(
    `INSERT INTO ExceptionLog (Signature, ExceptionType, Message, Source, Page, Operation, LoggerName, Account, UserId,
       FirstOccurredAt, LastOccurredAt, OccurrenceCount, LastTraceId, StackTraceFile)
     VALUES (?, ?, ?, ?, ?, ?, 'MyProject.Web.Test', ?, NULL, '${day} 08:00:00', ?, ?, ?, NULL)`,
    `e2e-signature-${n}`, type, `${seedTag} ${message}`, source, page, n === 1 ? 'OnSaveAsync' : null, account, last, count, `E2E-TRACE-${n}`);
  exception(1, 'System.InvalidOperationException', '儲存專案時狀態不正確', '畫面', '/projects', 'e2e-wang', 3, `${day} 09:10:00`);
  exception(2, 'System.TimeoutException', '寄信逾時', '背景作業', null, null, 120, `${day} 09:20:00`);
  exception(3, 'Microsoft.Data.Sqlite.SqliteException', '資料庫被鎖定', 'WebAPI', '/api/v1/projects', 'support', 12, `${day} 09:30:00`);

  // AI 對話紀錄（沒有內容檔）。
  const call = (n, operation, success, model, elapsed, chars, related, failure) => execute(
    `INSERT INTO AiCallLog (CallId, OccurredAt, Operation, Provider, Model, Success, FailureReason, FinishReason, HttpStatus,
       ElapsedMilliseconds, RequestCharacters, ResponseCharacters, Account, UserId, RelatedInfo, ContentFile)
     VALUES (?, ?, ?, 'OpenAI', ?, ?, ?, ?, ?, ?, ?, ?, 'support', 1, ?, NULL)`,
    `00000000-0000-4000-8000-00000000000${n}`, `${day} 09:0${n}:00`, operation, model, success ? 1 : 0, failure ?? null,
    success ? 'stop' : null, success ? 200 : null, elapsed, chars, success ? 1800 : 0, `${seedTag} ${related}`);
  call(1, 'AI 日誌分析', true, 'gpt-e2e-mini', 8200, 12000, '日誌區間 09:00～10:00');
  call(2, 'AI 例外分析', true, 'gpt-e2e', 15300, 4200, '例外紀錄 #1');
  call(3, 'AI 例外分析', false, 'gpt-e2e', 600, 4300, '例外紀錄 #2', 'Canceled');

  // Token 用量（帳號 e2e-ai，以帳號篩出來拍）。
  const usage = (n, hour, operation, model, input, output, cost) => execute(
    `INSERT INTO TokenUsageLog (OccurredAt, Operation, CallKind, Provider, Model, Success, ElapsedMilliseconds,
       InputCount, OutputCount, TotalCount, CachedInputCount, ReasoningCount, CostUsd, CostTwd, CostExchangeRate, CostLongContext,
       Account, UserId, CallId)
     VALUES (?, ?, 'Chat', 'OpenAI', ?, 1, 5000, ?, ?, ?, 0, 0, ?, ?, 32.5, 0, 'e2e-ai', NULL, ?)`,
    `${day} ${hour}:00:00`, operation, model, input, output, input + output, cost / 32.5, cost, `00000000-0000-4000-9000-00000000000${n}`);
  usage(1, '09', 'AI 日誌分析', 'gpt-e2e-mini', 12000, 1500, 0.8);
  usage(2, '10', 'AI 例外分析', 'gpt-e2e', 4200, 900, 2.4);
  usage(3, '11', 'AI 例外分析', 'gpt-e2e', 5100, 1200, 3.1);

  // 提示詞：日誌分析兩個版本，第 2 版作用中。
  const prompt = (version, active, content, note) => execute(
    `INSERT INTO PromptTemplate (TemplateKey, Version, Content, Note, IsActive, CreatedAtUtc, CreatedByAccount)
     VALUES ('LogAnalysis', ?, ?, ?, ?, ?, 'support')`, version, content, note, active ? 1 : 0, `${day} 0${version}:00:00`);
  prompt(1, false, '請依時間順序整理錯誤，指出最可能的原因。', `${seedTag} 第一版`);
  prompt(2, true, '請依時間順序整理錯誤，指出最可能的原因，並列出建議的處理步驟。', `${seedTag} 加上處理步驟`);
}

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(800);
}

async function toolbar(page, title) {
  await page.locator('button.toolbar-icon-button', { hasText: title }).first().click();
}

async function closeModal(page) {
  await page.locator('.ant-modal-close').last().click();
  await page.waitForSelector('.ant-modal-content', { state: 'hidden', timeout: 15000 });
}

async function rowCount(page, tableClass, count) {
  await waitForPredicate(page, 
    ([css, n]) => document.querySelectorAll(`${css} .ant-table-row`).length === n,
    [`.${tableClass}`, count], { timeout: 30000 });
  await settle(page);
}

/** AntDesign 日期欄位：點開、輸入、Enter 確定，再確認欄位真的是這個值（沒吃進去就直接報錯，不要拍錯的畫面）。 */
export async function fillPicker(page, scope, index, value) {
  const input = page.locator(`${scope} .ant-picker input`).nth(index);
  for (let attempt = 0; attempt < 3; attempt++) {
    // 等面板打開後再用鍵盤輸入：太早 fill 會被面板打開時的重新渲染蓋回原值（0.10.9 實機發現，同一頁第二個欄位最明顯）。
    await input.click();
    await page.locator('.ant-picker-dropdown:not(.ant-picker-dropdown-hidden)').first().waitFor({ timeout: 5000 }).catch(() => {});
    await page.waitForTimeout(300);
    await input.press('Control+A');
    await input.pressSequentially(value, { delay: 10 });
    await page.waitForTimeout(300);
    // 只有日期的欄位：點面板上那一格才會確定；含時間的欄位：按面板的「確定」（與真人操作相同）。
    // 0.10.9 實機發現：AntDesign 含時間的欄位以 Enter 確定，同一頁第二個以後的欄位會吃不進去（按「確定」正常），所以 Enter 只當最後手段。
    const dropdown = page.locator('.ant-picker-dropdown:not(.ant-picker-dropdown-hidden)');
    const cell = dropdown.locator(`td[title="${value}"]`);
    const ok = dropdown.locator('.ant-picker-ok button');
    if (await cell.count() > 0) {
      await cell.first().click();
    } else if (await ok.count() > 0 && await ok.first().isEnabled()) {
      await ok.first().click();
    } else {
      await input.press('Enter');
    }
    await page.waitForTimeout(500);
    if (await input.inputValue() === value) {
      return;
    }
  }

  throw new Error(`日期欄位沒有吃進「${value}」（現在是「${await input.inputValue()}」）`);
}

/** 拍下所有固定畫面，回傳檔名清單。前置：已佈置資料。 */
export async function captureViews(browser, prefix) {
  const files = [];
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  freezeTimes();
  try {
    await captureAll(page, prefix, files);
  } catch (error) {
    await screenshot(page, `${prefix}-失敗當下`);
    throw error;
  }

  await context.close();
  return files;
}

async function captureAll(page, prefix, files) {
  // 先等開頁的第一次查詢回來：它會把起訖時間設成伺服器實際套用的值，太早填會被蓋掉（0.10.9 實機發現）。
  const initialSearch = page.waitForResponse((r) => r.url().includes('/api/v1/logs/search'), { timeout: 60000 });
  await page.goto('/logs');
  await page.waitForSelector('.log-view-table', { timeout: 60000 });
  await initialSearch;
  await page.waitForTimeout(500);
  await fillPicker(page, '.log-view-toolbar-left', 0, `${day} 09:00:00`);
  await fillPicker(page, '.log-view-toolbar-left', 1, `${day} 10:00:00`);
  await page.locator('.log-view-text-filters input').first().fill(seedTag);
  await toolbar(page, '查詢');
  await rowCount(page, 'log-view-table', 5);
  files.push(await screenshot(page, `${prefix}-01-日誌檢視`));

  await page.goto('/log-level-setting');
  await page.waitForSelector('.ant-radio-group', { timeout: 60000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-02-日誌等級`));

  await page.goto('/system-exceptions');
  await page.waitForSelector('.exception-log-table', { timeout: 60000 });
  await page.fill('.exception-log-search-input', seedTag);
  await toolbar(page, '查詢');
  await rowCount(page, 'exception-log-table', 3);
  files.push(await screenshot(page, `${prefix}-03-系統例外`));

  await page.locator('.exception-log-table .ant-table-row', { hasText: '資料庫被鎖定' }).first().locator('button', { hasText: '查看' }).click();
  await page.waitForSelector('.ant-modal-content', { state: 'visible', timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-04-例外明細`));
  await closeModal(page);

  await page.goto('/ai-call-logs');
  await page.waitForSelector('.ai-call-log-toolbar', { timeout: 60000 });
  await page.locator('.ai-call-log-toolbar-left input[placeholder^="關聯說明"]').fill(seedTag);
  await toolbar(page, '查詢');
  await waitForPredicate(page, () => document.querySelectorAll('.ant-table-row').length === 3, null, { timeout: 30000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-05-AI對話紀錄`));

  await page.goto('/token-usage');
  await page.waitForSelector('.token-usage-toolbar', { timeout: 60000 });
  await page.fill('.token-usage-account-input', 'e2e-ai');
  await fillPicker(page, '.token-usage-toolbar-left', 1, '2026-11-02');
  await toolbar(page, '查詢');
  await page.waitForSelector('.ant-tabs-tab:has-text("明細（3）")', { timeout: 30000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-06-Token用量`));

  await page.locator('.ant-tabs-tab', { hasText: '明細（3）' }).click();
  await waitForPredicate(page, () => document.querySelectorAll('.ant-tabs-tabpane-active .ant-table-row').length === 3, null, { timeout: 30000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-07-Token明細`));

  await page.goto('/prompt-templates');
  await page.waitForSelector('.prompt-table', { timeout: 60000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-08-AI提示詞`));

  await page.locator('.prompt-main button', { hasText: '編輯' }).first().click();
  await page.waitForSelector('.ant-modal-content', { state: 'visible', timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-09-提示詞編輯窗`));
}

// 直接執行：node m06-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  seedAiData();
  const browser = await launchBrowser();
  try {
    const files = await captureViews(browser, process.argv[2] ?? 'view');
    console.log(files.join('\n'));
  } finally {
    await browser.close();
  }
}
