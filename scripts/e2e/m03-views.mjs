// M3 外觀比對用的固定畫面（0.10.2 起，WASM 遷移 M3）：在隔離資料庫佈置固定的分類、團隊與唯讀角色，
// 依序拍下分類頁、團隊頁的各個狀態。改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。
// 用法見 README.md；資料只佈置一次（以分類「E2E 硬體」是否存在判斷），同一個資料庫可以重跑。
import { createUser, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';

export const readerAccount = 'e2e-reader';
export const readerPassword = 'Reader!E2e2026';

const fixedTime = '2026-11-01 09:00:00';

/** 佈置固定資料（時間固定，畫面上的「更新時間」才不會每次不同）。 */
export function seedFixedData() {
  if (query("SELECT Id FROM MyUser WHERE Account = ?", readerAccount).length > 0) {
    return;
  }

  const team = (name, code, parentId, deleted = false) => {
    execute(
      `INSERT INTO Team (Name, Code, Description, ParentId, IsEnabled, CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted, DeletedAt, DeletedBy)
       VALUES (?, ?, NULL, ?, 1, ?, ?, lower(hex(randomblob(16))), ?, ?, ?)`,
      name, code, parentId, fixedTime, fixedTime, deleted ? 1 : 0, deleted ? fixedTime : null, deleted ? 'support' : null);
    return query('SELECT Id FROM Team WHERE Name = ?', name)[0].Id;
  };
  const rd = team('E2E 研發部', 'RD', null);
  team('E2E 研發一課', 'RD1', rd);
  team('E2E 研發二課', 'RD2', rd);
  team('E2E 業務部', 'SALES', null);
  team('E2E 舊部門', 'OLD', null, true);

  const category = (name, description, teams, enabled, deleted = false) => execute(
    `INSERT INTO Category (Name, Description, Teams, IsEnabled, CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted, DeletedAt, DeletedBy)
     VALUES (?, ?, ?, ?, ?, ?, lower(hex(randomblob(16))), ?, ?, ?)`,
    name, description, teams, enabled ? 1 : 0, fixedTime, fixedTime, deleted ? 1 : 0, deleted ? fixedTime : null, deleted ? 'support' : null);
  category('E2E 硬體', '電腦、周邊與零件', '\nE2E 研發部\n', true);
  category('E2E 軟體', '授權與訂閱', null, true);
  category('E2E 行銷', '活動與廣告', '\nE2E 業務部\n', false);
  category('E2E 過期分類', '已不使用', null, true, true);

  // 唯讀角色：分類、團隊只有「檢視」。
  execute(
    `INSERT INTO RoleView (Name, TabViewJson, DefaultTeamsJson, RequireTwoFactor, CreateAt, UpdateAt, ConcurrencyStamp, IsDeleted)
     VALUES ('E2E 唯讀', '[]', '[]', 0, ?, ?, lower(hex(randomblob(16))), 0)`, fixedTime, fixedTime);
  const roleId = query("SELECT Id FROM RoleView WHERE Name = 'E2E 唯讀'")[0].Id;
  for (const key of ['分類清單:view', '團隊清單:view']) {
    // 動作鍵的權限列在角色頁第一次存檔時才建立（RbacWriteService），這裡比照補上。
    if (query('SELECT Id FROM Permission WHERE Key = ?', key).length === 0) {
      execute('INSERT INTO Permission (Key, DisplayName, SortOrder) VALUES (?, ?, 0)', key, key);
    }
    const permissionId = query('SELECT Id FROM Permission WHERE Key = ?', key)[0].Id;
    execute('INSERT INTO RolePermissionMap (RoleViewId, PermissionId) VALUES (?, ?)', roleId, permissionId);
  }

  createUser(readerAccount, readerPassword);
  const readerId = query('SELECT Id FROM MyUser WHERE Account = ?', readerAccount)[0].Id;
  execute('UPDATE MyUser SET RoleViewId = ? WHERE Id = ?', roleId, readerId);
  execute('DELETE FROM UserRole WHERE MyUserId = ?', readerId);
  execute('INSERT INTO UserRole (MyUserId, RoleViewId) VALUES (?, ?)', readerId, roleId);
}

/** 等表格畫完（Server 版走電路、WASM 版走 API，兩邊都等到資料列出現）。 */
async function waitForRows(page, tableClass) {
  await page.waitForSelector(`.${tableClass} .ant-table-row`, { timeout: 30000 });
  await settle(page);
}

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(800);
}

async function openModal(page, rowText, title) {
  await page.locator('.ant-table-row', { hasText: rowText }).first().locator('button.crud-action-button', { hasText: title }).click();
  await page.waitForSelector('.ant-modal-content', { state: 'visible', timeout: 15000 });
  await settle(page);
}

async function closeModal(page) {
  await page.keyboard.press('Escape');
  await page.waitForSelector('.ant-modal-content', { state: 'hidden', timeout: 15000 });
}

async function toolbar(page, title) {
  await page.locator('button.toolbar-icon-button', { hasText: title }).click();
}

/** 拍下所有固定畫面，回傳檔名清單。前置：已佈置資料。 */
export async function captureViews(browser, prefix) {
  const files = [];
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);

  await page.goto('/categories');
  await waitForRows(page, 'category-view-table');
  files.push(await screenshot(page, `${prefix}-01-分類清單`));

  await openModal(page, 'E2E 硬體', '修改');
  files.push(await screenshot(page, `${prefix}-02-分類修改窗`));
  await closeModal(page);

  await toolbar(page, '顯示已刪除');
  await page.waitForSelector('.category-view-table .ant-table-row:has-text("E2E 過期分類")', { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-03-分類已刪除`));

  await page.goto('/teams');
  await waitForRows(page, 'team-view-table');
  files.push(await screenshot(page, `${prefix}-04-團隊樹`));

  await page.locator('.ant-table-row', { hasText: 'E2E 研發部' }).first().locator('.ant-table-row-expand-icon').click();
  await page.waitForSelector('.team-view-table .ant-table-row:has-text("E2E 研發一課")', { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-05-團隊樹展開`));

  await openModal(page, 'E2E 研發一課', '修改');
  files.push(await screenshot(page, `${prefix}-06-團隊修改窗`));
  await closeModal(page);

  await page.fill('.team-view-search-input', 'E2E 研發');
  await toolbar(page, '搜尋');
  await page.waitForSelector('.team-view-table .ant-table-row:has-text("E2E 研發二課")', { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-07-團隊搜尋`));
  await context.close();

  const readerContext = await newContext(browser);
  const reader = await readerContext.newPage();
  await login(reader, { account: readerAccount, password: readerPassword });
  await reader.goto('/categories');
  await waitForRows(reader, 'category-view-table');
  files.push(await screenshot(reader, `${prefix}-08-唯讀分類清單`));
  await readerContext.close();

  return files;
}

// 直接執行：node m03-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  seedFixedData();
  const browser = await launchBrowser();
  try {
    const files = await captureViews(browser, process.argv[2] ?? 'view');
    console.log(files.join('\n'));
  } finally {
    await browser.close();
  }
}
