// M5 外觀比對用的固定畫面（0.10.4 起，WASM 遷移 M5）：在隔離資料庫佈置固定的使用者、角色、稽核紀錄與寄信紀錄，
// 拍下使用者、角色、稽核紀錄、寄信紀錄頁的各個狀態。改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。
// 沿用 m03-views 的團隊與唯讀角色。資料只佈置一次，同一個資料庫可以重跑。
import { createUser, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { seedFixedData as seedM03 } from './m03-views.mjs';

const fixedTime = '2026-11-01 09:00:00';
export const seedTag = 'E2E種子';

/** 佈置固定資料。 */
export function seedAdminData() {
  seedM03();
  if (query("SELECT Id FROM MyUser WHERE Account = 'e2e-wang'").length > 0) {
    return;
  }

  // 角色：有預設團隊與部分權限的「E2E 主管」。
  execute(
    `INSERT INTO RoleView (Name, TabViewJson, DefaultTeamsJson, RequireTwoFactor, CreateAt, UpdateAt, ConcurrencyStamp, IsDeleted)
     VALUES ('E2E 主管', ?, '["E2E 研發部"]', 1, ?, ?, lower(hex(randomblob(16))), 0)`,
    JSON.stringify(['專案管理', '專案項目', '資料定義', '分類清單:view', '分類清單:edit']), fixedTime, fixedTime);
  execute(
    `INSERT INTO RoleView (Name, TabViewJson, DefaultTeamsJson, RequireTwoFactor, CreateAt, UpdateAt, ConcurrencyStamp, IsDeleted, DeletedAt, DeletedBy)
     VALUES ('E2E 舊角色', '[]', '[]', 0, ?, ?, lower(hex(randomblob(16))), 1, ?, 'support')`, fixedTime, fixedTime, fixedTime);
  const managerRole = query("SELECT Id FROM RoleView WHERE Name = 'E2E 主管'")[0].Id;

  // 使用者：一般、被鎖定、有兩步驟驗證、已刪除。
  const user = (account, name, email, extra = {}) => {
    createUser(account, 'Seed!E2e2026x');
    const id = query('SELECT Id FROM MyUser WHERE Account = ?', account)[0].Id;
    execute('UPDATE MyUser SET Name = ?, Email = ?, RoleViewId = ? WHERE Id = ?', name, email, managerRole, id);
    execute('DELETE FROM UserRole WHERE MyUserId = ?', id);
    execute('INSERT INTO UserRole (MyUserId, RoleViewId) VALUES (?, ?)', id, managerRole);
    for (const [column, value] of Object.entries(extra)) {
      execute(`UPDATE MyUser SET ${column} = ? WHERE Id = ?`, value, id);
    }
    return id;
  };
  const wang = user('e2e-wang', '王小明', 'wang@example.test');
  execute('INSERT INTO UserTeam (MyUserId, TeamId) VALUES (?, ?)', wang, query("SELECT Id FROM Team WHERE Name = 'E2E 研發部'")[0].Id);
  user('e2e-locked', '林鎖定', 'locked@example.test', { LockoutEndUtc: '2099-01-01 01:00:00', AccessFailedCount: 5 });
  user('e2e-2fa', '陳兩步', null, { TwoFactorEnabled: 1 });
  user('e2e-gone', '張離職', 'gone@example.test', { IsDeleted: 1, DeletedAt: fixedTime, DeletedBy: 'support' });

  // 稽核紀錄（未簽章的舊式紀錄，時間固定；以關鍵字「E2E種子」篩出來拍）。
  const audit = (minutes, action, success, targetType, targetId, detail) => execute(
    `INSERT INTO AuditLog (OccurredAt, ActorUserId, ActorAccount, Action, TargetType, TargetId, Detail, Success, ClientIp)
     VALUES (datetime('2026-11-01 01:00:00', ?), 1, 'support', ?, ?, ?, ?, ?, '10.0.0.8')`,
    `+${minutes} minutes`, action, targetType, targetId, detail, success ? 1 : 0);
  audit(1, 'User.Update', true, 'MyUser', String(wang), `${seedTag} account=e2e-wang`);
  audit(2, 'Role.Create', true, 'RoleView', String(managerRole), `${seedTag} name=E2E 主管`);
  audit(3, 'Auth.Login', false, 'MyUser', String(wang), `${seedTag} reason=密碼錯誤`);

  // 寄信紀錄（四種狀態各一筆）。
  const mail = (minutes, kind, to, subject, status, attempts, lastError) => execute(
    `INSERT INTO EmailOutbox (CreatedAtUtc, Kind, "To", Subject, Status, Attempts, NextAttemptAtUtc, SentAtUtc, LastError)
     VALUES (datetime('2026-11-01 01:00:00', ?), ?, ?, ?, ?, ?, NULL, ?, ?)`,
    `+${minutes} minutes`, kind, to, subject, status, attempts, status === 'Sent' ? '2026-11-01 01:30:00' : null, lastError);
  mail(1, 'Notification', 'wang@e2e.test', `${seedTag} 專案進度通知`, 'Sent', 1, null);
  mail(2, 'PasswordReset', 'locked@e2e.test', `${seedTag} 重設密碼`, 'Failed', 5, 'SmtpException: 550 mailbox unavailable');
  mail(3, 'Test', 'support@e2e.test', `${seedTag} 測試信`, 'Skipped', 0, null);
  mail(4, 'ExceptionAlert', 'admin@e2e.test', `${seedTag} 系統例外通知`, 'Pending', 0, null);
}

/**
 * 使用者與角色的時間固定（support、預設角色是網站啟動時建立的，每次不同）。登入之後才改，登入本身不會再動到它們。
 * support 的 Email 來自開發機的設定（可能是真的個人信箱）：隔離資料庫裡改成測試用地址，截圖才不會露出個資。
 */
export function freezeTimes() {
  execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
  execute('UPDATE MyUser SET CreateAt = ?, UpdateAt = ?', fixedTime, fixedTime);
  execute('UPDATE RoleView SET CreateAt = ?, UpdateAt = ?', fixedTime, fixedTime);
}

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(800);
}

async function waitForRows(page, tableClass) {
  await page.waitForSelector(`.${tableClass} .ant-table-row`, { timeout: 60000 });
  await settle(page);
}

async function openRowModal(page, rowText, title) {
  await page.locator('.ant-table-row', { hasText: rowText }).first().locator('button.crud-action-button', { hasText: title }).click();
  await page.waitForSelector('.ant-modal-content', { state: 'visible', timeout: 15000 });
  await settle(page);
}

async function closeModal(page) {
  await page.locator('.ant-modal-close').last().click();
  await page.waitForSelector('.ant-modal-content', { state: 'hidden', timeout: 15000 });
}

async function toolbar(page, title) {
  await page.locator('button.toolbar-icon-button', { hasText: title }).first().click();
}

/** 拍下所有固定畫面，回傳檔名清單。前置：已佈置資料。 */
export async function captureViews(browser, prefix) {
  const files = [];
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  freezeTimes();

  await page.goto('/myusers');
  await waitForRows(page, 'my-user-table');
  files.push(await screenshot(page, `${prefix}-01-使用者清單`));

  await openRowModal(page, 'e2e-wang', '修改');
  files.push(await screenshot(page, `${prefix}-02-使用者修改窗`));
  await closeModal(page);

  await toolbar(page, '顯示已刪除');
  await page.waitForSelector('.my-user-table .ant-table-row:has-text("e2e-gone")', { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-03-使用者已刪除`));

  await page.goto('/roleviews');
  await waitForRows(page, 'role-view-table');
  files.push(await screenshot(page, `${prefix}-04-角色清單`));

  await openRowModal(page, 'E2E 主管', '修改');
  files.push(await screenshot(page, `${prefix}-05-角色修改窗`));
  await closeModal(page);

  await page.goto('/audit-logs');
  await waitForRows(page, 'audit-log-table');
  await page.fill('.audit-log-search-input', seedTag);
  await toolbar(page, '查詢');
  await page.waitForFunction(() => document.querySelectorAll('.audit-log-table .ant-table-row').length === 3, null, { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-06-稽核紀錄`));

  await page.locator('.audit-log-table .ant-table-row', { hasText: 'Role.Create' }).first().locator('button', { hasText: '查看' }).click();
  await page.waitForSelector('.ant-modal-content', { state: 'visible', timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-07-稽核明細`));
  await closeModal(page);

  await page.goto('/email-logs');
  await page.waitForSelector('.email-log-table', { timeout: 60000 });
  await page.fill('.email-log-search-input', seedTag);
  await toolbar(page, '查詢');
  await page.waitForFunction(() => document.querySelectorAll('.email-log-table .ant-table-row').length === 4, null, { timeout: 15000 });
  await settle(page);
  files.push(await screenshot(page, `${prefix}-08-寄信紀錄`));

  await context.close();
  return files;
}

// 直接執行：node m05-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  seedAdminData();
  const browser = await launchBrowser();
  try {
    const files = await captureViews(browser, process.argv[2] ?? 'view');
    console.log(files.join('\n'));
  } finally {
    await browser.close();
  }
}
