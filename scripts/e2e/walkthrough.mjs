// 最終巡檢（0.10.11 起，WASM 遷移完成後）：以看得到的 Chrome（E2E_HEADED=1）把每個畫面實際開一次並截圖，供人逐張目視檢查。
//   V01～：登入後 28 個路由（0.10.22 起少了 Agent 測試台與 Agent 工作流程）（每頁等外框與內容出現），記錄頁面錯誤、主控台錯誤、CSP 違規（AntDesign 頁籤的 ondragover 為已知、另計）、錯誤列。
//   D01～：主要對話窗與浮層：各 CRUD 清單的「新增」窗、操作說明窗、使用者選單、通知鈴鐺、關於。
//   T01：工具列按鈕的提示框會不會蓋住對話窗的關閉鈕（M6 實機腳本遇到）：例外 AI 分析完成後按「產生 PDF 報告並下載」，
//        再以分段移動的滑鼠（較接近真人）移到關閉鈕並一般點擊，對話窗要關得掉。會呼叫一次真實模型。
//   A01～：登入相關頁面（未登入的瀏覽器）；最後登出。
// 結果寫到 E2E_OUT_DIR/walkthrough-results.json，截圖在 screenshots/walk-*.png。
import { createRecorder, execute, launchBrowser, login, newContext, screenshot } from './common.mjs';
import { authenticatedRoutes, anonymousRoutes } from './smoke-routes.mjs';
import { seedProjects } from './m04-views.mjs';
import { seedAiData, seedTag } from './m06-views.mjs';
import { seedOpsData } from './m07-views.mjs';
import { clickToolbar } from './crud-ui.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
seedProjects();
seedAiData();
seedOpsData();

const recorder = createRecorder('walkthrough-results.json');
const browser = await launchBrowser();
const name = (route) => route.replace(/[^A-Za-z0-9]+/g, '_').replace(/^_|_$/g, '') || 'root';

/** 監聽一個分頁的問題：頁面錯誤、主控台錯誤、CSP 違規（已知的 AntDesign 頁籤另計）。 */
async function watch(context, page) {
  const problems = [];
  let knownTabs = 0;
  page.on('pageerror', (e) => problems.push(`頁面錯誤：${e.message.slice(0, 160)}`));
  page.on('console', (m) => {
    // CSP 違規由下面的 securitypolicyviolation 事件逐一判斷（含已知的 AntDesign 頁籤），主控台的同一則訊息不重複計算。
    if (m.type() === 'error' && !/Failed to load resource: the server responded with a status of 40[134]|violates the following Content Security Policy/.test(m.text())) {
      problems.push(`主控台：${m.text().slice(0, 160)}`);
    }
  });
  await context.exposeBinding('__walkCsp', (_, v) => {
    if (v.directive === 'script-src-attr' && /class="ant-tabs-tab[^"]*"[^>]*ondragover="event\.preventDefault\(\);"/.test(v.element)) {
      knownTabs++;
      return;
    }
    problems.push(`CSP：${v.directive} ${v.blocked} ${v.element.slice(0, 100)}`);
  });
  await context.addInitScript(() => document.addEventListener('securitypolicyviolation', (e) => {
    const t = e.target;
    window.__walkCsp({ directive: e.violatedDirective, blocked: e.blockedURI, element: t && t.outerHTML ? t.outerHTML.slice(0, 300) : String(t) });
  }));
  return { problems, take: () => problems.splice(0), knownTabs: () => knownTabs };
}

async function errorBanner(page) {
  return (await page.locator('#blazor-error-ui:visible, .alert-danger:visible, .session-throttled:visible').count()) > 0;
}

try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const w = await watch(context, page);
  await login(page);

  // V：登入後的每個路由。
  let index = 1;
  for (const route of authenticatedRoutes) {
    const id = `V${String(index++).padStart(2, '0')}`;
    await page.goto(route);
    await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 }).catch(() => {});
    await page.waitForTimeout(2500);
    const file = await screenshot(page, `walk-${id}-${name(route)}`);
    const problems = w.take();
    const banner = route !== '/Error' && await errorBanner(page);
    const path = new URL(page.url()).pathname;
    recorder.add(id, `開啟 ${route}`, problems.length === 0 && !banner && path.toLowerCase() !== '/auths/login',
      `停在 ${path}${banner ? '，出現錯誤列' : ''}${problems.length ? `；${problems.join('；')}` : ''}（${file.split(/[\\/]/).pop()}）`);
  }

  // D：主要對話窗與浮層。
  let d = 1;
  const dialog = async (title, open, close) => {
    const id = `D${String(d++).padStart(2, '0')}`;
    try {
      await open();
      await page.waitForTimeout(1200);
      const file = await screenshot(page, `walk-${id}-${title}`);
      await close();
      await page.waitForTimeout(600);
      const problems = w.take();
      recorder.add(id, title, problems.length === 0, problems.length ? problems.join('；') : `（${file.split(/[\\/]/).pop()}）`);
    } catch (error) {
      recorder.add(id, title, false, String(error.message).split('\n')[0]);
      await page.keyboard.press('Escape').catch(() => {});
    }
  };
  for (const [route, label] of [['/categories', '分類新增窗'], ['/teams', '團隊新增窗'], ['/projects', '專案新增窗'], ['/myusers', '使用者新增窗'], ['/roleviews', '角色新增窗'], ['/announcements', '公告新增窗']]) {
    await dialog(label, async () => {
      await page.goto(route);
      await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
      await page.waitForTimeout(1500);
      await clickToolbar(page, '新增');
      await page.locator('.ant-modal-content:visible').first().waitFor({ timeout: 15000 });
    }, async () => {
      await page.keyboard.press('Escape');
      await page.locator('.ant-modal-confirm-btns button', { hasText: /放棄|確定|離開/ }).first().click({ timeout: 2000 }).catch(() => {});
    });
  }

  await dialog('操作說明窗', async () => {
    await page.goto('/projects');
    await page.locator('.page-help-button').waitFor({ timeout: 60000 });
    await page.locator('.page-help-button').click();
    await page.locator('.page-help-modal .page-help-nav-item').first().waitFor({ timeout: 15000 });
  }, () => page.keyboard.press('Escape'));
  await dialog('使用者選單', () => page.locator('.user-menu-trigger').click(), () => page.locator('.user-menu-trigger').click());
  execute("INSERT INTO Notification (RecipientUserId, Category, Title, Body, Link, CreatedAtUtc) SELECT Id, '系統', '巡檢通知', '最終巡檢用的通知', '/projects', datetime('now') FROM MyUser WHERE Account = 'support'");
  await dialog('通知鈴鐺', async () => {
    await page.reload();
    await page.locator('.notification-bell-badge').waitFor({ timeout: 70000 });
    await page.locator('.notification-bell-trigger').click();
    await page.locator('.notification-bell-item', { hasText: '巡檢通知' }).waitFor({ timeout: 15000 });
  }, () => page.locator('.notification-bell-trigger').click());
  await dialog('關於', async () => {
    await page.locator('.user-menu-trigger').click();
    await page.locator('.user-menu-item', { hasText: '關於' }).click();
    await page.locator('.ant-modal-content:visible').first().waitFor({ timeout: 15000 });
  }, () => page.keyboard.press('Escape'));

  // T01：提示框會不會蓋住對話窗的關閉鈕（真實模型一次）。
  try {
    await page.goto('/system-exceptions');
    await page.waitForSelector('.exception-log-table', { timeout: 60000 });
    await page.fill('.exception-log-search-input', seedTag);
    await clickToolbar(page, '查詢');
    await page.locator('.exception-log-table .ant-table-row', { hasText: '資料庫被鎖定' }).first().locator('button', { hasText: '查看' }).click();
    await page.waitForSelector('.exception-log-modal .ant-modal-content', { state: 'visible', timeout: 15000 });
    await page.locator('.exception-log-detail-actions button:not([disabled])').first().click({ timeout: 15000 });
    const modal = page.locator('.exception-ai-modal');
    await modal.locator('.ant-modal-content').waitFor({ state: 'visible', timeout: 15000 });
    await modal.locator('.exception-ai-entry-label', { hasText: 'AI 分析報告' }).first().waitFor({ timeout: 240000 });
    await modal.locator('button', { hasText: '停止產生' }).waitFor({ state: 'detached', timeout: 240000 }).catch(() => {});
    const pdf = modal.locator('button.toolbar-icon-button', { hasText: '產生 PDF 報告並下載' });
    await Promise.all([page.waitForEvent('download', { timeout: 120000 }), pdf.click()]);
    await page.waitForTimeout(800);
    const tooltipAfterClick = await page.locator('.ant-tooltip:not(.ant-tooltip-hidden)').isVisible().catch(() => false);
    const close = modal.locator('.ant-modal-close');
    const box = await close.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2, { steps: 25 });
    await page.waitForTimeout(500);
    const tooltipAtClose = await page.locator('.ant-tooltip:not(.ant-tooltip-hidden)').isVisible().catch(() => false);
    await screenshot(page, 'walk-T01-滑鼠移到關閉鈕');
    await page.mouse.down();
    await page.mouse.up();
    const closed = await modal.locator('.ant-modal-content').waitFor({ state: 'hidden', timeout: 5000 }).then(() => true).catch(() => false);
    recorder.add('T01', '按過 PDF 鈕後，滑鼠移到關閉鈕一般點擊能關掉對話窗', closed,
      `按下後提示框顯示=${tooltipAfterClick}；滑鼠移到關閉鈕時提示框仍顯示=${tooltipAtClose}；點擊後對話窗關閉=${closed}`);
    await page.keyboard.press('Escape').catch(() => {});
  } catch (error) {
    recorder.add('T01', '按過 PDF 鈕後，滑鼠移到關閉鈕一般點擊能關掉對話窗', false, String(error.message).split('\n')[0]);
  }

  // A：未登入的瀏覽器開登入相關頁面。
  const anonymous = await newContext(browser);
  const anon = await anonymous.newPage();
  const wa = await watch(anonymous, anon);
  let a = 1;
  for (const route of anonymousRoutes) {
    const id = `A${String(a++).padStart(2, '0')}`;
    await anon.goto(route);
    await anon.locator('#account, .login-card, .pending-card').first().waitFor({ timeout: 60000 }).catch(() => {});
    await anon.waitForTimeout(2000);
    const file = await screenshot(anon, `walk-${id}-${name(route)}`);
    const problems = wa.take();
    recorder.add(id, `開啟 ${route}（未登入）`, problems.length === 0 && !(await errorBanner(anon)),
      `停在 ${new URL(anon.url()).pathname}${problems.length ? `；${problems.join('；')}` : ''}（${file.split(/[\\/]/).pop()}）`);
  }

  await anonymous.close();

  // 登出。
  await page.goto('/Auths/Logout');
  await page.waitForURL((url) => url.pathname.startsWith('/Auths/Login'), { timeout: 30000 }).catch(() => {});
  recorder.add('A99', '登出後回到登入頁', new URL(page.url()).pathname.startsWith('/Auths/Login'), `停在 ${new URL(page.url()).pathname}；已知 AntDesign 頁籤 CSP 訊息 ${w.knownTabs()} 則（不影響功能）`);
  await context.close();
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
