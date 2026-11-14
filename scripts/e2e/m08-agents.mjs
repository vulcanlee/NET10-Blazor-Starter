// WASM 遷移 M8（0.10.7）的實機情境（真實模型）：系統管理助理在 WebAssembly 執行，對話每個請求接續一次；通知推播串流。
// 0.10.22 起 Agent 測試台與 Agent 工作流程移除，原本的 M8-G1～G5 一併拿掉（共用的對話流程由 AgentConversationApiTests 守門）。
// 系統管理助理不可復原動作要輸入「確認」、
// 巡檢報告連結、推播 2 秒內到達，最後跑全部路由的煙霧測試。參數 nopush：只跑「關閉推播仍可輪詢」（主機要以 NotificationSettings__PushEnabled=false 啟動）。
// 結果寫到 E2E_OUT_DIR/m08-results.json（nopush 模式為 m08-nopush-results.json）與 smoke-results.json。
import { apiAsPageUser, createRecorder, isWasmPage, launchBrowser, login, newContext, query, screenshot, waitForPredicate } from './common.mjs';
import { closeToasts, watchErrors } from './crud-ui.mjs';
import { freezeTimes } from './m05-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const noPush = process.argv[2] === 'nopush';
const recorder = createRecorder(noPush ? 'm08-nopush-results.json' : 'm08-results.json');
const smoke = createRecorder('smoke-results.json');

async function step(id, title, action) {
  try {
    recorder.add(id, title, true, (await action()) ?? '通過');
  } catch (error) {
    recorder.add(id, title, false, String(error?.message ?? error).split('\n')[0]);
  }
}

function check(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

async function waitFor(predicate, timeout, message) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await predicate()) {
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  throw new Error(message);
}

/** 在輸入框打字並送出（TextArea 失焦才回寫值）。 */
async function send(page, root, text) {
  const box = page.locator(`${root} textarea`).last();
  await box.fill(text);
  await box.press('Tab');
  await page.locator(`${root} button`, { hasText: /^\s*送\s*出\s*$/ }).last().click();
}

/** 等到不在產生中（沒有「停止產生」）。 */
async function waitIdle(page, timeout = 600000) {
  await page.waitForTimeout(1000);
  await waitForPredicate(page, () => ![...document.querySelectorAll('button')].some((b) => b.offsetParent !== null && b.textContent.includes('停止產生')),
    null, { timeout });
}

async function bellCount(page) {
  const badge = page.locator('.notification-bell-badge');
  return (await badge.count()) === 0 ? 0 : Number((await badge.innerText()).replace(/\D/g, '') || 0);
}

async function runAgents(browser) {
  const context = await newContext(browser);
  const page = await context.newPage();
  const errors = watchErrors(page);
  await login(page);
  freezeTimes();

  await step('M8-G6', '系統管理助理：不可復原的動作要輸入「確認」才能核准（這次按拒絕）', async () => {
    await closeToasts(page);
    await page.goto('/admin-assistant');
    await page.waitForSelector('.agent-playground-layout', { timeout: 60000 });
    check(await isWasmPage(page), '系統管理助理不是 WASM');
    await send(page, '.agent-playground', '請清空所有系統例外紀錄。直接呼叫工具，不要先問我。');
    const modal = page.locator('.ant-modal-content:visible', { hasText: '系統管理助理要執行會修改資料的操作' });
    await modal.waitFor({ timeout: 600000 });
    const approve = modal.locator('button', { hasText: /^\s*核\s*准\s*$/ });
    check(await approve.isDisabled(), '沒輸入「確認」就能按核准');
    await modal.locator('input').last().fill('確認');
    await page.waitForTimeout(500);
    check(!(await approve.isDisabled()), '輸入「確認」後仍不能按核准');
    await screenshot(page, 'm08-g6-不可復原確認');
    await modal.locator('button', { hasText: /^\s*拒\s*絕\s*$/ }).click();
    await waitIdle(page);
    return '未輸入時核准停用，輸入後可按；按拒絕';
  });

  await step('M8-G7', '巡檢報告：排程巡檢產生報告，網址 ?patrolReport= 直接打開', async () => {
    await closeToasts(page);
    const before = query('SELECT COUNT(*) AS N FROM AdminPatrolReport')[0].N;
    const status = await apiAsPageUser(page, '/api/v1/scheduled-jobs/AdminPatrol/trigger', { method: 'POST' });
    check(status === 200, `觸發巡檢：${status}`);
    await waitFor(() => query('SELECT COUNT(*) AS N FROM AdminPatrolReport')[0].N > before, 600000, '巡檢沒有產生報告');
    const id = query('SELECT ReportId FROM AdminPatrolReport ORDER BY Id DESC LIMIT 1')[0].ReportId;
    await page.goto(`/admin-assistant?patrolReport=${id}`);
    await page.waitForSelector('.admin-patrol-report-modal .ant-modal-content', { state: 'visible', timeout: 60000 });
    await page.waitForTimeout(1500);
    await screenshot(page, 'm08-g7-巡檢報告');
    return `報告 ${id}`;
  });

  await step('M8-G8', '推播：另一個瀏覽器的鈴鐺在 2 秒內更新', async () => {
    const other = await newContext(browser);
    const watcher = await other.newPage();
    await login(watcher);
    await watcher.goto('/Profile');
    await watcher.waitForSelector('.notification-bell', { timeout: 60000 });
    await watcher.waitForTimeout(4000);
    const before = await bellCount(watcher);
    const started = Date.now();
    const status = await apiAsPageUser(page, '/api/v1/system-health/test-notification', { method: 'POST' });
    check(status === 200, `送測試通知：${status}`);
    await waitFor(async () => (await bellCount(watcher)) > before, 10000, '10 秒內沒有更新');
    const elapsed = Date.now() - started;
    await other.close();
    check(elapsed <= 2000, `花了 ${elapsed} ms`);
    return `${elapsed} ms（${before} → ${before + 1}）`;
  });

  recorder.add('M8-G9', '情境期間沒有未處理的瀏覽器錯誤', errors.length === 0, errors.length === 0 ? '0 個' : errors.slice(0, 3).join(' | '));
  await context.close();
  await runSmoke(browser, smoke);
}

async function runNoPush(browser) {
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);
  await step('M8-N1', '關閉推播：鈴鐺仍會在輪詢時更新（60 秒內）', async () => {
    const other = await newContext(browser);
    const watcher = await other.newPage();
    await login(watcher);
    await watcher.goto('/Profile');
    await watcher.waitForSelector('.notification-bell', { timeout: 60000 });
    await watcher.waitForTimeout(3000);
    const before = await bellCount(watcher);
    const started = Date.now();
    check(await apiAsPageUser(page, '/api/v1/system-health/test-notification', { method: 'POST' }) === 200, '送測試通知失敗');
    await waitFor(async () => (await bellCount(watcher)) > before, 75000, '75 秒內沒有更新');
    const elapsed = Date.now() - started;
    await other.close();
    return `${Math.round(elapsed / 1000)} 秒後由輪詢更新`;
  });
  await context.close();
}

const browser = await launchBrowser();
try {
  if (noPush) {
    await runNoPush(browser);
  } else {
    await runAgents(browser);
  }
} finally {
  await browser.close();
}

const okSmoke = noPush ? true : smoke.save();
process.exit(recorder.save() && okSmoke ? 0 : 1);
