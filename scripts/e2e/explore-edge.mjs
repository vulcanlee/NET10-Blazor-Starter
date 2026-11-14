// .NET 11 全面實機探索（0.10.16 起）：邊界情境。
//   E1 大量資料：灌 2,000 筆專案、300 個分類。專案清單開得起來（記時間）、搜尋找得到最後一筆、跳到最後一頁、匯出 2,000 筆的 Excel；
//      新增專案的分類下拉有 300 多個選項，打字篩選正常。
//   E2 慢速網路（CDP：延遲 400 毫秒、下載約 1.5 Mbps）：新增分類成功；儲存確認窗連按兩下「儲存」只會寫入一筆。
//   E3 離線：清單按「重新整理」顯示離線訊息、畫面不壞；恢復連線後重新整理正常。
//   E4 手機尺寸（390×844）：每個登入後路由都開得起來、整頁沒有橫向捲動、沒有錯誤，並截圖；0.10.17 起收合的側邊欄只留選單鈕，頁首只有一列（不超過 80px）。
//   E5 英文瀏覽器語系＋東京時區、UTC 時區：同一筆資料的時間與中文、台北時區看到的一字不差（時間照伺服器的本地時間顯示）。
// 結果寫到 E2E_OUT_DIR/explore-edge-results.json，截圖 explore-edge-*.png。
import { readFileSync } from 'node:fs';
import { createRecorder, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { clickToolbar, closeToasts, confirm, confirmIfShown, formField, openList, rowTexts, submitForm, waitForToast } from './crud-ui.mjs';
import { collectProblems, insertRows } from './explore-common.mjs';
import { authenticatedRoutes } from './smoke-routes.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const stamp = Date.now().toString(36).slice(-4);
const pad = (n, w) => String(n).padStart(w, '0');
insertRows('Category', [...Array(300).keys()].map((i) => ({ Name: `E大量分類${pad(i + 1, 3)}`, IsEnabled: 1 })));
insertRows('Project', [...Array(2000).keys()].map((i) => ({
  Title: `E大量專案${pad(i + 1, 4)}`, Description: `大量資料第 ${i + 1} 筆，說明文字稍微長一點，模擬實際資料。`, Status: ['未開始', '進行中', '已完成', '暫緩'][i % 4],
  Priority: ['低', '中', '高'][i % 3], CompletionPercentage: i % 101, Owner: `負責人${i % 37}`, StartDate: '2026-01-01 00:00:00', EndDate: '2026-12-31 00:00:00',
  Categories: `\nE大量分類${pad((i % 300) + 1, 3)}\n`,
})));

const recorder = createRecorder('explore-edge-results.json');
const browser = await launchBrowser();

async function search(page, text) {
  await page.locator('input[placeholder="請輸入搜尋關鍵字"]').first().fill(text);
  await clickToolbar(page, '搜尋');
  await page.waitForTimeout(1500);
}

try {
  // ── E1 大量資料 ──
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    const problems = collectProblems(page);
    await login(page);
    let started = Date.now();
    await openList(page, '/projects');
    const openMs = Date.now() - started;
    await search(page, 'E大量專案2000');
    const found = await rowTexts(page);
    await search(page, 'E大量專案');
    const lastItem = page.locator('.ant-pagination-item').last();
    const lastNo = await lastItem.innerText();
    started = Date.now();
    await lastItem.click();
    await page.waitForTimeout(1500);
    const lastRows = await rowTexts(page);
    const lastMs = Date.now() - started;
    started = Date.now();
    const [download] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), clickToolbar(page, '匯出 Excel')]);
    const file = await download.path();
    const exportMs = Date.now() - started;
    const exportBytes = file ? readFileSync(file).length : 0;
    // 新增專案的分類下拉：300 多個選項，打字篩選。
    await clickToolbar(page, '新增');
    await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
    const categoryItem = page.locator('.form-modal .ant-form-item', { has: page.locator('label', { hasText: '分類' }) }).first();
    await categoryItem.locator('.ant-select-selector').click();
    await categoryItem.locator('input.ant-select-selection-search-input').pressSequentially('E大量分類29', { delay: 30 });
    await page.waitForTimeout(800);
    const filtered = await page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option:visible').allInnerTexts();
    await screenshot(page, 'explore-edge-E1-分類下拉篩選');
    // 點對話窗標題收起下拉（Esc 會連對話窗一起關掉），再關窗。
    await page.locator('.form-modal .ant-modal-title').click();
    await page.waitForTimeout(500);
    if (await page.locator('.form-modal .ant-modal-close:visible').count()) {
      await page.locator('.form-modal .ant-modal-close:visible').click();
      await confirmIfShown(page, '放棄變更', 1500);
    }
    const issues = await problems.take();
    recorder.add('E1', '大量資料（2,000 專案、300 分類）：開清單、搜尋、最後一頁、匯出、分類下拉篩選都正常',
      found.length === 1 && lastRows.length > 0 && exportBytes > 10000 && filtered.length >= 10 && filtered.every((x) => x.includes('E大量分類29')) && issues.length === 0,
      `開清單 ${openMs} 毫秒；搜尋最後一筆 ${found.length} 列；跳到第 ${lastNo} 頁 ${lastMs} 毫秒、${lastRows.length} 列；匯出 ${exportMs} 毫秒、${exportBytes} 位元組；下拉篩選「E大量分類29」${filtered.length} 項${issues.length ? '；問題：' + issues.join('；') : ''}`);
    await context.close();
  }

  // ── E2 慢速網路 ──
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    const problems = collectProblems(page);
    await login(page);
    await openList(page, '/categories');
    const cdp = await context.newCDPSession(page);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 400, downloadThroughput: (1.5 * 1024 * 1024) / 8, uploadThroughput: (750 * 1024) / 8 });
    const name = `E慢速分類${stamp}`;
    await clickToolbar(page, '新增');
    await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 30000 });
    await formField(page, '名稱').fill(name);
    await formField(page, '描述').click();
    await page.waitForTimeout(2000);
    await submitForm(page);
    // 確認窗的「儲存」連按兩下。
    const dialog = page.locator('.ant-modal-confirm').last();
    await dialog.waitFor({ state: 'visible', timeout: 15000 });
    const okButton = dialog.locator('.ant-modal-confirm-btns button', { hasText: '儲存' });
    await okButton.click();
    await okButton.click({ timeout: 1000 }).catch(() => {});
    await confirmIfShown(page, '仍要儲存', 5000);
    const toast = await waitForToast(page, '新增成功', 30000).catch(() => '');
    await page.waitForTimeout(3000);
    const written = query('SELECT COUNT(*) AS n FROM Category WHERE Name = ?', name)[0].n;
    await screenshot(page, 'explore-edge-E2-慢速網路');
    await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    const issues = await problems.take();
    recorder.add('E2', '慢速網路：新增成功；確認窗連按兩下「儲存」只寫入一筆', toast !== '' && written === 1 && issues.length === 0,
      `通知「${toast.replace(/\s+/g, ' ').slice(0, 30)}」；資料庫 ${written} 筆${issues.length ? '；問題：' + issues.join('；') : ''}`);

    // ── E3 離線 ──
    await closeToasts(page);
    await context.setOffline(true);
    await clickToolbar(page, '重新整理');
    const offlineToast = await page.locator('.ant-notification-notice').last().innerText({ timeout: 10000 }).catch(() => '');
    const offlineBar = await page.locator('.app-offline-bar').count();
    await screenshot(page, 'explore-edge-E3-離線');
    const rowsOffline = await rowTexts(page);
    await context.setOffline(false);
    await page.waitForTimeout(3000);
    await closeToasts(page);
    await clickToolbar(page, '重新整理');
    const onlineToast = await waitForToast(page, '已更新最新資料', 15000).catch(() => '');
    const issues3 = (await problems.take()).filter((x) => !/ERR_INTERNET_DISCONNECTED|Failed to fetch|NetworkError/.test(x));
    recorder.add('E3', '離線：重新整理顯示離線訊息、表格保留；恢復後重新整理正常', offlineToast.includes('離線') && rowsOffline.length > 0 && onlineToast !== '' && issues3.length === 0,
      `離線通知「${offlineToast.replace(/\s+/g, ' ').slice(0, 40)}」；離線提示列 ${offlineBar}；表格保留 ${rowsOffline.length} 列；恢復後「${onlineToast.replace(/\s+/g, ' ').slice(0, 20)}」${issues3.length ? '；問題：' + issues3.join('；') : ''}`);
    await context.close();
  }

  // ── E4 手機尺寸 ──
  {
    const context = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: process.env.E2E_BASE_URL ?? 'https://localhost:7144', viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, locale: 'zh-TW' });
    const page = await context.newPage();
    const problems = collectProblems(page);
    await login(page);
    const bad = [];
    let index = 0;
    for (const route of authenticatedRoutes) {
      await page.goto(route);
      await page.waitForTimeout(2500);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      // 已知殘留（速查表 §10）：AntDesign 頁籤的 ondragover 行內事件被 CSP 擋下，刻意不放行，沒有功能影響（m11-split S7 同樣排除）。
      const issues = (await problems.take()).filter((x) => !(['/token-usage', '/prompt-templates'].includes(route) && /Executing inline event handler/.test(x)));
      await screenshot(page, `explore-edge-E4-${String(++index).padStart(2, '0')}${route.replace(/[^A-Za-z0-9]+/g, '_')}`);
      if (overflow > 1 || issues.length) {
        bad.push(`${route}${overflow > 1 ? ` 橫向多出 ${overflow}px` : ''}${issues.length ? ' ' + issues.join('；') : ''}`);
      }
    }
    await page.goto('/App');
    await page.waitForTimeout(2500);
    const header = await page.evaluate(() => {
      const main = document.querySelector('main > .top-row')?.getBoundingClientRect();
      const ham = document.querySelector('.sidebar-hamburger')?.getBoundingClientRect();
      return { mainTop: Math.round(main?.top ?? -1), mainBottom: Math.round(main?.bottom ?? -1), hamBottom: Math.round(ham?.bottom ?? -1) };
    });
    const oneRow = header.mainTop === 0 && header.mainBottom <= 80 && header.hamBottom <= header.mainBottom;
    recorder.add('E4', '手機尺寸（390×844）：每個路由開得起來、沒有橫向捲動、沒有錯誤；收合時頁首只有一列', bad.length === 0 && oneRow,
      `${authenticatedRoutes.length} 個路由；${bad.length ? '不符：' + bad.join('；') : '全部正常'}；頁首 ${header.mainTop}～${header.mainBottom}px、選單鈕下緣 ${header.hamBottom}px`);
    await context.close();
  }

  // ── E5 語系與時區 ──
  {
    const target = 'E大量專案0001';
    const readRow = async (locale, timezoneId) => {
      const context = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: process.env.E2E_BASE_URL ?? 'https://localhost:7144', viewport: { width: 1440, height: 900 }, locale, timezoneId });
      const page = await context.newPage();
      const problems = collectProblems(page);
      await login(page);
      await openList(page, '/projects');
      await search(page, target);
      const rows = await rowTexts(page);
      await page.goto('/audit-logs');
      await page.waitForSelector('.audit-log-table', { timeout: 60000 });
      await page.waitForTimeout(2000);
      const audit = (await page.locator('.audit-log-table .ant-table-row').first().innerText().catch(() => '')).replace(/\s+/g, ' ');
      await screenshot(page, `explore-edge-E5-${locale}-${timezoneId.replace('/', '_')}`);
      const issues = await problems.take();
      await context.close();
      return { row: (rows[0] ?? '').replace(/\s+/g, ' '), audit, issues };
    };
    const base = await readRow('zh-TW', 'Asia/Taipei');
    const tokyo = await readRow('en-US', 'Asia/Tokyo');
    const utc = await readRow('en-US', 'UTC');
    const dates = (x) => (x.match(/\d{4}[/-]\d{1,2}[/-]\d{1,2}[^\s]*(\s*(上午|下午|AM|PM)?\s*\d{1,2}:\d{2}(:\d{2})?)?/g) ?? []).join(' | ');
    const same = dates(base.row) === dates(tokyo.row) && dates(base.row) === dates(utc.row);
    const issues = [...base.issues, ...tokyo.issues, ...utc.issues];
    recorder.add('E5', '英文語系＋東京、UTC 時區：專案的日期時間與中文台北一字不差，畫面沒有錯誤', same && dates(base.row) !== '' && issues.length === 0,
      `台北：${dates(base.row)}；東京：${dates(tokyo.row)}；UTC：${dates(utc.row)}；稽核第一列 台北「${base.audit.slice(0, 60)}」東京「${tokyo.audit.slice(0, 60)}」${issues.length ? '；問題：' + issues.join('；') : ''}`);
  }
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
