// WASM 遷移 M5（0.10.4）的實機情境：使用者、角色、稽核紀錄、寄信紀錄頁在 WebAssembly 執行，資料走只限管理員的 Web API。
// 使用者完整流程、改別人的角色後對方被登出、改自己仍維持登入（換發登入）、停用帳號後對方被登出、非管理員直打管理 API 得 403、
// 複製使用者與交付窗、解鎖、角色權限矩陣存檔、稽核查詢／明細／驗證／匯出、寄信紀錄篩選、匯出 Excel、Server／WASM 切換，
// 最後跑全部路由的煙霧測試。用法見同資料夾的 README.md。結果寫到 E2E_OUT_DIR/m05-results.json 與 smoke-results.json。
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  apiAsPageUser, createRecorder, createUser, execute, isWasmPage, launchBrowser, login, newContext, outDir, query, screenshot,
} from './common.mjs';
import { clickRowAction, clickToolbar, closeToasts, confirm, openList, rowTexts, waitForToast, watchErrors } from './crud-ui.mjs';
import { readerAccount, readerPassword } from './m03-views.mjs';
import { freezeTimes, seedAdminData, seedTag } from './m05-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m05-results.json');
const smoke = createRecorder('smoke-results.json');
const userPassword = 'User!E2e2026x';

/** 目前看得到的表單對話窗（使用者頁有兩個：一般與「複製」）。 */
const modal = (page) => page.locator('.form-modal:visible');
const field = (page, label) => modal(page).locator('.ant-form-item', { has: page.locator('label', { hasText: label }) }).locator('input, textarea').first();

/** 一般帳號（主要角色「預設角色」）；admin=true 時是管理員。 */
function seedUser(account, { admin = false } = {}) {
  if (query('SELECT Id FROM MyUser WHERE Account = ?', account).length === 0) {
    createUser(account, userPassword);
  }

  execute('UPDATE MyUser SET IsAdmin = ?, Status = 1 WHERE Account = ?', admin ? 1 : 0, account);
  return query('SELECT Id FROM MyUser WHERE Account = ?', account)[0].Id;
}

/** 按確認窗的「儲存」；緊接著若跳「確認團隊設定」就按「仍要儲存」。 */
async function confirmSave(page) {
  await page.locator('.ant-modal-confirm .ant-modal-confirm-btns button', { hasText: /^\s*儲\s*存\s*$/ }).last().click();
  const team = page.locator('.ant-modal-confirm', { hasText: '確認團隊設定' });
  try {
    await team.waitFor({ state: 'visible', timeout: 2000 });
  } catch {
    return;
  }

  await team.locator('.ant-modal-confirm-btns button', { hasText: '仍要儲存' }).click();
  await team.waitFor({ state: 'hidden', timeout: 15000 });
}

/** 多選下拉加選一個選項（依下拉的標籤找）。 */
async function pickOption(page, label, option) {
  await modal(page).locator('.ant-form-item', { has: page.locator('label', { hasText: label }) }).locator('.ant-select').click();
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option', { hasText: option }).first().click();
  // 點對話窗標題收起下拉（Esc 會被對話窗當成「取消」）。
  await modal(page).locator('.ant-modal-title').click();
}

/** 送出目前的表單對話窗（按「儲存」或「建立」）。 */
async function submit(page, text = '儲存') {
  await modal(page).locator('.ant-modal-footer button', { hasText: text }).click();
}

seedAdminData();
const targetId = seedUser('e2e-target');
seedUser('e2e-target2');
seedUser('e2e-admin2', { admin: true });

const browser = await launchBrowser();
try {
  const ctx = await newContext(browser);
  const page = await ctx.newPage();
  const errors = watchErrors(page);
  await login(page);
  freezeTimes();
  const stamp = Date.now() % 100000;

  // A1：使用者完整流程（WASM）：新增、修改、停用、啟用、刪除、顯示已刪除、還原、永久刪除。
  await openList(page, '/myusers');
  const wasm = await isWasmPage(page);
  const account = `e2e-new${stamp}`;
  await clickToolbar(page, '新增');
  await modal(page).waitFor();
  await field(page, '帳號').fill(account);
  await field(page, '密碼').fill('Abcd!2345678x');
  await field(page, '名稱').fill('實機新人');
  await submit(page);
  await confirmSave(page);
  await waitForToast(page, '新增成功');
  await clickRowAction(page, account, '修改');
  await modal(page).waitFor();
  await field(page, '名稱').fill('實機新人改名');
  await submit(page);
  await confirmSave(page);
  await waitForToast(page, '修改成功');
  await page.locator('.ant-table-row', { hasText: account }).first().locator('button.status-pill-clickable').click();
  await confirm(page, '停用');
  await waitForToast(page, `已停用「${account}」`);
  await page.locator('.ant-table-row', { hasText: account }).first().locator('button.status-pill-clickable').click();
  await waitForToast(page, `已啟用「${account}」`);
  await clickRowAction(page, account, '刪除');
  await confirm(page, '刪除');
  await waitForToast(page, '刪除成功');
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: account }).first().waitFor();
  await clickRowAction(page, account, '還原');
  await confirm(page, '還原');
  await waitForToast(page, '還原成功');
  await clickToolbar(page, '返回清單');
  await page.locator('.ant-table-row', { hasText: account }).first().waitFor();
  await clickRowAction(page, account, '刪除');
  await confirm(page, '刪除');
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: account }).first().waitFor();
  await clickRowAction(page, account, '永久刪除');
  await confirm(page, '永久刪除');
  await waitForToast(page, '已永久刪除');
  await clickToolbar(page, '返回清單');
  await closeToasts(page);
  const userAudits = query("SELECT Action FROM AuditLog WHERE TargetType = 'MyUser' AND Detail LIKE ? ORDER BY Id", `%${account}%`).map((x) => x.Action);
  recorder.add('A1', '使用者完整流程（新增、修改、停用、啟用、刪除、還原、永久刪除）',
    wasm && query('SELECT COUNT(*) AS n FROM MyUser WHERE Account = ?', account)[0].n === 0 && userAudits.includes('User.Purge'),
    `WASM=${wasm}；稽核 ${userAudits.join('→')}`);

  // A2：改別人的角色：對方下一次開頁就被登出。
  const targetCtx = await newContext(browser);
  const target = await targetCtx.newPage();
  await login(target, { account: 'e2e-target', password: userPassword });
  await target.goto('/Profile');
  await target.getByText('角色與團隊').first().waitFor({ timeout: 60000 });
  await clickRowAction(page, 'e2e-target', '修改');
  await modal(page).waitFor();
  await pickOption(page, '額外角色', 'E2E 主管');
  await submit(page);
  await confirmSave(page);
  await waitForToast(page, '修改成功');
  await target.goto('/Profile');
  await target.waitForURL(/\/Auths\/Login/, { timeout: 30000 }).catch(() => {});
  const targetSignedOut = target.url().includes('/Auths/Login');
  await targetCtx.close();
  await closeToasts(page);
  recorder.add('A2', '改別人的角色後，對方下一次開頁就被登出', targetSignedOut, `對方重新開 /Profile 後停在 ${new URL(target.url()).pathname}`);

  // A3：停用帳號：對方被登出，重新登入也被拒。
  const target2Ctx = await newContext(browser);
  const target2 = await target2Ctx.newPage();
  await login(target2, { account: 'e2e-target2', password: userPassword });
  await page.locator('.ant-table-row', { hasText: 'e2e-target2' }).first().locator('button.status-pill-clickable').click();
  await confirm(page, '停用');
  await waitForToast(page, '已停用「e2e-target2」');
  await target2.goto('/Profile');
  await target2.waitForURL(/\/Auths\/Login/, { timeout: 30000 }).catch(() => {});
  const disabledOut = target2.url().includes('/Auths/Login');
  await target2Ctx.close();
  await closeToasts(page);
  recorder.add('A3', '停用帳號後，對方被登出', disabledOut, `對方重新開 /Profile 後停在 ${new URL(target2.url()).pathname}`);

  // A4：解鎖被鎖定的帳號。
  await clickRowAction(page, 'e2e-locked', '解鎖');
  await confirm(page, '解鎖');
  const unlocked = await waitForToast(page, '已解除「e2e-locked」的鎖定').then(() => true, () => false);
  const lockAfter = query("SELECT LockoutEndUtc FROM MyUser WHERE Account = 'e2e-locked'")[0].LockoutEndUtc;
  await closeToasts(page);
  recorder.add('A4', '解除鎖定', unlocked && lockAfter === null, `通知「已解除「e2e-locked」的鎖定。」=${unlocked}；資料庫鎖定時間 ${lockAfter}`);

  // A5：複製使用者：產生密碼、建立、交付窗列出登入資訊。
  const copyAccount = `e2e-copy${stamp}`;
  await clickRowAction(page, 'e2e-wang', '複製');
  await modal(page).waitFor();
  await field(page, '帳號').fill(copyAccount);
  await field(page, '名稱').fill('複製的同仁');
  await modal(page).locator('button', { hasText: '產生' }).click();
  await page.waitForTimeout(500);
  await submit(page, '建立');
  const delivery = page.locator('.user-copy-result-modal');
  await delivery.waitFor({ state: 'visible', timeout: 15000 });
  const deliveryText = await delivery.innerText();
  await screenshot(page, 'm05-a5-複製交付窗');
  await delivery.locator('.ant-modal-footer button', { hasText: '關閉' }).click();
  const copyAudit = query("SELECT Detail FROM AuditLog WHERE Action = 'User.Create' AND Detail LIKE ?", `%${copyAccount}%`)[0]?.Detail ?? '';
  await closeToasts(page);
  recorder.add('A5', '複製使用者（產生密碼、交付窗）', deliveryText.includes(copyAccount) && deliveryText.includes('密碼：') && copyAudit.includes('copiedFrom=e2e-wang'),
    `交付窗列出帳號與密碼；稽核含 copiedFrom=e2e-wang=${copyAudit.includes('copiedFrom=e2e-wang')}`);

  // A6：匯出使用者 Excel。
  const [userExport] = await Promise.all([page.waitForEvent('download', { timeout: 30000 }), clickToolbar(page, '匯出 Excel')]);
  const userFile = join(outDir, userExport.suggestedFilename());
  await userExport.saveAs(userFile);
  recorder.add('A6', '匯出使用者 Excel', readFileSync(userFile).subarray(0, 2).toString('latin1') === 'PK' && query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'User.Export'")[0].n >= 1,
    `下載 ${userExport.suggestedFilename()}，寫了匯出稽核`);

  // A7：角色權限矩陣：勾「團隊清單（全部）」存檔，權限鍵寫進資料庫。
  await openList(page, '/roleviews');
  await clickRowAction(page, 'E2E 主管', '修改');
  await modal(page).waitFor();
  const teamCard = modal(page).locator('.role-permission-card', { hasText: '團隊清單（全部）' });
  await teamCard.locator('.ant-checkbox-input').first().check();
  await submit(page);
  await confirm(page, '儲存');
  await waitForToast(page, '修改成功');
  const keys = query("SELECT p.Key FROM RolePermissionMap m JOIN Permission p ON p.Id = m.PermissionId JOIN RoleView r ON r.Id = m.RoleViewId WHERE r.Name = 'E2E 主管'").map((x) => x.Key);
  await closeToasts(page);
  recorder.add('A7', '角色權限矩陣存檔', keys.includes('團隊清單'), `「E2E 主管」的權限鍵：${keys.join('、')}`);

  // A8：稽核紀錄：查詢、明細、完整性驗證、匯出 CSV。
  await page.goto('/audit-logs');
  await page.waitForSelector('.audit-log-table', { timeout: 60000 });
  await page.fill('.audit-log-search-input', seedTag);
  await clickToolbar(page, '查詢');
  await page.waitForFunction(() => document.querySelectorAll('.audit-log-table .ant-table-row').length === 3, null, { timeout: 15000 });
  await page.locator('.audit-log-table .ant-table-row', { hasText: 'Role.Create' }).first().locator('button', { hasText: '查看' }).click();
  const detail = page.locator('.audit-log-modal:visible');
  await detail.waitFor({ timeout: 15000 });
  const detailText = await detail.innerText();
  // 關閉鈕而不是 Esc：Esc 要焦點在對話窗裡才有效。
  await detail.locator('.ant-modal-close').click();
  await detail.waitFor({ state: 'hidden', timeout: 15000 });
  await clickToolbar(page, '驗證完整性');
  const verify = page.locator('.audit-log-modal:visible', { hasText: '稽核紀錄完整性驗證' });
  await verify.locator('.ant-tag').first().waitFor({ timeout: 120000 });
  const verifyText = await verify.innerText();
  await screenshot(page, 'm05-a8-完整性驗證');
  await verify.locator('.ant-modal-close').click();
  await verify.waitFor({ state: 'hidden', timeout: 15000 });
  const [auditExport] = await Promise.all([page.waitForEvent('download', { timeout: 30000 }), clickToolbar(page, '匯出目前查詢結果（CSV）')]);
  const csvFile = join(outDir, auditExport.suggestedFilename());
  await auditExport.saveAs(csvFile);
  const csv = readFileSync(csvFile);
  const csvLines = csv.subarray(3).toString('utf8').split('\r\n').filter(Boolean).length;
  const auditAudits = ['Audit.Search', 'Audit.View', 'Audit.Verify', 'Audit.Export'].map((a) => `${a}=${query('SELECT COUNT(*) AS n FROM AuditLog WHERE Action = ?', a)[0].n}`);
  recorder.add('A8', '稽核紀錄：查詢、明細、完整性驗證、匯出',
    detailText.includes('2026-11-01 01:02:00') && detailText.includes('升級前的紀錄') && (verifyText.includes('發現問題') || verifyText.includes('全部正確')) && csvLines === 4,
    `明細有 UTC 時間與簽章；驗證結果「${verifyText.includes('發現問題') ? '發現問題（種子資料未簽章，預期）' : '全部正確'}」；CSV ${csvLines - 1} 筆；稽核 ${auditAudits.join('、')}`);

  // A9：寄信紀錄依狀態篩選。
  await page.goto('/email-logs');
  await page.waitForSelector('.email-log-table', { timeout: 60000 });
  await page.fill('.email-log-search-input', seedTag);
  await page.locator('.email-log-toolbar .ant-select').nth(0).click();
  await page.locator('.ant-select-dropdown:visible .ant-select-item-option', { hasText: '失敗' }).click();
  await clickToolbar(page, '查詢');
  await page.waitForFunction(() => document.querySelectorAll('.email-log-table .ant-table-row').length === 1, null, { timeout: 15000 });
  const mailRows = await rowTexts(page);
  recorder.add('A9', '寄信紀錄依狀態篩選', mailRows.length === 1 && mailRows[0].includes('重設密碼'), `只剩 1 筆：${mailRows[0]?.replace(/\s+/g, ' ').slice(0, 60)}`);

  // A10：Server 頁面與 WASM 頁面互相切換。
  await page.goto('/logs');
  await page.waitForTimeout(3000);
  const logsIsWasm = await isWasmPage(page);
  await page.goto('/roleviews');
  await page.locator('.role-view-table .ant-table-row').first().waitFor({ timeout: 60000 });
  recorder.add('A10', 'Server 與 WASM 頁面互相切換', !logsIsWasm && (await isWasmPage(page)), '/logs（Server）→ /roleviews（WASM，整頁載入）');

  recorder.add('A11', '管理員操作過程沒有未處理的錯誤', errors.length === 0, errors.length === 0 ? '無' : errors.join('；'));
  await ctx.close();

  // A12：改自己（另一位管理員加一個額外角色）：工作階段版本換掉，整頁換發登入後仍在使用者頁。
  const selfCtx = await newContext(browser);
  const self = await selfCtx.newPage();
  await login(self, { account: 'e2e-admin2', password: userPassword });
  await openList(self, '/myusers');
  await clickRowAction(self, 'e2e-admin2', '修改');
  await modal(self).waitFor();
  await pickOption(self, '額外角色', 'E2E 唯讀');
  await submit(self);
  const refreshRequest = self.waitForRequest((request) => request.url().includes('/Auths/RefreshSession'), { timeout: 30000 });
  await confirmSave(self);
  await refreshRequest;
  await self.waitForURL(/\/myusers$/, { timeout: 30000 });
  await self.locator('.my-user-table .ant-table-row').first().waitFor({ timeout: 60000 });
  const stillIn = new URL(self.url()).pathname === '/myusers';
  const refreshed = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action LIKE 'Auth.%Refresh%' OR Action = 'Auth.SessionRefresh'")[0].n;
  await screenshot(self, 'm05-a12-改自己後仍登入');
  await selfCtx.close();
  recorder.add('A12', '改自己的角色後這台裝置仍維持登入', stillIn, `換發後停在 /myusers，清單正常顯示（換發相關稽核 ${refreshed} 筆）`);

  // A13：非管理員直接呼叫管理 API 得 403；開頁顯示沒有權限。
  const readerCtx = await newContext(browser);
  const reader = await readerCtx.newPage();
  await login(reader, { account: readerAccount, password: readerPassword });
  await reader.goto('/myusers');
  await reader.locator('.alert-danger').waitFor({ timeout: 60000 });
  const statuses = [];
  for (const [path, body] of [['/api/v1/users/search', {}], ['/api/v1/roles/search', {}], ['/api/v1/audit-logs/search', {}], ['/api/v1/email-logs/search', {}]]) {
    statuses.push(await apiAsPageUser(reader, path, { method: 'POST', body }));
  }
  statuses.push(await apiAsPageUser(reader, `/api/v1/users/${targetId}/force-logout`, { method: 'POST', body: {} }));
  await screenshot(reader, 'm05-a13-沒有權限');
  await readerCtx.close();
  recorder.add('A13', '非管理員直打管理 API 得 403', statuses.every((x) => x === 403), `使用者、角色、稽核、寄信搜尋與強制登出：${statuses.join('、')}`);

  // 全部路由煙霧測試（/myusers、/roleviews、/audit-logs、/email-logs 已是 WASM）。
  await runSmoke(browser, smoke);
} catch (error) {
  for (const [i, open] of browser.contexts().flatMap((c) => c.pages()).entries()) {
    await screenshot(open, `m05-failed-${i}`).catch(() => {});
  }
  throw error;
} finally {
  await browser.close();
}

const ok = recorder.save() & smoke.save();
process.exit(ok ? 0 : 1);
