// CRUD 產生器產生的頁面實跑（0.10.2 起，由 scripts/Test-CrudGenerator.ps1 在暫存 worktree 呼叫）：
// 每個頁面在 WASM 執行，各做一次新增、修改、刪除、顯示已刪除、還原，且沒有未處理的錯誤。
// 路由由 E2E_CRUD_ROUTES 指定（逗號分隔，例如 /gensamples,/genteamsamples）。
import { createRecorder, launchBrowser, login, newContext, screenshot } from './common.mjs';
import { addRecord, clickToolbar, deleteRecord, editRecord, openList, restoreRecord, rowTexts, watchErrors } from './crud-ui.mjs';

const routes = (process.env.E2E_CRUD_ROUTES ?? '').split(',').map((x) => x.trim()).filter(Boolean);
if (routes.length === 0) {
  throw new Error('請設定 E2E_CRUD_ROUTES（逗號分隔的路由）。');
}

const recorder = createRecorder('crud-generated-results.json');
const browser = await launchBrowser();
try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const errors = watchErrors(page);
  await login(page);

  for (const route of routes) {
    const name = `實跑${Date.now() % 100000}`;
    try {
      await openList(page, route);
      // WASM 頁面會載入 .wasm（Server 頁面不會）：確認產生的頁面真的在 WebAssembly 執行。
      const isWasm = await page.evaluate(() => performance.getEntriesByType('resource').some((x) => x.name.includes('.wasm')));
      await addRecord(page, { 名稱: name, 描述: '產生器實跑' });
      await editRecord(page, name, { 描述: '改過' });
      await deleteRecord(page, name);
      await clickToolbar(page, '顯示已刪除');
      await page.locator('.ant-table-row', { hasText: name }).first().waitFor({ timeout: 15000 });
      await restoreRecord(page, name);
      await clickToolbar(page, '返回清單');
      await page.locator('.ant-table-row', { hasText: name }).first().waitFor({ timeout: 15000 });
      const rows = await rowTexts(page);
      await screenshot(page, `crud-generated-${route.replaceAll('/', '_')}`);
      recorder.add(route, `${route} 新增、修改、刪除、還原`, isWasm && rows.some((x) => x.includes(name) && x.includes('改過')), `清單含「${name}」且描述為「改過」`);
    } catch (error) {
      await screenshot(page, `crud-generated-${route.replaceAll('/', '_')}-failed`).catch(() => {});
      recorder.add(route, `${route} 新增、修改、刪除、還原`, false, String(error).slice(0, 300));
    }
  }

  recorder.add('errors', '沒有未處理的頁面錯誤', errors.length === 0, errors.length === 0 ? '無' : errors.join('；'));
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
