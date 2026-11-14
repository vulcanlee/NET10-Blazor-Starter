// .NET 11 全面實機探索（0.10.16 起）：五個資料維護清單（分類、團隊、專案、使用者、角色）的共用操作，每一頁都逐項按過。
//   每頁先在隔離資料庫灌 30 筆帶「L列表」字樣的資料（另 1 筆已刪除），然後：
//   Lx-1 搜尋：輸入關鍵字按「搜尋」，每一列都含關鍵字；「清空搜尋」後恢復。
//   Lx-2 排序：每個可排序的欄位標題各點兩次（遞增、遞減），表格照常更新、第一列會變（值都相同的欄位除外）。
//   Lx-3 分頁：第 2 頁的資料與第 1 頁不同；每頁筆數改 20，列數變多。
//   Lx-4 顯示已刪除：看得到已刪除的那一筆，「返回清單」回來。
//   Lx-5 匯出 Excel（角色沒有匯出）：下載的是 xlsx（PK 開頭）。
//   Lx-6 操作說明：說明窗開得起來、有內容、關得掉。
//   每頁最後檢查：沒有頁面錯誤、主控台錯誤、CSP 違規、伺服器 5xx。
// 結果寫到 E2E_OUT_DIR/explore-lists-results.json，截圖 explore-lists-*.png。
import { readFileSync } from 'node:fs';
import { createRecorder, createUser, execute, launchBrowser, login, newContext, screenshot } from './common.mjs';
import { clickToolbar, openList, rowTexts } from './crud-ui.mjs';
import { collectProblems, insertRows, nowText } from './explore-common.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const token = 'L列表';
const pad = (i) => String(i).padStart(2, '0');

// ── 灌資料（30 筆＋1 筆已刪除；名稱與時間錯開，排序才看得出差別）──
const minutesAgo = (m) => {
  const d = new Date(Date.now() - m * 60000);
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
};
const deleted = { IsDeleted: 1, DeletedAt: nowText(), DeletedBy: 'support' };
insertRows('Category', [...Array(30).keys()].map((i) => ({ Name: `${token}分類${pad(i)}`, IsEnabled: i % 3 ? 1 : 0, Description: `說明${30 - i}`, CreatedAt: minutesAgo(i), UpdatedAt: minutesAgo(i) }))
  .concat([{ Name: `${token}分類已刪`, IsEnabled: 1, ...deleted }]));
insertRows('Team', [...Array(30).keys()].map((i) => ({ Name: `${token}團隊${pad(i)}`, Code: `LT${pad(i)}`, IsEnabled: i % 4 ? 1 : 0, CreatedAt: minutesAgo(i), UpdatedAt: minutesAgo(i) }))
  .concat([{ Name: `${token}團隊已刪`, IsEnabled: 1, ...deleted }]));
const statuses = ['未開始', '進行中', '已完成', '暫緩'];
insertRows('Project', [...Array(30).keys()].map((i) => ({
  Title: `${token}專案${pad(i)}`, Description: `說明${i}`, Status: statuses[i % 4], Priority: ['低', '中', '高'][i % 3], CompletionPercentage: (i * 7) % 101,
  Owner: `負責人${pad(29 - i)}`, StartDate: `2026-0${(i % 9) + 1}-01 00:00:00`, EndDate: '2026-12-31 00:00:00', CreatedAt: minutesAgo(i), UpdatedAt: minutesAgo(i),
})).concat([{ Title: `${token}專案已刪`, Status: '未開始', Priority: '中', Owner: '某人', ...deleted }]));
insertRows('RoleView', [...Array(30).keys()].map((i) => ({ Name: `${token}角色${pad(i)}`, TabViewJson: '[]', DefaultTeamsJson: '[]', CreateAt: minutesAgo(i), UpdateAt: minutesAgo(i) }))
  .concat([{ Name: `${token}角色已刪`, TabViewJson: '[]', DefaultTeamsJson: '[]', ...deleted }]));
const userPassword = 'List!E2e-' + Date.now().toString(36) + 'Aa9';
for (let i = 0; i < 30; i++) {
  createUser(`e2e-list${pad(i)}`, userPassword);
}
execute(`UPDATE MyUser SET Name = '${token}使用者' || substr(Account, 9) WHERE Account LIKE 'e2e-list%'`);
createUser('e2e-listdel', userPassword);
execute(`UPDATE MyUser SET Name = '${token}使用者已刪', IsDeleted = 1, DeletedAt = ?, DeletedBy = 'support' WHERE Account = 'e2e-listdel'`, nowText());

const pages = [
  { id: 'L1', route: '/categories', name: '分類', deletedName: `${token}分類已刪`, exports: true },
  { id: 'L2', route: '/teams', name: '團隊', deletedName: `${token}團隊已刪`, exports: true },
  { id: 'L3', route: '/projects', name: '專案', deletedName: `${token}專案已刪`, exports: true },
  { id: 'L4', route: '/myusers', name: '使用者', deletedName: `${token}使用者已刪`, exports: true },
  { id: 'L5', route: '/roleviews', name: '角色', deletedName: `${token}角色已刪`, exports: false },
];

const recorder = createRecorder('explore-lists-results.json');
const browser = await launchBrowser();

/** 等表格內容改變（或逾時）：回傳新的第一列文字。 */
async function waitRowsChange(page, before, timeout = 8000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    await page.waitForTimeout(200);
    const now = (await rowTexts(page)).join('\n');
    if (now !== before) {
      return now;
    }
  }

  return (await rowTexts(page)).join('\n');
}

async function search(page, text) {
  const input = page.locator('input[placeholder="請輸入搜尋關鍵字"]').first();
  await input.fill(text);
  const before = (await rowTexts(page)).join('\n');
  await clickToolbar(page, '搜尋');
  await waitRowsChange(page, before);
  await page.waitForTimeout(500);
}

try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const problems = collectProblems(page);
  await login(page);

  for (const p of pages) {
    await openList(page, p.route);
    await problems.take();

    // 1. 搜尋。
    await search(page, token);
    const searched = await rowTexts(page);
    const allMatch = searched.length > 0 && searched.every((x) => x.includes(token));
    await clickToolbar(page, '清空搜尋');
    await page.waitForTimeout(1500);
    const cleared = await rowTexts(page);
    recorder.add(`${p.id}-1`, `${p.name}：搜尋只列出符合的、清空後恢復`, allMatch && cleared.length > 0,
      `搜尋後 ${searched.length} 列、全部含關鍵字=${allMatch}；清空後 ${cleared.length} 列`);

    // 2. 排序：每個可排序的欄位點兩次。
    await search(page, token);
    const headers = page.locator('th.ant-table-column-has-sorters');
    const headerCount = await headers.count();
    const sortNotes = [];
    for (let i = 0; i < headerCount; i++) {
      const title = (await headers.nth(i).innerText()).trim();
      const start = (await rowTexts(page)).join('\n');
      await headers.nth(i).click();
      const first = await waitRowsChange(page, start, 5000);
      await headers.nth(i).click();
      const second = await waitRowsChange(page, first, 5000);
      sortNotes.push(`${title}${first.split('\n')[0] === second.split('\n')[0] ? '（第一列沒變）' : ''}`);
    }
    await screenshot(page, `explore-lists-${p.id}-排序`);
    const sortProblems = await problems.take();
    recorder.add(`${p.id}-2`, `${p.name}：每個可排序欄位遞增、遞減都正常`, headerCount > 0 && sortProblems.length === 0,
      `${headerCount} 個欄位：${sortNotes.join('、')}${sortProblems.length ? '；問題：' + sortProblems.join('；') : ''}`);

    // 3. 分頁與每頁筆數。
    const page1 = (await rowTexts(page)).join('\n');
    await page.locator('.ant-pagination-item-2').first().click();
    const page2 = await waitRowsChange(page, page1);
    let sizeRows = 0;
    const sizeChanger = page.locator('.ant-pagination-options .ant-select-selector').first();
    if (await sizeChanger.count()) {
      await sizeChanger.click();
      await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option', { hasText: '20' }).first().click();
      await waitRowsChange(page, page2);
      await page.waitForTimeout(800);
      sizeRows = (await rowTexts(page)).length;
    }
    recorder.add(`${p.id}-3`, `${p.name}：第 2 頁與第 1 頁不同；每頁改 20 筆`, page2 !== page1 && page2.length > 0 && (sizeRows === 0 || sizeRows > 10),
      `第 2 頁不同=${page2 !== page1}；每頁 20 筆後 ${sizeRows || '（沒有每頁筆數選單）'} 列`);

    // 4. 顯示已刪除。
    await clickToolbar(page, '顯示已刪除');
    await page.waitForTimeout(2000);
    const deletedRows = await rowTexts(page);
    await screenshot(page, `explore-lists-${p.id}-已刪除`);
    await clickToolbar(page, '返回清單');
    await page.waitForTimeout(1500);
    const back = await rowTexts(page);
    recorder.add(`${p.id}-4`, `${p.name}：顯示已刪除看得到刪掉的、返回清單回來`,
      deletedRows.some((x) => x.includes(p.deletedName)) && !back.some((x) => x.includes(p.deletedName)),
      `已刪除清單含「${p.deletedName}」=${deletedRows.some((x) => x.includes(p.deletedName))}；返回後不含=${!back.some((x) => x.includes(p.deletedName))}`);

    // 5. 匯出 Excel。
    if (p.exports) {
      const [download] = await Promise.all([page.waitForEvent('download', { timeout: 30000 }), clickToolbar(page, '匯出 Excel')]);
      const file = await download.path();
      const head = file ? readFileSync(file).subarray(0, 2).toString('latin1') : '';
      recorder.add(`${p.id}-5`, `${p.name}：匯出 Excel 下載的是 xlsx`, head === 'PK', `${download.suggestedFilename()}，檔頭 ${head}`);
    }

    // 6. 操作說明。
    await page.locator('.page-help-button').click();
    const help = page.locator('.page-help-modal').last();
    await help.waitFor({ state: 'visible', timeout: 15000 });
    await page.waitForTimeout(800);
    const helpText = (await help.innerText()).length;
    await help.locator('.ant-modal-close').click();
    await help.waitFor({ state: 'hidden', timeout: 10000 });
    const finalProblems = await problems.take();
    recorder.add(`${p.id}-6`, `${p.name}：操作說明開得起來、有內容；整頁沒有錯誤`, helpText > 200 && finalProblems.length === 0,
      `說明 ${helpText} 字；${finalProblems.length ? '問題：' + finalProblems.join('；') : '沒有錯誤'}`);
  }

  await context.close();
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
