// M8 外觀比對用的固定畫面（0.10.7 起，WASM 遷移 M8）：系統管理助理（含巡檢報告窗）的初始畫面（Agent 測試台與 Agent 工作流程 0.10.22 起已移除，檔名編號沿用 03、04）。
// 改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。對話與執行紀錄的畫面含 AI 輸出，不放進逐像素比對（改由 m08-agents.mjs 的情境驗證）。
import { launchBrowser, login, newContext, screenshot } from './common.mjs';
import { freezeTimes } from './m05-views.mjs';

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(1200);
}

export async function captureViews(browser, prefix) {
  const files = [];
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  freezeTimes();
  try {
    await page.goto('/admin-assistant');
    await page.waitForSelector('.agent-playground-layout', { timeout: 60000 });
    await settle(page);
    files.push(await screenshot(page, `${prefix}-03-系統管理助理`));

    await page.locator('button.toolbar-icon-button', { hasText: '巡檢報告' }).click();
    await page.waitForSelector('.admin-patrol-report-modal .ant-modal-content', { state: 'visible', timeout: 15000 });
    await settle(page);
    files.push(await screenshot(page, `${prefix}-04-巡檢報告窗`));
  } catch (error) {
    await screenshot(page, `${prefix}-失敗當下`);
    throw error;
  }

  await context.close();
  return files;
}

// 直接執行：node m08-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  const browser = await launchBrowser();
  try {
    console.log((await captureViews(browser, process.argv[2] ?? 'view')).join('\n'));
  } finally {
    await browser.close();
  }
}
