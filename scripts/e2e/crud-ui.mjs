// 資料維護頁（分類、團隊、CRUD 產生器產生的頁面）的共用操作（0.10.2 起，WASM 遷移 M3）。
// 選擇器依共用元件的標記：ToolbarIconButton（.toolbar-icon-button）、CrudActionButton（.crud-action-button）、
// 表單對話窗（.form-modal）、確認窗（AntDesign confirm：.ant-modal-confirm）、右下角通知（.ant-notification-notice）。

/** 打開清單頁，等表格出現（有資料列或空白提示）。 */
export async function openList(page, route) {
  await page.goto(route);
  await page.waitForSelector('.ant-table-wrapper', { timeout: 60000 });
  await page.waitForSelector('.ant-table-row, .ant-empty', { timeout: 30000 });
  await page.waitForTimeout(500);
}

export async function clickToolbar(page, title) {
  await page.locator('button.toolbar-icon-button', { hasText: title }).first().click();
}

/** 某一列的操作按鈕（修改、刪除、還原、永久刪除、異動歷程）。 */
export async function clickRowAction(page, rowText, title) {
  await page.locator('.ant-table-row', { hasText: rowText }).first().locator('button.crud-action-button', { hasText: title }).click();
}

/** 確認窗按下指定的按鈕（例如「儲存」「刪除」「還原」）；等它出現。 */
export async function confirm(page, okText) {
  const dialog = page.locator('.ant-modal-confirm').last();
  await dialog.waitFor({ state: 'visible', timeout: 15000 });
  await dialog.locator('.ant-modal-confirm-btns button', { hasText: okText }).click();
  await dialog.waitFor({ state: 'hidden', timeout: 15000 });
}

/** 若出現指定標題的確認窗就按下確定（例如分類未指定團隊的提醒）；沒出現就略過。 */
export async function confirmIfShown(page, okText, timeout = 1500) {
  const dialog = page.locator('.ant-modal-confirm').last();
  try {
    await dialog.waitFor({ state: 'visible', timeout });
  } catch {
    return false;
  }

  await dialog.locator('.ant-modal-confirm-btns button', { hasText: okText }).click();
  await dialog.waitFor({ state: 'hidden', timeout: 15000 });
  return true;
}

/** 表單對話窗的欄位（依標籤找，例如「名稱」「描述」「代號」）。 */
export function formField(page, label) {
  return page.locator('.form-modal .ant-form-item', { has: page.locator('label', { hasText: label }) }).locator('input, textarea').first();
}

/** 按表單對話窗的「儲存」。 */
export async function submitForm(page) {
  await page.locator('.form-modal .ant-modal-footer button', { hasText: '儲存' }).click();
}

/** 等右下角出現含指定文字的通知，回傳通知全文；逾時丟例外。 */
export async function waitForToast(page, text, timeout = 15000) {
  const toast = page.locator('.ant-notification-notice', { hasText: text }).last();
  await toast.waitFor({ state: 'visible', timeout });
  return toast.innerText();
}

/** 關掉所有右下角通知（錯誤通知不會自動消失）。 */
export async function closeToasts(page) {
  for (const close of await page.locator('.ant-notification-notice-close').all()) {
    await close.click().catch(() => {});
  }
}

/**
 * 新增一筆：按「新增」→ 填欄位 → 儲存 → 確認儲存 →（有的話）團隊提醒。
 * fields：{ 標籤: 值 }。回傳結果通知的文字（成功是「新增成功」）。
 */
export async function addRecord(page, fields, { expect = '新增成功', extraConfirm } = {}) {
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  for (const [label, value] of Object.entries(fields)) {
    await formField(page, label).fill(value);
  }

  await submitForm(page);
  await confirm(page, '儲存');
  if (extraConfirm) {
    await confirmIfShown(page, extraConfirm);
  }

  return waitForToast(page, expect);
}

/** 修改一筆：按該列的「修改」→ 改欄位 → 儲存 → 確認。 */
export async function editRecord(page, rowText, fields, { expect = '修改成功', extraConfirm } = {}) {
  await clickRowAction(page, rowText, '修改');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  for (const [label, value] of Object.entries(fields)) {
    await formField(page, label).fill(value);
  }

  await submitForm(page);
  await confirm(page, '儲存');
  if (extraConfirm) {
    await confirmIfShown(page, extraConfirm);
  }

  return waitForToast(page, expect);
}

/** 關掉表單對話窗（按右上角 ✕；有未儲存變更時選「放棄變更」）。Esc 要焦點在對話窗裡才有效，所以不用它。 */
export async function closeForm(page) {
  await page.locator('.form-modal .ant-modal-close').click();
  await confirmIfShown(page, '放棄變更', 1000);
  await page.locator('.form-modal').waitFor({ state: 'hidden', timeout: 15000 });
}

export async function deleteRecord(page, rowText) {
  await clickRowAction(page, rowText, '刪除');
  await confirm(page, '刪除');
  return waitForToast(page, '刪除');
}

export async function restoreRecord(page, rowText) {
  await clickRowAction(page, rowText, '還原');
  await confirm(page, '還原');
  return waitForToast(page, '還原');
}

export async function purgeRecord(page, rowText) {
  await clickRowAction(page, rowText, '永久刪除');
  await confirm(page, '永久刪除');
  return waitForToast(page, '永久刪除');
}

/** 目前表格上看得到的列文字。 */
export async function rowTexts(page) {
  return page.locator('.ant-table-row').allInnerTexts();
}

/** 收集頁面錯誤與 Blazor 的未處理例外（錯誤列 #blazor-error-ui）。 */
export function watchErrors(page) {
  const errors = [];
  page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
  page.on('console', (m) => {
    if (m.type() === 'error' && /Unhandled exception|crit:/i.test(m.text())) {
      errors.push(`console: ${m.text().slice(0, 300)}`);
    }
  });
  return errors;
}
