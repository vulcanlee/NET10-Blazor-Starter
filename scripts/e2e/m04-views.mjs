// M4 外觀比對用的固定畫面（0.10.3 起，WASM 遷移 M4）：在隔離資料庫佈置固定的專案（含附件紀錄），
// 依序拍下專案頁的各個狀態。改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。
// 沿用 m03-views 的團隊、分類與唯讀角色，唯讀角色另加「專案項目:view」。資料只佈置一次，同一個資料庫可以重跑。
import { execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { readerAccount, readerPassword, seedFixedData as seedM03 } from './m03-views.mjs';

const fixedTime = '2026-11-01 09:00:00';

/** 佈置固定資料（時間固定，畫面上的時間欄位才不會每次不同）。 */
export function seedProjects() {
  seedM03();
  if (query("SELECT Id FROM Project WHERE Title = 'E2E 專案甲'").length > 0) {
    return;
  }

  const project = (title, status, priority, percent, owner, categories, teams, deleted = false) => {
    execute(
      `INSERT INTO Project (Title, Description, StartDate, EndDate, Status, Priority, CompletionPercentage, Owner, Categories, Teams,
                            CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted, DeletedAt, DeletedBy)
       VALUES (?, ?, '2026-11-01 00:00:00', '2026-12-31 00:00:00', ?, ?, ?, ?, ?, ?, ?, ?, lower(hex(randomblob(16))), ?, ?, ?)`,
      title, `${title}的說明`, status, priority, percent, owner, categories, teams, fixedTime, fixedTime,
      deleted ? 1 : 0, deleted ? fixedTime : null, deleted ? 'support' : null);
    return query('SELECT Id FROM Project WHERE Title = ?', title)[0].Id;
  };

  const a = project('E2E 專案甲', '進行中', '高', 40, '王小明', '\nE2E 硬體\n', '\nE2E 研發部\n');
  project('E2E 專案乙', '未開始', '中', 0, '李小華', '\nE2E 軟體\n', null);
  project('E2E 專案丙', '已完成', '低', 100, '陳大同', null, '\nE2E 業務部\n');
  project('E2E 舊專案', '暫緩', '中', 10, '林小美', null, null, true);

  // 附件只佈置紀錄（畫面只顯示檔名與大小；外觀比對不下載）。
  const file = (name, size) => execute(
    `INSERT INTO ProjectFile (ProjectId, OriginalFileName, StoredFileName, RelativePath, ContentType, FileSize, CreatedAt)
     VALUES (?, ?, ?, ?, 'application/pdf', ?, ?)`,
    a, name, `${name}.bin`, `2026/11/${name}.bin`, size, fixedTime);
  file('需求規格.pdf', 1048576);
  file('會議紀錄.pdf', 20480);

  // 唯讀角色加上專案的「檢視」。
  const roleId = query("SELECT Id FROM RoleView WHERE Name = 'E2E 唯讀'")[0].Id;
  const key = '專案項目:view';
  if (query('SELECT Id FROM Permission WHERE Key = ?', key).length === 0) {
    execute('INSERT INTO Permission (Key, DisplayName, SortOrder) VALUES (?, ?, 0)', key, key);
  }
  const permissionId = query('SELECT Id FROM Permission WHERE Key = ?', key)[0].Id;
  execute('INSERT INTO RolePermissionMap (RoleViewId, PermissionId) VALUES (?, ?)', roleId, permissionId);
}

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(800);
}

async function waitForRows(page) {
  await page.waitForSelector('.project-view-table .ant-table-row', { timeout: 30000 });
  await settle(page);
}

/** 拍下所有固定畫面，回傳檔名清單。前置：已佈置資料。 */
export async function captureViews(browser, prefix) {
  const files = [];
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);

  await page.goto('/projects');
  await waitForRows(page);
  files.push(await screenshot(page, `${prefix}-01-專案清單`));

  await page.locator('.ant-table-row', { hasText: 'E2E 專案甲' }).first().locator('button.crud-action-button', { hasText: '修改' }).click();
  await page.waitForSelector('.ant-modal-content', { state: 'visible', timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-02-專案修改窗`));
  await page.locator('.form-modal .ant-modal-body').evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-03-專案修改窗附件`));
  await page.locator('.form-modal .ant-modal-close').click();
  await page.waitForSelector('.ant-modal-content', { state: 'hidden', timeout: 15000 });

  await page.locator('button.toolbar-icon-button', { hasText: '顯示已刪除' }).click();
  await page.waitForSelector('.project-view-table .ant-table-row:has-text("E2E 舊專案")', { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-04-專案已刪除`));
  await context.close();

  const readerContext = await newContext(browser);
  const reader = await readerContext.newPage();
  await login(reader, { account: readerAccount, password: readerPassword });
  await reader.goto('/projects');
  await waitForRows(reader);
  files.push(await screenshot(reader, `${prefix}-05-唯讀專案清單`));
  await readerContext.close();

  return files;
}

// 直接執行：node m04-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  seedProjects();
  const browser = await launchBrowser();
  try {
    const files = await captureViews(browser, process.argv[2] ?? 'view');
    console.log(files.join('\n'));
  } finally {
    await browser.close();
  }
}
