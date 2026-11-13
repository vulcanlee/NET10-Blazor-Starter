// 畫面選項資料一致性（0.10.15 起）：兩個瀏覽器、兩個帳號。甲停在畫面上，乙（另一個登入的瀏覽器，以 API 操作）新增、停用了分類或團隊。
//   F1 專案：乙新增分類，甲不重新整理直接開「新增專案」→ 分類下拉看得到新分類。
//   F2 專案：甲開窗選了分類 X；乙停用 X；甲按儲存 → 擋下並顯示「所選的分類「X」已不存在或已停用」，分類下拉不再有 X（選項已更新）。
//   F3 專案：乙新增分類，甲按工具列「重新整理」→「分類過濾」看得到新分類。
//   F4 分類：乙新增團隊，甲開「新增分類」→「適用團隊」看得到。
//   F5 角色：乙新增團隊，甲開「新增角色」→「預設團隊」看得到。
//   F6 公告：乙新增團隊，甲開「新增公告」、對象選「團隊」→ 看得到。
//   F7 使用者：乙新增團隊，甲開「新增使用者」→「團隊」看得到。
//   F8 分類：開「新增分類」輸入已存在的名稱、離開欄位 → 不必按儲存就在欄位下方看到「分類名稱已存在」；改名後訊息消失（.NET 11 非同步表單驗證）。
//   F8b 分類：輸入已存在的名稱後立刻按「儲存」（不等檢查）→ 儲存等檢查完成、以驗證錯誤擋下，不跳出確認窗。
//   F9 使用者：開「新增使用者」輸入已存在的帳號 → 欄位下方看到「帳號已存在」。
//   F10 權限（既有機制，記錄實際行為）：丙停在分類頁時，管理員拿掉丙角色的「新增」權限 → 丙不重新整理按「新增」存檔，伺服器擋下（403）；重新整理後「新增」鈕消失。
// 結果寫到 E2E_OUT_DIR/ux-data-freshness-results.json，截圖在 screenshots/fresh-*.png。
import { apiJsonAsPageUser, createRecorder, createScopedUser, createUser, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { clickToolbar, closeForm, closeToasts, confirm, confirmIfShown, openList, waitForToast } from './crud-ui.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const otherPassword = 'Fresh!E2e-' + Date.now().toString(36) + 'Aa9';
createUser('e2e-fresh-other', otherPassword);
execute("UPDATE MyUser SET IsAdmin = 1 WHERE Account = 'e2e-fresh-other'");
const limitedPassword = 'Lim!E2e-' + Date.now().toString(36) + 'Aa9';
const limitedKeys = ['分類清單:view', '分類清單:create'];
createScopedUser('e2e-fresh-limited', limitedPassword, 'E2E 分類維護F', limitedKeys);
// 角色編輯窗的權限矩陣由 TabViewJson 回填（權限判定看 RolePermissionMap）：與畫面建立的角色一致，F10 才能經 API 只拿掉「新增」。
execute('UPDATE RoleView SET TabViewJson = ? WHERE Name = ?', JSON.stringify(limitedKeys), 'E2E 分類維護F');

const recorder = createRecorder('ux-data-freshness-results.json');
const browser = await launchBrowser();
const stamp = Date.now().toString(36).slice(-5);

/** 表單對話窗裡某個標籤的下拉：打開、讀選項文字、點標題關掉。 */
async function dropdownOptions(page, label, { scope = '.form-modal' } = {}) {
  const item = page.locator(`${scope} .ant-form-item`, { has: page.locator('label', { hasText: label }) }).first();
  await item.locator('.ant-select-selector').click();
  const dropdown = page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)').last();
  await dropdown.waitFor({ state: 'visible', timeout: 10000 });
  await page.waitForTimeout(300);
  const options = (await dropdown.locator('.ant-select-item-option-content').allInnerTexts()).map((x) => x.trim());
  await page.locator(`${scope} .ant-modal-title`).first().click();
  await page.waitForTimeout(200);
  return options;
}

/** 工具列（不在對話窗裡）的下拉：依 placeholder 找。 */
async function toolbarDropdownOptions(page, placeholder) {
  const selector = page.locator('.ant-select', { has: page.locator('.ant-select-selection-placeholder', { hasText: placeholder }) }).first().locator('.ant-select-selector');
  await selector.click();
  const dropdown = page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)').last();
  await dropdown.waitFor({ state: 'visible', timeout: 10000 });
  await page.waitForTimeout(300);
  const options = (await dropdown.locator('.ant-select-item-option-content').allInnerTexts()).map((x) => x.trim());
  // 再點一次收起來（Esc 要焦點在下拉上才有效）；等收好，下一次才不會拿到這個舊的。
  await selector.click();
  await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)').waitFor({ state: 'hidden', timeout: 10000 }).catch(() => {});
  return options;
}

async function selectOption(page, label, text) {
  const item = page.locator('.form-modal .ant-form-item', { has: page.locator('label', { hasText: label }) }).first();
  await item.locator('.ant-select-selector').click();
  const dropdown = page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden)').last();
  await dropdown.waitFor({ state: 'visible', timeout: 10000 });
  await dropdown.locator('.ant-select-item-option', { hasText: text }).first().click();
  await page.locator('.form-modal .ant-modal-title').first().click();
}

try {
  // 甲：support；乙：另一個瀏覽器的管理員。
  const contextA = await newContext(browser);
  const a = await contextA.newPage();
  await login(a);
  const contextB = await newContext(browser);
  const b = await contextB.newPage();
  await login(b, { account: 'e2e-fresh-other', password: otherPassword });
  const asB = (path, options) => apiJsonAsPageUser(b, path, options);

  // F1：乙新增分類，甲直接開窗。
  await openList(a, '/projects');
  const cat1 = `F分類甲${stamp}`;
  const created = await asB('/api/v1/Category', { method: 'POST', body: { id: 0, name: cat1, isEnabled: true } });
  await clickToolbar(a, '新增');
  await a.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  const f1 = await dropdownOptions(a, '分類');
  await screenshot(a, 'fresh-F1-新分類出現在下拉');
  recorder.add('F1', '專案：別人剛新增的分類，不重新整理直接開窗就看得到', created.status === 200 && f1.includes(cat1),
    `乙新增分類 HTTP ${created.status}；甲的分類下拉 ${f1.length} 項，含「${cat1}」=${f1.includes(cat1)}`);

  // F2：甲選了分類，乙停用它，甲存檔。
  await a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '標題' }) }).locator('input').fill(`F專案${stamp}`);
  await a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '負責人' }) }).locator('input').fill('王小明');
  await selectOption(a, '分類', cat1);
  const catId = created.json?.data?.id;
  const disabled = await asB(`/api/v1/Category/${catId}/enabled`, { method: 'PUT', body: { enabled: false } });
  await a.locator('.form-modal .ant-modal-footer button', { hasText: '儲存' }).click();
  await confirm(a, '儲存');
  await confirmIfShown(a, '仍要儲存', 3000);
  const f2Toast = await waitForToast(a, '已不存在或已停用').catch(() => '');
  await a.waitForTimeout(800);
  await screenshot(a, 'fresh-F2-存檔被擋');
  const f2Options = await dropdownOptions(a, '分類');
  const stillOpen = await a.locator('.form-modal').isVisible();
  recorder.add('F2', '專案：選的分類被別人停用，存檔擋下並顯示原因，下拉換成最新的選項',
    disabled.status === 200 && f2Toast.includes(`「${cat1}」`) && stillOpen,
    `乙停用 HTTP ${disabled.status}；通知「${f2Toast.replace(/\s+/g, ' ').slice(0, 80)}」；對話窗仍開著=${stillOpen}；之後下拉的可選項含「${cat1}」=${f2Options.includes(cat1)}（已選的會以「受限」顯示）`);
  await closeForm(a);

  // F3：工具列的分類過濾在「重新整理」後更新。
  const cat2 = `F分類乙${stamp}`;
  await asB('/api/v1/Category', { method: 'POST', body: { id: 0, name: cat2, isEnabled: true } });
  const before = await toolbarDropdownOptions(a, '分類過濾');
  await clickToolbar(a, '重新整理');
  await waitForToast(a, '已更新最新資料').catch(() => {});
  const after = await toolbarDropdownOptions(a, '分類過濾');
  recorder.add('F3', '專案：按「重新整理」後工具列的分類過濾看得到別人剛新增的分類', !before.includes(cat2) && after.includes(cat2),
    `重新整理前含「${cat2}」=${before.includes(cat2)}、之後=${after.includes(cat2)}`);

  // F4～F7：乙新增團隊，甲在各頁開窗。
  const team = `F隊${stamp}`;
  const pages = [
    ['F4', '/categories', '適用團隊', '分類'],
    ['F5', '/roleviews', '預設團隊', '角色'],
    ['F7', '/myusers', '團隊', '使用者'],
  ];
  for (const [id, route, label, name] of pages) {
    await openList(a, route);
    // 開頁之後乙才新增，才驗得到「開窗時重讀」。
    const late = `${team}-${id}`;
    const teamCreated = await asB('/api/v1/Team', { method: 'POST', body: { id: 0, name: late, isEnabled: true } });
    await clickToolbar(a, '新增');
    await a.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
    await a.waitForTimeout(500);
    const options = await dropdownOptions(a, label);
    await screenshot(a, `fresh-${id}-${name}`);
    recorder.add(id, `${name}：別人剛新增的團隊，不重新整理直接開窗就看得到`, teamCreated.status === 200 && options.includes(late),
      `「${label}」${options.length} 項，含「${late}」=${options.includes(late)}`);
    await closeForm(a);
  }

  // F6：公告（不是 .form-modal，選項在對象選「團隊」之後出現）。
  await a.goto('/announcements');
  await a.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
  await a.waitForTimeout(1000);
  const lateAnn = `${team}-F6`;
  await asB('/api/v1/Team', { method: 'POST', body: { id: 0, name: lateAnn, isEnabled: true } });
  await clickToolbar(a, '新增');
  const annModal = a.locator('.ant-modal-content:visible').first();
  await annModal.waitFor({ timeout: 15000 });
  await annModal.locator('.ant-form-item', { has: a.locator('label', { hasText: '對象' }) }).locator('.ant-select-selector').click();
  await a.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option', { hasText: '團隊' }).first().click();
  await a.waitForTimeout(300);
  const annOptions = await dropdownOptions(a, '團隊', { scope: '.ant-modal-content:visible' });
  await screenshot(a, 'fresh-F6-公告');
  recorder.add('F6', '公告：別人剛新增的團隊，不重新整理直接開窗就看得到', annOptions.includes(lateAnn), `「團隊」含「${lateAnn}」=${annOptions.includes(lateAnn)}`);
  await a.keyboard.press('Escape');
  await a.locator('.ant-modal-confirm-btns button', { hasText: /放棄|離開|確定/ }).first().click({ timeout: 2000 }).catch(() => {});

  // F8：分類名稱的非同步檢查。
  await openList(a, '/categories');
  await clickToolbar(a, '新增');
  await a.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  const nameInput = a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '名稱' }) }).locator('input').first();
  await nameInput.fill(cat2);
  await a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '描述' }) }).locator('textarea').click();
  const conflict = await a.locator('.form-modal .validation-message', { hasText: '已存在' }).first().waitFor({ timeout: 10000 }).then(() => true).catch(() => false);
  await screenshot(a, 'fresh-F8-名稱已存在');
  await nameInput.fill(`F沒重複${stamp}`);
  await a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '描述' }) }).locator('textarea').click();
  const cleared = await a.locator('.form-modal .validation-message', { hasText: '已存在' }).waitFor({ state: 'detached', timeout: 10000 }).then(() => true).catch(() => false);
  recorder.add('F8', '分類：輸入已存在的名稱，離開欄位就在欄位下方看到「已存在」，改名後消失', conflict && cleared,
    `出現=${conflict}、改名後消失=${cleared}`);

  // F8b：打完立刻按儲存，儲存要等非同步檢查（外層 EditForm 的 ValidateAsync，SaveContext）。
  await nameInput.fill(cat2);
  await a.locator('.form-modal .ant-modal-footer button', { hasText: '儲存' }).click();
  const blockedToast = await waitForToast(a, '已存在').catch(() => '');
  const confirmShown = await a.locator('.ant-modal-confirm').isVisible().catch(() => false);
  await screenshot(a, 'fresh-F8b-立刻儲存被擋');
  recorder.add('F8b', '分類：輸入已存在的名稱後立刻按「儲存」，儲存等檢查完成並擋下，不跳確認窗', blockedToast !== '' && !confirmShown,
    `通知「${blockedToast.replace(/\s+/g, ' ').slice(0, 60)}」；出現確認窗=${confirmShown}`);
  if (confirmShown) {
    await a.locator('.ant-modal-confirm-btns button', { hasText: '再檢查' }).click().catch(() => {});
  }
  await closeForm(a);

  // F9：使用者帳號的非同步檢查。
  await openList(a, '/myusers');
  await clickToolbar(a, '新增');
  await a.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  const accountInput = a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '帳號' }) }).locator('input').first();
  await accountInput.fill('e2e-fresh-other');
  await a.locator('.form-modal .ant-form-item', { has: a.locator('label', { hasText: '名稱' }) }).locator('input').first().click();
  const accountConflict = await a.locator('.form-modal .validation-message', { hasText: '已存在' }).first().waitFor({ timeout: 10000 }).then(() => true).catch(() => false);
  await screenshot(a, 'fresh-F9-帳號已存在');
  recorder.add('F9', '使用者：輸入已存在的帳號，離開欄位就在欄位下方看到「已存在」', accountConflict, `出現=${accountConflict}`);
  await closeForm(a);

  // F10：丙停在分類頁，管理員（甲）拿掉丙角色的「新增」權限。
  const contextC = await newContext(browser);
  const c = await contextC.newPage();
  await login(c, { account: 'e2e-fresh-limited', password: limitedPassword });
  await openList(c, '/categories');
  const addButton = c.locator('button.toolbar-icon-button', { hasText: '新增' });
  const visibleBefore = await addButton.count() > 0;
  const roleId = query("SELECT Id FROM RoleView WHERE Name = 'E2E 分類維護F'")[0].Id;
  const role = await apiJsonAsPageUser(a, `/api/v1/roles/${roleId}`);
  const dto = role.json.data;
  for (const group of dto.permission.groups) {
    for (const node of group.permissions) {
      if (node.actions.create) {
        node.actions.create = false;
        node.enable = false;
      }
    }
  }
  const saved = await apiJsonAsPageUser(a, `/api/v1/roles/${roleId}`, { method: 'PUT', body: dto });
  await clickToolbar(c, '新增');
  await c.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  await c.locator('.form-modal .ant-form-item', { has: c.locator('label', { hasText: '名稱' }) }).locator('input').first().fill(`F權限${stamp}`);
  await c.locator('.form-modal .ant-form-item', { has: c.locator('label', { hasText: '描述' }) }).locator('textarea').click();
  await c.waitForTimeout(1500);
  // 名稱的即時檢查（POST check）這時已經沒有權限：記下畫面上出現的通知，關掉再存檔。
  const earlyToasts = (await c.locator('.ant-notification-notice').allInnerTexts()).map((x) => x.replace(/\s+/g, ' ').trim());
  await screenshot(c, 'fresh-F10-權限拿掉後輸入名稱');
  await closeToasts(c);
  await c.locator('.form-modal .ant-modal-footer button', { hasText: '儲存' }).click();
  await confirm(c, '儲存');
  await confirmIfShown(c, '仍要儲存', 3000);
  const deniedToast = await c.locator('.ant-notification-notice').last().innerText({ timeout: 15000 }).catch(() => '');
  await screenshot(c, 'fresh-F10-權限拿掉後存檔');
  const deniedCreated = query('SELECT COUNT(*) AS n FROM Category WHERE Name = ?', `F權限${stamp}`)[0].n;
  await closeForm(c);
  await c.reload();
  await openList(c, '/categories');
  const visibleAfter = await addButton.count() > 0;
  recorder.add('F10', '權限（既有機制）：角色被拿掉「新增」後，沒重新整理的畫面存檔被伺服器擋下；重新整理後按鈕消失',
    saved.status === 200 && visibleBefore && deniedCreated === 0 && !visibleAfter,
    `改角色 HTTP ${saved.status}；拿掉前看得到「新增」=${visibleBefore}；輸入名稱後的通知 ${JSON.stringify(earlyToasts).slice(0, 120)}；沒重新整理時存檔的通知「${deniedToast.replace(/\s+/g, ' ').slice(0, 80)}」、資料庫新增了 ${deniedCreated} 筆；重新整理後看得到「新增」=${visibleAfter}`);
  await contextC.close();

  await contextA.close();
  await contextB.close();
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
