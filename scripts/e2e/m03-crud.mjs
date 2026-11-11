// WASM 遷移 M3（0.10.2）的實機情境：分類、團隊頁在 WebAssembly 執行，資料走 Web API（與畫面共用同一個服務）。
// 完整新增修改刪除、重名、兩個分頁同時修改、團隊範圍、唯讀角色（看不到按鈕、API 回 403）、匯出 Excel、
// 切換啟用、還原與永久刪除、批次刪除（先下屬後上層）、團隊改名同步分類、資料保留頁的「前往」、異動歷程、
// 沒有權限開頁的稽核、Server／WASM 頁面互相切換，最後跑全部路由的煙霧測試。
// 用法見同資料夾的 README.md。結果寫到 E2E_OUT_DIR/m03-results.json 與 smoke-results.json。
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  apiAsPageUser, createRecorder, createScopedUser, isWasmPage, launchBrowser, login, newContext, outDir, query, screenshot,
} from './common.mjs';
import {
  addRecord, clickRowAction, clickToolbar, closeForm, closeToasts, confirm, deleteRecord, editRecord, formField, openList,
  purgeRecord, restoreRecord, rowTexts, submitForm, waitForToast, watchErrors,
} from './crud-ui.mjs';
import { readerAccount, readerPassword, seedFixedData } from './m03-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m03-results.json');
const smoke = createRecorder('smoke-results.json');
const conflictMessage = '這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。';

seedFixedData();
const allCategoryKeys = ['分類清單:view', '分類清單:create', '分類清單:edit', '分類清單:delete'];
createScopedUser('e2e-rd', 'Rd!E2e2026x', 'E2E 研發分類維護', allCategoryKeys, ['E2E 研發部']);
createScopedUser('e2e-noperm', 'NoPerm!E2e2026', 'E2E 只看專案', ['專案項目:view']);

const browser = await launchBrowser();
try {
  const startedAt = new Date().toISOString().replace('T', ' ').replace('Z', '');
  const ctx = await newContext(browser);
  const page = await ctx.newPage();
  const errors = watchErrors(page);
  await login(page);

  // C1：分類完整流程（WASM）：新增（未指定團隊的提醒）、修改、切換停用、刪除、顯示已刪除、還原、永久刪除。
  await openList(page, '/categories');
  const wasm = await isWasmPage(page);
  const name = `E2E 新分類${Date.now() % 10000}`;
  const steps = [];
  steps.push(await addRecord(page, { 名稱: name, 描述: '實機新增' }, { extraConfirm: '仍要儲存' }));
  steps.push(await editRecord(page, name, { 描述: '實機修改' }, { extraConfirm: '仍要儲存' }));
  await page.locator('.ant-table-row', { hasText: name }).first().locator('button.status-pill-clickable').click();
  await confirm(page, '停用');
  steps.push(await waitForToast(page, `已停用「${name}」`));
  steps.push(await deleteRecord(page, name));
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: name }).first().waitFor();
  steps.push(await restoreRecord(page, name));
  // 還原後畫面仍在「已刪除」清單：回一般清單再刪一次，然後到已刪除清單永久刪除。
  await clickToolbar(page, '返回清單');
  await page.locator('.ant-table-row', { hasText: name }).first().waitFor();
  await deleteRecord(page, name);
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: name }).first().waitFor();
  steps.push(await purgeRecord(page, name));
  await clickToolbar(page, '返回清單');
  const dbAfter = query('SELECT COUNT(*) AS n FROM Category WHERE Name = ?', name)[0].n;
  const audits = query(`SELECT Action FROM AuditLog WHERE TargetType = 'Category' AND Detail LIKE ? ORDER BY Id`, `%${name}%`).map((x) => x.Action);
  await closeToasts(page);
  recorder.add('C1', '分類完整流程（新增、修改、停用、刪除、還原、永久刪除）', wasm && dbAfter === 0 && audits.includes('Category.Purge'),
    `WASM=${wasm}；通知：${steps.map((x) => x.replace(/\s+/g, ' ').replace('系統訊息', '').trim()).join('／')}；資料庫剩 ${dbAfter} 筆；稽核 ${audits.join('→')}`);

  // C2：重名（不分大小寫、前後空白）被擋。
  await addRecord(page, { 名稱: ' e2e 硬體 ' }, { expect: '分類名稱已存在' }).catch(() => null);
  const duplicateToast = await page.locator('.ant-notification-notice', { hasText: '分類名稱已存在' }).count();
  await closeForm(page);
  await closeToasts(page);
  recorder.add('C2', '分類重名被擋', duplicateToast > 0, duplicateToast > 0 ? '顯示「分類名稱已存在，無法新增。」' : '沒有擋下');

  // C3：兩個分頁同時修改同一筆：後存的得到版本衝突訊息，先存的內容保留。
  const pageB = await ctx.newPage();
  await openList(pageB, '/categories');
  await clickRowAction(page, 'E2E 軟體', '修改');
  await page.locator('.form-modal').waitFor();
  await clickRowAction(pageB, 'E2E 軟體', '修改');
  await pageB.locator('.form-modal').waitFor();
  const stamp = Date.now() % 100000;
  await formField(page, '描述').fill(`分頁 A 先存 ${stamp}`);
  await submitForm(page);
  await confirm(page, '儲存');
  await confirmIfTeam(page);
  await waitForToast(page, '修改成功');
  await formField(pageB, '描述').fill(`分頁 B 後存 ${stamp}`);
  await submitForm(pageB);
  await confirm(pageB, '儲存');
  await confirmIfTeam(pageB);
  const conflictShown = await waitForToast(pageB, '已被其他人修改').then(() => true, () => false);
  await screenshot(pageB, 'm03-c3-版本衝突');
  const savedDescription = query("SELECT Description FROM Category WHERE Name = 'E2E 軟體'")[0].Description;
  await pageB.close();
  await closeToasts(page);
  recorder.add('C3', '兩個分頁同時修改：後存的得到衝突訊息', conflictShown && savedDescription === `分頁 A 先存 ${stamp}`,
    `分頁 B 顯示「${conflictMessage.slice(0, 18)}…」=${conflictShown}；資料庫描述「${savedDescription}」`);

  // C4：匯出 Excel（下載檔是 xlsx）。
  const [download] = await Promise.all([page.waitForEvent('download', { timeout: 30000 }), clickToolbar(page, '匯出 Excel')]);
  const file = join(outDir, download.suggestedFilename());
  await download.saveAs(file);
  const head = readFileSync(file).subarray(0, 2).toString('latin1');
  recorder.add('C4', '匯出 Excel', file.endsWith('.xlsx') && head === 'PK' && query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Category.Export'")[0].n >= 1,
    `下載 ${download.suggestedFilename()}（${head === 'PK' ? 'xlsx 格式' : '格式不對'}），寫了匯出稽核`);

  // C5：異動歷程（管理員）：E2E 軟體剛被修改過。
  await clickRowAction(page, 'E2E 軟體', '異動歷程');
  await page.locator('.audit-history-modal .audit-history-item').first().waitFor({ timeout: 15000 });
  const historyItems = await page.locator('.audit-history-modal .audit-history-item').count();
  await page.locator('.audit-history-modal .audit-history-item').first().click();
  await page.waitForTimeout(800);
  const historyDetail = await page.locator('.audit-history-modal .audit-history-detail').innerText();
  await screenshot(page, 'm03-c5-異動歷程');
  await page.keyboard.press('Escape');
  recorder.add('C5', '異動歷程（管理員）', historyItems >= 1 && historyDetail.includes('Category.Update'),
    `列出 ${historyItems} 筆，點開第一筆顯示 Category.Update 的摘要（詳細內容視系統參數「記錄修改前後差異」而定）`);

  // C6：團隊：批次刪除（同時勾上層與下屬，先刪下屬）、團隊改名同步分類的適用團隊。
  await openList(page, '/teams');
  await addRecord(page, { 名稱: 'E2E 批次上層', 代號: 'BATCHP' });
  await addRecord(page, { 名稱: 'E2E 批次下層', 代號: 'BATCHC' });
  await clickRowAction(page, 'E2E 批次下層', '修改');
  await page.locator('.form-modal').waitFor();
  await page.locator('.form-modal .ant-form-item', { has: page.locator('label', { hasText: '上層團隊' }) }).locator('.ant-select').click();
  await page.locator('.ant-select-item-option', { hasText: 'E2E 批次上層' }).click();
  await submitForm(page);
  await confirm(page, '儲存');
  await waitForToast(page, '修改成功');
  await page.locator('.ant-table-row', { hasText: 'E2E 批次上層' }).first().locator('.ant-table-row-expand-icon').click();
  await page.locator('.ant-table-row', { hasText: 'E2E 批次下層' }).first().waitFor();
  for (const team of ['E2E 批次上層', 'E2E 批次下層']) {
    await page.locator('.ant-table-row', { hasText: team }).first().locator('.ant-checkbox-input').check();
  }
  await page.locator('button', { hasText: '刪除選取（2）' }).click();
  await confirm(page, '刪除');
  const batchToast = await waitForToast(page, '已刪除 2 筆').then(() => true, () => false);
  const remainingBatch = query("SELECT COUNT(*) AS n FROM Team WHERE Name LIKE 'E2E 批次%' AND IsDeleted = 0")[0].n;
  await closeToasts(page);
  recorder.add('C6', '團隊批次刪除（先下屬後上層）', batchToast && remainingBatch === 0, `通知「已刪除 2 筆」=${batchToast}；未刪除的剩 ${remainingBatch} 筆`);

  // 依目前的名稱在「業務部」「業務處」之間切換，同一個資料庫可以重跑。
  const salesName = query("SELECT Name FROM Team WHERE Code = 'SALES'")[0].Name;
  const renamed = salesName === 'E2E 業務部' ? 'E2E 業務處' : 'E2E 業務部';
  await editRecord(page, salesName, { 名稱: renamed });
  await openList(page, '/categories');
  const marketing = (await rowTexts(page)).find((x) => x.includes('E2E 行銷')) ?? '';
  recorder.add('C7', '團隊改名同步分類的適用團隊', marketing.includes(renamed), `「${salesName}」改名為「${renamed}」後，「E2E 行銷」的適用團隊：${marketing.includes(renamed) ? renamed : marketing}`);

  // C8：資料保留頁的「前往」帶 ?deleted=1：一進來就是已刪除的清單。
  await page.goto('/data-retention');
  await page.locator('.ant-table-row', { hasText: '已刪除的分類' }).first().locator('a.crud-action-button').click();
  await page.waitForURL(/\/categories\?deleted=1/, { timeout: 30000 });
  await page.locator('button.toolbar-icon-button', { hasText: '返回清單' }).waitFor({ timeout: 30000 });
  recorder.add('C8', '資料保留頁「前往」直接顯示已刪除的分類', await isWasmPage(page), `${new URL(page.url()).pathname}${new URL(page.url()).search}，工具列顯示「返回清單」`);

  // C9：Server 頁面與 WASM 頁面互相切換（側邊選單）。
  await page.goto('/projects');
  await page.locator('.ant-table-wrapper').first().waitFor({ timeout: 30000 });
  const serverIsWasm = await isWasmPage(page);
  // 在 Server 頁面「資料定義」群組是收合的：先展開再點。
  await page.locator('.ant-menu-submenu-title:visible', { hasText: '資料定義' }).first().click();
  await page.locator('a:visible', { hasText: '團隊清單' }).first().click();
  await page.waitForURL(/\/teams$/, { timeout: 30000 });
  await page.locator('.team-view-table').waitFor({ timeout: 60000 });
  const teamsIsWasm = await isWasmPage(page);
  await page.locator('a:visible', { hasText: '分類清單' }).first().click();
  await page.waitForURL(/\/categories$/, { timeout: 30000 });
  await page.locator('.category-view-table .ant-table-row').first().waitFor({ timeout: 30000 });
  recorder.add('C9', 'Server 與 WASM 頁面互相切換', !serverIsWasm && teamsIsWasm, `/projects（Server）→ /teams（WASM，整頁載入）→ /categories（WASM 內換頁）`);

  recorder.add('C10', '管理員操作過程沒有未處理的錯誤', errors.length === 0, errors.length === 0 ? '無' : errors.join('；'));
  await ctx.close();

  // C11：團隊範圍：研發部的人只看到研發部與公開的分類；畫面與 API 一致。
  const rdCtx = await newContext(browser);
  const rd = await rdCtx.newPage();
  await login(rd, { account: 'e2e-rd', password: 'Rd!E2e2026x' });
  await openList(rd, '/categories');
  const rdRows = await rowTexts(rd);
  const marketingId = query("SELECT Id FROM Category WHERE Name = 'E2E 行銷'")[0].Id;
  const outsideStatus = await apiAsPageUser(rd, `/api/v1/Category/${marketingId}`);
  await screenshot(rd, 'm03-c11-團隊範圍');
  await rdCtx.close();
  recorder.add('C11', '團隊範圍（研發部的人看不到業務部的分類）',
    rdRows.some((x) => x.includes('E2E 硬體')) && rdRows.some((x) => x.includes('E2E 軟體')) && !rdRows.some((x) => x.includes('E2E 行銷')) && outsideStatus === 404,
    `清單：${rdRows.map((x) => x.match(/E2E \S+/)?.[0]).filter(Boolean).join('、')}；API 讀業務部的分類回 ${outsideStatus}`);

  // C12：唯讀角色：看不到新增、修改、刪除與勾選欄；直接呼叫 API 寫入回 403。
  const readerCtx = await newContext(browser);
  const reader = await readerCtx.newPage();
  await login(reader, { account: readerAccount, password: readerPassword });
  await openList(reader, '/categories');
  const writeButtons = await reader.locator('button.toolbar-icon-button', { hasText: '新增' }).count()
    + await reader.locator('button.crud-action-button', { hasText: '修改' }).count()
    + await reader.locator('button.crud-action-button', { hasText: '刪除' }).count()
    + await reader.locator('.ant-table-selection-column').count();
  const createStatus = await apiAsPageUser(reader, '/api/v1/Category', { method: 'POST', body: { id: 0, name: '越權新增' } });
  const deleteStatus = await apiAsPageUser(reader, `/api/v1/Category/${marketingId}`, { method: 'DELETE' });
  await readerCtx.close();
  recorder.add('C12', '唯讀角色看不到寫入按鈕、API 回 403', writeButtons === 0 && createStatus === 403 && deleteStatus === 403,
    `寫入按鈕與勾選欄 ${writeButtons} 個；POST 回 ${createStatus}、DELETE 回 ${deleteStatus}`);

  // C13：沒有權限的人打開分類頁：顯示沒有權限，伺服器記一筆權限拒絕（via=WASM）。
  const noCtx = await newContext(browser);
  const no = await noCtx.newPage();
  await login(no, { account: 'e2e-noperm', password: 'NoPerm!E2e2026' });
  await no.goto('/categories');
  await no.locator('.alert-danger').waitFor({ timeout: 60000 });
  const deniedText = await no.locator('.alert-danger').innerText();
  await no.waitForTimeout(1500);
  const deniedAudit = query(`SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Permission.Denied' AND TargetId = '/categories' AND Detail = 'via=WASM' AND OccurredAt >= ?`, startedAt)[0].n;
  await screenshot(no, 'm03-c13-沒有權限');
  await noCtx.close();
  recorder.add('C13', '沒有權限開頁：顯示沒有權限並記稽核', deniedText.includes('沒有權限') && deniedAudit >= 1, `畫面「${deniedText.trim()}」；權限拒絕稽核 ${deniedAudit} 筆`);

  // 全部路由煙霧測試（/categories、/teams 已是 WASM）。
  await runSmoke(browser, smoke);
} catch (error) {
  // 情境中途失敗：每個還開著的分頁各拍一張，方便查當時畫面。
  for (const [i, open] of browser.contexts().flatMap((c) => c.pages()).entries()) {
    await screenshot(open, `m03-failed-${i}`).catch(() => {});
  }
  throw error;
} finally {
  await browser.close();
}

const ok = recorder.save() & smoke.save();
process.exit(ok ? 0 : 1);

/** 分類儲存時可能跳出「確認團隊設定」（未指定團隊）；有就按「仍要儲存」。 */
async function confirmIfTeam(page) {
  const dialog = page.locator('.ant-modal-confirm', { hasText: '確認團隊設定' });
  try {
    await dialog.waitFor({ state: 'visible', timeout: 1500 });
    await dialog.locator('.ant-modal-confirm-btns button', { hasText: '仍要儲存' }).click();
  } catch {
    // 沒有跳出提醒（已指定團隊）。
  }
}
