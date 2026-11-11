// WASM 遷移 M8（0.10.7）的實機情境（真實模型）：Agent 測試台、Agent 工作流程、系統管理助理在 WebAssembly 執行，對話每個請求接續一次；通知推播串流。
// 寫入工具核准與拒絕、核准中重新整理可恢復、兩分頁同時送出被排隊、工作流程人工介入（中途重新整理接回）、系統管理助理不可復原動作要輸入「確認」、
// 巡檢報告連結、推播 2 秒內到達，最後跑全部路由的煙霧測試。參數 nopush：只跑「關閉推播仍可輪詢」（主機要以 NotificationSettings__PushEnabled=false 啟動）。
// 結果寫到 E2E_OUT_DIR/m08-results.json（nopush 模式為 m08-nopush-results.json）與 smoke-results.json。
import { apiAsPageUser, apiJsonAsPageUser, createRecorder, isWasmPage, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { clickToolbar, closeToasts, watchErrors } from './crud-ui.mjs';
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

function utcNow() {
  return new Date().toISOString().replace('T', ' ').slice(0, 19);
}

function audits(action, since) {
  return query('SELECT TargetId, Detail FROM AuditLog WHERE Action = ? AND OccurredAt >= ? ORDER BY Id', action, since);
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
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].some((b) => b.offsetParent !== null && b.textContent.includes('停止產生')),
    null, { timeout });
}

/**
 * 送出會用到寫入工具的指示，等到核准窗出現。真實模型常會先回問「請確認是否要修改？」而不呼叫工具：
 * 回問時就回答「確認」，最多三輪。
 */
async function sendUntilApproval(page, root, modal, text) {
  await send(page, root, text);
  for (let round = 0; round < 3; round++) {
    const appeared = await Promise.race([
      page.waitForSelector(`${modal} .ant-modal-content`, { state: 'visible', timeout: 600000 }).then(() => true),
      waitIdle(page).then(() => false),
    ]);
    if (appeared || await page.locator(`${modal} .ant-modal-content`).isVisible()) {
      return round;
    }

    await send(page, root, '確認，請直接呼叫工具修改。');
  }

  throw new Error('三輪之後模型仍沒有呼叫寫入工具');
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
  const since = utcNow();
  let sample = null;

  await step('M8-G1', 'Agent 測試台在 WASM、產生測試專案', async () => {
    await page.goto('/agent-playground');
    await page.waitForSelector('.agent-playground-layout', { timeout: 60000 });
    check(await isWasmPage(page), 'Agent 測試台不是 WASM');
    await clickToolbar(page, '產生 10 筆測試專案');
    await page.waitForSelector('.ant-modal-content:visible', { timeout: 30000 });
    await page.locator('.ant-modal-content:visible .ant-modal-close, .ant-modal-content:visible button:has-text("確定"), .ant-modal-content:visible button:has-text("關閉")').first().click();
    sample = query("SELECT Title, Description FROM Project WHERE Title LIKE '【測試】%' AND IsDeleted = 0 ORDER BY Id LIMIT 1")[0];
    check(sample, '沒有產生測試專案');
    return sample.Title;
  });

  await step('M8-G2', '寫入工具：跳出核准窗 → 核准 → 真的寫入、稽核一筆', async () => {
    const asked = await sendUntilApproval(page, '.agent-playground', '.agent-playground-approval-modal',
      `請把專案「${sample.Title}」的描述改成「E2E 核准測試」。直接呼叫工具修改，不要先問我。`);
    await screenshot(page, 'm08-g2-核准窗');
    await page.locator('.agent-playground-approval-modal button', { hasText: /^\s*核\s*准\s*$/ }).click();
    await waitIdle(page);
    await waitFor(() => query('SELECT Description FROM Project WHERE Title = ?', sample.Title)[0].Description === 'E2E 核准測試', 30000, '描述沒有被改');
    check(audits('Ai.ToolInvoked', since).some((x) => x.TargetId === 'update_project_description'), '沒有工具執行稽核');
    check(page.url().includes('conversation='), `網址沒有帶對話：${page.url()}`);
    return `描述已改成「E2E 核准測試」（模型回問 ${asked} 次）`;
  });

  await step('M8-G3', '核准中重新整理：回到同一段對話、核准窗再跳出 → 拒絕 → 沒有寫入', async () => {
    await sendUntilApproval(page, '.agent-playground', '.agent-playground-approval-modal',
      `再把專案「${sample.Title}」的描述改成「E2E 重新整理」。直接呼叫工具，不要先問我。`);
    await page.reload();
    await page.waitForSelector('.agent-playground-approval-modal .ant-modal-content', { state: 'visible', timeout: 60000 });
    await page.locator('.agent-playground-approval-modal button', { hasText: /^\s*拒\s*絕\s*$/ }).click();
    await waitIdle(page);
    await page.waitForTimeout(1500);
    const description = query('SELECT Description FROM Project WHERE Title = ?', sample.Title)[0].Description;
    check(description === 'E2E 核准測試', `被寫入了：${description}`);
    check(audits('Ai.ToolRejected', since).length >= 1, '沒有拒絕稽核');
    return '重新整理後核准窗仍在；拒絕後描述不變';
  });

  await step('M8-G4', '兩個分頁同時送出同一段對話：後到的排隊，兩則都完成', async () => {
    const url = page.url();
    const second = await context.newPage();
    await second.goto(url);
    await second.waitForSelector('.agent-playground textarea', { timeout: 60000 });
    await page.waitForTimeout(1500);
    await Promise.all([
      send(page, '.agent-playground', '請只回覆「甲」這一個字。'),
      send(second, '.agent-playground', '請只回覆「乙」這一個字。'),
    ]);
    await Promise.all([waitIdle(page), waitIdle(second)]);
    const id = new URL(url).searchParams.get('conversation');
    const detail = (await apiJsonAsPageUser(page, `/api/v1/agent-playground/conversations/${id}`)).json;
    const texts = JSON.stringify(detail.data);
    await second.close();
    check(texts.includes('請只回覆「甲」') && texts.includes('請只回覆「乙」'), '兩則訊息沒有都存進對話');
    return '兩則都在同一段對話裡，沒有互相覆蓋';
  });

  await step('M8-G5', '工作流程：專案狀態報告，人工介入中重新整理可接回，直到完成', async () => {
    await closeToasts(page);
    await page.goto('/agent-workflows');
    await page.waitForSelector('.agent-workflow-layout', { timeout: 60000 });
    check(await isWasmPage(page), 'Agent 工作流程不是 WASM');
    const startDescription = query('SELECT Description FROM Project WHERE Title = ?', sample.Title)[0].Description;
    await page.locator('.agent-workflow-picker button', { hasText: '專案狀態報告' }).click();
    const box = page.locator('.agent-workflow-composer textarea');
    await box.fill(sample.Title);
    await box.press('Tab');
    await page.locator('.agent-workflow-composer button', { hasText: '開始執行' }).click();
    let reloaded = false;
    const handled = [];
    const deadline = Date.now() + 900000;
    while (Date.now() < deadline) {
      const description = query('SELECT Description FROM Project WHERE Title = ?', sample.Title)[0].Description;
      if (description !== startDescription) {
        break;
      }

      const request = page.locator('.agent-workflow-request');
      if (await request.count() > 0 && await request.first().isVisible()) {
        if (!reloaded) {
          reloaded = true;
          await page.reload();
          await page.waitForSelector('.agent-workflow-request', { timeout: 60000 });
          handled.push('重新整理後接回');
          continue;
        }

        const text = await request.first().innerText();
        if (text.includes('送出回答')) {
          await request.locator('textarea').fill('請直接用現有的專案資料撰寫即可。');
          await request.locator('textarea').press('Tab');
          await request.locator('button', { hasText: '送出回答' }).click();
          handled.push('回答提問');
        } else if (text.includes('通過')) {
          await request.locator('button', { hasText: /^\s*通\s*過\s*$/ }).click();
          handled.push('審閱通過');
        } else {
          await request.locator('button', { hasText: /^\s*核\s*准\s*$/ }).click();
          handled.push('核准收尾工具');
        }

        await page.waitForTimeout(3000);
        continue;
      }

      await page.waitForTimeout(2000);
    }

    const description = query('SELECT Description FROM Project WHERE Title = ?', sample.Title)[0].Description;
    check(description !== startDescription, `沒有完成（處理過：${handled.join('、')}）`);
    check(reloaded, '沒有遇到人工介入');
    await screenshot(page, 'm08-g5-工作流程完成');
    return handled.join('、');
  });

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
