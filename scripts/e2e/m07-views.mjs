// M7 外觀比對用的固定畫面（0.10.6 起，WASM 遷移 M7）：佈置固定的公告與兩個備份檔，拍下排程作業、系統健康、系統備份、資料保留、
// 資料庫用量、系統參數、公告管理、首頁（管理員與一般使用者）。改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。
// ⚠️ 系統健康、資料庫用量、排程作業的「下次執行」含即時數值（記憶體、檔案大小、時間），這幾張的差異集中在數值處，比對時另外說明。
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { createUser, databaseFile, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { freezeTimes } from './m05-views.mjs';

export const seedTag = 'E2E種子';
export const userAccount = 'e2e-m7-user';
export const userPassword = 'User!E2e2026x';

/** 空的 zip（只有結尾目錄）：備份清單只看檔名、大小與內含的 manifest（沒有就顯示未知）。 */
const emptyZip = Buffer.from([0x50, 0x4b, 0x05, 0x06, ...new Array(18).fill(0)]);

export function seedOpsData() {
  const backupDir = join(dirname(dirname(databaseFile)), 'Backup');
  mkdirSync(backupDir, { recursive: true });
  writeFileSync(join(backupDir, 'backup-20261101-090000.zip'), emptyZip);
  writeFileSync(join(backupDir, 'backup-20261101-100000-2.zip'), emptyZip);

  if (query('SELECT Id FROM MyUser WHERE Account = ?', userAccount).length === 0) {
    createUser(userAccount, userPassword);
  }

  if (query('SELECT Id FROM Announcement WHERE Title LIKE ?', `${seedTag}%`).length > 0) {
    return;
  }

  const announcement = (title, content, start, end) => execute(
    `INSERT INTO Announcement (Title, Content, StartAtUtc, EndAtUtc, TargetKind, TargetId, CreatedAtUtc, UpdatedAtUtc, CreatedByAccount, ConcurrencyStamp)
     VALUES (?, ?, ?, ?, 'All', NULL, '2026-11-01 01:00:00', '2026-11-01 01:00:00', 'support', lower(hex(randomblob(16))))`, `${seedTag} ${title}`, content, start, end);
  announcement('系統維護通知', '11/30 晚上 10 點到 12 點暫停服務。', '2026-11-01 01:00:00', '2099-12-31 15:59:00');
  announcement('已結束的公告', '這則已經過期。', '2026-10-01 01:00:00', '2026-10-31 01:00:00');
  announcement('排定中的公告', '這則還沒開始。', '2099-01-01 01:00:00', null);
}

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(1200);
}

async function open(page, route, selector) {
  await page.goto(route);
  await page.waitForSelector(selector, { timeout: 60000 });
  await settle(page);
}

/** 拍下所有固定畫面，回傳檔名清單。 */
export async function captureViews(browser, prefix) {
  const files = [];
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  freezeTimes();
  try {
    await open(page, '/scheduled-jobs', '.scheduled-job-table .ant-table-row');
    files.push(await screenshot(page, `${prefix}-01-排程作業`));
    await open(page, '/system-health', '.system-health-page');
    files.push(await screenshot(page, `${prefix}-02-系統健康`));
    await open(page, '/backups', '.backup-table .ant-table-row');
    files.push(await screenshot(page, `${prefix}-03-系統備份`));
    await open(page, '/data-retention', '.data-retention-table .ant-table-row');
    files.push(await screenshot(page, `${prefix}-04-資料保留`));
    await open(page, '/database-usage', '.ant-table-row');
    files.push(await screenshot(page, `${prefix}-05-資料庫用量`));
    await open(page, '/system-parameters', '.system-parameter-table .ant-table-row');
    files.push(await screenshot(page, `${prefix}-06-系統參數`));
    await open(page, '/announcements', '.ant-table-row');
    files.push(await screenshot(page, `${prefix}-07-公告管理`));
    await open(page, '/App', '.home-welcome');
    files.push(await screenshot(page, `${prefix}-08-首頁管理員`));
  } catch (error) {
    await screenshot(page, `${prefix}-失敗當下`);
    throw error;
  }

  await context.close();

  const userContext = await newContext(browser);
  const userPage = await userContext.newPage();
  await login(userPage, { account: userAccount, password: userPassword });
  await open(userPage, '/App', '.home-welcome');
  files.push(await screenshot(userPage, `${prefix}-09-首頁一般使用者`));
  await userContext.close();
  return files;
}

// 直接執行：node m07-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  seedOpsData();
  const browser = await launchBrowser();
  try {
    const files = await captureViews(browser, process.argv[2] ?? 'view');
    console.log(files.join('\n'));
  } finally {
    await browser.close();
  }
}
