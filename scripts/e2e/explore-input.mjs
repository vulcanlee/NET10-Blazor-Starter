// .NET 11 全面實機探索（0.10.16 起）：錯誤輸入與對話窗的誤觸。每一項都看「畫面怎麼說」與「資料庫有沒有被寫入」。
//   I1 分類名稱留空 → 擋下、沒寫入。                I2 只有空白 → 擋下。
//   I3 名稱 101 字 → 擋下（上限 100）。             I4 描述 2001 字 → 擋下（上限 2000）。
//   I5 名稱含 emoji 與 HTML 字串 → 存得進去，清單照字面顯示（不會變成元素、不會執行）。
//   I6 重名的變形：大小寫不同、前後有半形與全形空白 → 一律擋下。
//   I7 搜尋含 % _ ' " \ 等特殊字元 → 不出錯，只找到真的含那個字元的資料（不是萬用字元）。
//   I8 專案結束日期早於開始日期（API）→ 400。      I9 完成百分比 150（API）→ 400。
//   I10 專案標題 5,000 字（API）→ 不可以 5xx（記錄伺服器怎麼處理）。
//   I11 使用者 Email 格式錯誤 → 擋下。              I12 使用者帳號含空白與 HTML（API）→ 不可以 5xx（記錄處理方式）。
//   I13 在名稱欄按 Enter → 對話窗不關、沒寫入。     I14 改了內容按右上角 ✕ → 問要不要放棄，選「放棄變更」才關、沒寫入。
//   I15 改了內容、打開下拉後按 Esc（Esc 會連對話窗一起關）→ 一樣先問要不要放棄，選「繼續編輯」內容還在。
//   每一項都檢查沒有頁面錯誤、主控台錯誤、伺服器 5xx。
// 結果寫到 E2E_OUT_DIR/explore-input-results.json，截圖 explore-input-*.png。
import { apiJsonAsPageUser, createRecorder, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { clickToolbar, closeToasts, confirm, confirmIfShown, formField, openList, rowTexts, submitForm } from './crud-ui.mjs';
import { collectProblems, insertRows } from './explore-common.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const stamp = Date.now().toString(36).slice(-4);
insertRows('Category', [
  { Name: `I百分比%分類${stamp}`, IsEnabled: 1 },
  { Name: `I底線_分類${stamp}`, IsEnabled: 1 },
  { Name: `I引號'"分類${stamp}`, IsEnabled: 1 },
  { Name: `I一般分類${stamp}`, IsEnabled: 1 },
  { Name: `Abc${stamp}`, IsEnabled: 1 },
]);

const recorder = createRecorder('explore-input-results.json');
const browser = await launchBrowser();
const categoryCount = (name) => query('SELECT COUNT(*) AS n FROM Category WHERE Name = ?', name)[0].n;

/** 開「新增分類」、填欄位、按儲存並確認；回傳出現的通知文字（沒有就空字串）與對話窗是否還開著。 */
async function tryAddCategory(page, fields) {
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  for (const [label, value] of Object.entries(fields)) {
    await formField(page, label).fill(value);
  }

  await formField(page, '描述').click(); // 離開欄位，讓即時檢查跑完
  await page.waitForTimeout(600);
  await submitForm(page);
  // 驗證失敗時不會跳確認窗；通過時才有「確認儲存」與團隊提醒。
  if (await confirmIfShown(page, '儲存', 2500)) {
    await confirmIfShown(page, '仍要儲存', 2000);
  }

  const toast = await page.locator('.ant-notification-notice').last().innerText({ timeout: 8000 }).catch(() => '');
  const open = await page.locator('.form-modal').isVisible();
  return { toast: toast.replace(/\s+/g, ' ').trim(), open };
}

async function closeFormDiscard(page) {
  await page.waitForTimeout(800); // 存檔成功時對話窗正在關閉的動畫中
  const close = page.locator('.form-modal .ant-modal-close:visible');
  if (await close.count()) {
    await close.first().click();
    await confirmIfShown(page, '放棄變更', 1500);
    await page.locator('.form-modal').waitFor({ state: 'hidden', timeout: 10000 }).catch(() => {});
  }

  await closeToasts(page);
}

try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const problems = collectProblems(page);
  await login(page);
  await openList(page, '/categories');

  const blocked = async (id, title, fields, dbName) => {
    const r = await tryAddCategory(page, fields);
    await screenshot(page, `explore-input-${id}`);
    const written = dbName === undefined ? 0 : categoryCount(dbName);
    const issues = await problems.take();
    recorder.add(id, title, r.open && written === 0 && r.toast !== '' && issues.length === 0,
      `通知「${r.toast.slice(0, 80)}」；對話窗仍開著=${r.open}；資料庫寫入 ${written} 筆${issues.length ? '；問題：' + issues.join('；') : ''}`);
    await closeFormDiscard(page);
  };

  await blocked('I1', '分類名稱留空：擋下、沒寫入', { 名稱: '' }, '');
  await blocked('I2', '分類名稱只有空白：擋下、沒寫入', { 名稱: '   ' }, '   ');
  await blocked('I3', '分類名稱 101 字：擋下', { 名稱: '長'.repeat(101) }, '長'.repeat(101));
  await blocked('I4', '分類描述 2001 字：擋下', { 名稱: `I描述太長${stamp}`, 描述: '述'.repeat(2001) }, `I描述太長${stamp}`);

  // I5：emoji 與 HTML。
  const htmlName = `I<img src=x onerror="window.__xss=1">🎉${stamp}`;
  const r5 = await tryAddCategory(page, { 名稱: htmlName });
  await closeFormDiscard(page);
  await page.locator('input[placeholder="請輸入搜尋關鍵字"]').fill(stamp);
  await clickToolbar(page, '搜尋');
  await page.waitForTimeout(1500);
  const rows5 = await rowTexts(page);
  const injected = await page.evaluate(() => Boolean(window.__xss) || document.querySelectorAll('.ant-table img[src="x"]').length > 0);
  await screenshot(page, 'explore-input-I5');
  recorder.add('I5', '名稱含 emoji 與 HTML：存得進去、照字面顯示、不會執行', categoryCount(htmlName) === 1 && rows5.some((x) => x.includes('🎉') && x.includes('<img')) && !injected,
    `通知「${r5.toast.slice(0, 40)}」；資料庫 ${categoryCount(htmlName)} 筆；清單照字面顯示=${rows5.some((x) => x.includes('<img'))}；被當成元素或執行=${injected}`);

  // I6：重名的變形。
  const variants = [`abc${stamp}`, `ABC${stamp}`, ` Abc${stamp} `, `　Abc${stamp}　`];
  const results6 = [];
  for (const name of variants) {
    const r = await tryAddCategory(page, { 名稱: name });
    results6.push({ name: JSON.stringify(name), blocked: r.open && r.toast.includes('已存在') });
    await closeFormDiscard(page);
  }
  const total6 = query("SELECT COUNT(*) AS n FROM Category WHERE lower(trim(replace(Name, '　', ''))) = lower(?)", `abc${stamp}`)[0].n;
  recorder.add('I6', '重名的變形（大小寫、半形與全形空白）一律擋下', results6.every((x) => x.blocked) && total6 === 1,
    `${results6.map((x) => `${x.name}→${x.blocked ? '擋下' : '沒擋'}`).join('、')}；資料庫同名 ${total6} 筆`);

  // I7：搜尋特殊字元。
  const searches = [['%', `I百分比%分類${stamp}`], ['_', `I底線_分類${stamp}`], ["'", `I引號'"分類${stamp}`], ['"', `I引號'"分類${stamp}`], ['\\', null]];
  const results7 = [];
  for (const [text, expected] of searches) {
    await page.locator('input[placeholder="請輸入搜尋關鍵字"]').fill(text);
    await clickToolbar(page, '搜尋');
    await page.waitForTimeout(1500);
    const rows = await rowTexts(page);
    const ok = expected === null ? rows.every((x) => x.includes('\\')) : rows.length >= 1 && rows.every((x) => x.includes(text));
    results7.push(`「${text}」${rows.length} 列${ok ? '' : '（不符）'}`);
  }
  const issues7 = await problems.take();
  recorder.add('I7', '搜尋特殊字元（% _ \' " \\）不出錯、不當成萬用字元', results7.every((x) => !x.includes('不符')) && issues7.length === 0,
    `${results7.join('、')}${issues7.length ? '；問題：' + issues7.join('；') : ''}`);

  // I8～I10：專案（API）。
  const project = (extra) => ({ id: 0, title: `I專案${stamp}${Math.random().toString(36).slice(2, 6)}`, owner: '王小明', status: '未開始', priority: '中', completionPercentage: 0, categories: null, teams: null, ...extra });
  const r8 = await apiJsonAsPageUser(page, '/api/v1/Project', { method: 'POST', body: project({ startDate: '2026-12-01T00:00:00', endDate: '2026-01-01T00:00:00' }) });
  recorder.add('I8', '專案結束日期早於開始日期：400', r8.status === 400, `HTTP ${r8.status}「${r8.json?.errorMessage ?? r8.json?.message ?? ''}」`);
  const r9 = await apiJsonAsPageUser(page, '/api/v1/Project', { method: 'POST', body: project({ completionPercentage: 150 }) });
  recorder.add('I9', '完成百分比 150：400', r9.status === 400, `HTTP ${r9.status}「${r9.json?.errorMessage ?? r9.json?.message ?? ''}」`);
  const r10 = await apiJsonAsPageUser(page, '/api/v1/Project', { method: 'POST', body: project({ title: '題'.repeat(5000) }) });
  recorder.add('I10', '專案標題 5,000 字：不可以 5xx（記錄處理方式）', r10.status < 500, `HTTP ${r10.status}「${r10.json?.errorMessage ?? r10.json?.message ?? ''}」`);

  // I11、I12：使用者。
  await openList(page, '/myusers');
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  await formField(page, '帳號').fill(`e2e-mail${stamp}`);
  await formField(page, '密碼').fill('Good!Pass2026x');
  await formField(page, '名稱').fill('Email 錯');
  await formField(page, 'Email').fill('not-an-email');
  await submitForm(page);
  await confirmIfShown(page, '儲存', 2500);
  await confirmIfShown(page, '仍要儲存', 2000);
  const toast11 = (await page.locator('.ant-notification-notice').last().innerText({ timeout: 8000 }).catch(() => '')).replace(/\s+/g, ' ');
  const written11 = query('SELECT COUNT(*) AS n FROM MyUser WHERE Account = ?', `e2e-mail${stamp}`)[0].n;
  await screenshot(page, 'explore-input-I11');
  recorder.add('I11', '使用者 Email 格式錯誤：擋下、沒寫入', written11 === 0 && toast11 !== '', `通知「${toast11.slice(0, 80)}」；資料庫寫入 ${written11} 筆`);
  await closeFormDiscard(page);

  const roleId = query("SELECT RoleViewId AS id FROM MyUser WHERE Account = 'support'")[0].id;
  const r12 = await apiJsonAsPageUser(page, '/api/v1/users', { method: 'POST', body: { id: 0, account: `bad account<b>${stamp}`, name: '怪帳號', password: 'Good!Pass2026x', roleViewId: roleId, teamNames: [], additionalRoleIds: [] } });
  recorder.add('I12', '使用者帳號含空白與 HTML（API）：不可以 5xx（記錄處理方式）', r12.status < 500, `HTTP ${r12.status}「${r12.json?.errorMessage ?? r12.json?.message ?? ''}」`);

  // I13：名稱欄按 Enter。
  await openList(page, '/categories');
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  await formField(page, '名稱').fill(`I按Enter${stamp}`);
  await formField(page, '名稱').press('Enter');
  await page.waitForTimeout(1500);
  const open13 = await page.locator('.form-modal').isVisible();
  recorder.add('I13', '在名稱欄按 Enter：對話窗不關、沒寫入', open13 && categoryCount(`I按Enter${stamp}`) === 0, `對話窗仍開著=${open13}；資料庫 ${categoryCount(`I按Enter${stamp}`)} 筆`);

  // I14：改了內容按 ✕。
  await page.locator('.form-modal .ant-modal-close').click();
  const asked = await page.locator('.ant-modal-confirm').last().isVisible({ timeout: 5000 }).catch(() => false);
  await screenshot(page, 'explore-input-I14');
  await confirmIfShown(page, '放棄變更', 3000);
  await page.locator('.form-modal').waitFor({ state: 'hidden', timeout: 10000 }).catch(() => {});
  const closed = !(await page.locator('.form-modal').isVisible());
  const issues = await problems.take();
  recorder.add('I14', '改了內容按 ✕：先問要不要放棄，選「放棄變更」才關、沒寫入', asked && closed && categoryCount(`I按Enter${stamp}`) === 0 && issues.length === 0,
    `問了=${asked}；關掉=${closed}${issues.length ? '；問題：' + issues.join('；') : ''}`);

  // I15：改了內容、打開「適用團隊」下拉後按 Esc。
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  await formField(page, '名稱').fill(`I按Esc${stamp}`);
  await page.locator('.form-modal .ant-form-item', { has: page.locator('label', { hasText: '適用團隊' }) }).locator('.ant-select-selector').click();
  await page.waitForTimeout(500);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(800);
  const asked15 = await page.locator('.ant-modal-confirm').last().isVisible().catch(() => false);
  await screenshot(page, 'explore-input-I15');
  let kept = false;
  if (asked15) {
    const buttons = await page.locator('.ant-modal-confirm').last().locator('.ant-modal-confirm-btns button').allInnerTexts();
    const stay = buttons.find((x) => !x.includes('放棄')) ?? buttons[0];
    await confirmIfShown(page, stay.trim(), 2000);
    kept = (await formField(page, '名稱').inputValue().catch(() => '')) === `I按Esc${stamp}`;
  }
  const open15 = await page.locator('.form-modal .ant-modal-close:visible').count() > 0;
  recorder.add('I15', '改了內容、打開下拉後按 Esc：先問要不要放棄，選不放棄時內容還在', asked15 && kept && open15,
    `問了=${asked15}；選不放棄後對話窗還開著=${open15}、內容還在=${kept}`);
  await closeFormDiscard(page);

  await context.close();
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
