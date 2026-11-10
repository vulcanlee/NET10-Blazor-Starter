// WASM 遷移 M2（0.10.1）的實機情境：個人資料頁以 WebAssembly 執行、WASM 外框（選單、頂欄、通知、公告、操作說明）、
// Server 與 WASM 頁面互相切換、強制改密碼導向、從 WASM 頁登出、換人登入不沿用上一個人的 Token、斷線不卡死，
// 最後跑全部路由的煙霧測試。用法見同資料夾的 README.md。結果寫到 E2E_OUT_DIR/m02-results.json 與 smoke-results.json。
import { callApi, createRecorder, createUser, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m02-results.json');
const smoke = createRecorder('smoke-results.json');
const browser = await launchBrowser();

/** 等 WASM 版個人資料頁顯示完成；回傳從開始導覽到顯示的毫秒數。 */
async function openProfile(page, how) {
  const started = Date.now();
  await how();
  await page.locator('.profile-page').waitFor({ timeout: 60000 });
  return Date.now() - started;
}

async function isWasmPage(page) {
  return page.evaluate(() => performance.getEntriesByType('resource').some((x) => /_framework\/dotnet\..*\.js/.test(x.name)));
}

try {
  const startedAt = new Date().toISOString().replace('T', ' ').replace('Z', '');
  const ctx = await newContext(browser);
  const page = await ctx.newPage();
  const pageErrors = [];
  page.on('pageerror', (e) => pageErrors.push(e.message));
  let wasmBytes = 0;
  page.on('response', async (response) => {
    if (/\/_framework\//.test(response.url())) {
      const length = Number((await response.headerValue('content-length')) ?? 0);
      wasmBytes += Number.isFinite(length) ? length : 0;
    }
  });

  await login(page);

  // W1：從 Server 頁面（分類清單）用右上角選單進個人資料：整頁載入成 WASM。
  await page.goto('/categories');
  await page.locator('.user-menu-trigger').waitFor({ timeout: 30000 });
  const coldMs = await openProfile(page, async () => {
    await page.locator('.user-menu-trigger').click();
    await page.getByRole('menuitem', { name: '個人資料' }).click();
  });
  const wasm = await isWasmPage(page);
  await screenshot(page, 'W1-profile-wasm');
  recorder.add('W1', '從 Server 頁面進入 WASM 版個人資料頁', new URL(page.url()).pathname === '/Profile' && wasm,
    `停在 ${new URL(page.url()).pathname}，以 WebAssembly 執行=${wasm}，第一次進入（含下載）${coldMs} ms，_framework 傳輸 ${(wasmBytes / 1024 / 1024).toFixed(1)} MB（開發模式：未修剪、未壓縮、含除錯資訊；正式發佈的大小在 M12 量測）`);

  // W1b：再次進入（瀏覽器已快取程式本體）。
  await page.goto('/categories');
  await page.locator('.user-menu-trigger').waitFor({ timeout: 30000 });
  const warmMs = await openProfile(page, () => page.goto('/Profile'));
  recorder.add('W1b', '再次進入個人資料頁（已快取）', warmMs < coldMs * 2, `${warmMs} ms`);

  // W2：外框：頂欄頁名、側邊欄、使用者名稱與管理員標籤。
  const title = (await page.locator('.top-row-title').innerText()).trim();
  const menuCount = await page.locator('.sidebar-menu .ant-menu-submenu, .sidebar-menu .ant-menu-item').count();
  const badge = await page.locator('.user-menu-badge').count();
  recorder.add('W2', 'WASM 外框：頁名、側邊選單、使用者與管理員標籤', title === '個人資料' && menuCount > 0 && badge === 1,
    `頁名「${title}」，選單項目 ${menuCount} 個，管理員標籤 ${badge}`);

  // W3：改姓名：存檔後右上角立即更新，重新整理後仍在。
  const newName = 'support 測試名';
  await page.locator('#profile-name').fill(newName);
  await page.getByRole('button', { name: /儲\s*存/ }).click();
  await page.locator('.user-menu-name', { hasText: newName }).waitFor({ timeout: 15000 });
  await screenshot(page, 'W3-name-updated');
  await page.reload();
  await page.locator('.profile-page').waitFor({ timeout: 60000 });
  const persisted = (await page.locator('.profile-name').innerText()).trim();
  recorder.add('W3', '改姓名後右上角立即更新、重新整理後仍在', persisted === newName,
    `右上角已顯示新姓名；重新整理後頁面姓名「${persisted}」`);
  await page.locator('#profile-name').fill('support');
  await page.getByRole('button', { name: /儲\s*存/ }).click();
  await page.locator('.user-menu-name', { hasText: /^support$/ }).waitFor({ timeout: 15000 });

  // W4：操作說明窗：七個章節、相關頁面。
  await page.locator('.page-help-button').click();
  await page.locator('.page-help-modal .page-help-nav-item').first().waitFor({ timeout: 15000 });
  const helpSections = await page.locator('.page-help-modal .page-help-nav-item').count();
  await screenshot(page, 'W4-help');
  await page.keyboard.press('Escape');
  recorder.add('W4', '操作說明窗', helpSections === 8, `目錄項目 ${helpSections} 個（「全部」＋七個章節）`);

  // W5：通知：伺服器端新增一則給 support 的通知，打開鈴鐺看得到，點下去標為已讀並導到連結（Server 頁面，整頁載入）。
  const support = query("SELECT Id FROM MyUser WHERE Account = 'support'")[0].Id;
  execute("INSERT INTO Notification (RecipientUserId, Category, Title, Body, Link, CreatedAtUtc) VALUES (?, '系統', 'M2 測試通知', '從 WASM 鈴鐺點開', '/categories', ?)",
    support, new Date().toISOString().replace('T', ' ').replace('Z', ''));
  await page.reload();
  await page.locator('.profile-page').waitFor({ timeout: 60000 });
  await page.locator('.notification-bell-badge').waitFor({ timeout: 15000 });
  const badgeText = (await page.locator('.notification-bell-badge').innerText()).trim();
  await page.locator('.notification-bell-trigger').click();
  await page.locator('.notification-bell-item', { hasText: 'M2 測試通知' }).waitFor({ timeout: 15000 });
  await screenshot(page, 'W5-bell');
  await page.locator('.notification-bell-item', { hasText: 'M2 測試通知' }).click();
  await page.waitForURL((url) => url.pathname === '/categories', { timeout: 30000 });
  const readAt = query("SELECT ReadAtUtc FROM Notification WHERE Title = 'M2 測試通知'")[0].ReadAtUtc;
  recorder.add('W5', 'WASM 通知鈴鐺：未讀數、清單、點開標為已讀並導到 Server 頁面', badgeText === '1' && readAt !== null,
    `未讀數「${badgeText}」；點開後已讀=${readAt !== null}，導到 /categories`);

  // W6：公告：新增一則對全體的公告，WASM 頁面顯示橫幅，按「知道了」後不再出現。
  execute("INSERT INTO Announcement (Title, Content, StartAtUtc, TargetKind, CreatedAtUtc, UpdatedAtUtc, ConcurrencyStamp) VALUES ('M2 測試公告', '公告內容', ?, 'All', ?, ?, 'stamp')",
    new Date(Date.now() - 60000).toISOString().replace('T', ' ').replace('Z', ''),
    new Date().toISOString().replace('T', ' ').replace('Z', ''),
    new Date().toISOString().replace('T', ' ').replace('Z', ''));
  await new Promise((resolve) => setTimeout(resolve, 61000)); // 伺服器端公告快取 60 秒
  await openProfile(page, () => page.goto('/Profile'));
  const banner = page.locator('.announcement-banner', { hasText: 'M2 測試公告' });
  await banner.waitFor({ timeout: 15000 });
  await screenshot(page, 'W6-announcement');
  await banner.getByRole('button', { name: '知道了' }).click();
  await page.reload();
  await page.locator('.profile-page').waitFor({ timeout: 60000 });
  await page.waitForTimeout(2000);
  const stillShown = await page.locator('.announcement-banner', { hasText: 'M2 測試公告' }).count();
  recorder.add('W6', 'WASM 公告橫幅：顯示、關閉後不再出現', stillShown === 0, `關閉後重新載入，橫幅數 ${stillShown}`);

  // W7：從 WASM 頁面的側邊欄連到 Server 頁面：整頁載入，Server 版正常。
  await page.locator('.sidebar-menu .ant-menu-submenu-title', { hasText: '資料定義' }).click();
  await page.locator('.sidebar-menu a[href="/teams"], .sidebar-menu [href="/teams"]').first().click();
  await page.waitForURL((url) => url.pathname === '/teams', { timeout: 30000 });
  await page.locator('.ant-table').first().waitFor({ timeout: 30000 });
  recorder.add('W7', '從 WASM 頁面的側邊欄連到 Server 頁面', !(await isWasmPage(page)), `停在 ${new URL(page.url()).pathname}，Server 版表格已顯示`);

  // W8：斷線不卡死：在個人資料頁中斷網路後按「儲存」，出現提示而不是卡住；恢復後可正常存檔。
  await openProfile(page, () => page.goto('/Profile'));
  await ctx.setOffline(true);
  await page.getByRole('button', { name: /儲\s*存/ }).click();
  const offlineToast = page.locator('.ant-notification-notice', { hasText: '目前無法連線到伺服器' });
  await offlineToast.waitFor({ timeout: 15000 });
  await screenshot(page, 'W8-offline');
  await ctx.setOffline(false);
  await page.locator('.ant-notification-notice-close').first().click().catch(() => {});
  await page.getByRole('button', { name: /儲\s*存/ }).click();
  await page.locator('.ant-notification-notice', { hasText: '已更新姓名' }).waitFor({ timeout: 15000 });
  recorder.add('W8', '斷線時操作出現提示、不卡死；恢復後可存檔', true, '離線時出現「目前無法連線到伺服器，請稍後再試。」；恢復連線後存檔成功');

  // W9：從 WASM 頁面登出：確認窗、撤銷刷新 Token、清掉瀏覽器存的 Token、回到登入頁。
  const tokenBefore = await page.evaluate(() => localStorage.getItem('myproject.auth') ?? sessionStorage.getItem('myproject.auth'));
  await page.locator('.user-menu-trigger').click();
  await page.locator('.user-menu-item', { hasText: '登出' }).click();
  await page.locator('.ant-modal').getByRole('button', { name: /登\s*出/ }).click();
  await page.waitForURL((url) => url.pathname.startsWith('/Auths/Login'), { timeout: 30000 });
  const tokenAfter = await page.evaluate(() => localStorage.getItem('myproject.auth') ?? sessionStorage.getItem('myproject.auth'));
  const revoked = query("SELECT COUNT(*) AS n FROM RefreshSession WHERE RevokedReason = 'WebLogout'")[0].n;
  const logoutAudit = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Logout' AND OccurredAt >= ?", startedAt)[0].n;
  // 登出只寫一筆稽核（網頁登出頁寫），Token 由網頁登出頁依登入工作階段撤銷（RevokedReason = WebLogout）。
  recorder.add('W9', '從 WASM 頁面登出', tokenBefore !== null && tokenAfter === null && revoked >= 1 && logoutAudit === 1,
    `登出前瀏覽器有 Token=${tokenBefore !== null}，登出後=${tokenAfter !== null}；撤銷紀錄 ${revoked} 筆；稽核 Logout ${logoutAudit} 筆`);

  // W10：換人登入：另一個帳號登入後打開個人資料，看到的是他自己（不會沿用上一個人的 Token）。
  createUser('e2e-user', 'E2eUser-pass1');
  await login(page, { account: 'e2e-user', password: 'E2eUser-pass1' });
  await openProfile(page, () => page.goto('/Profile'));
  const account = (await page.locator('.profile-muted').first().innerText()).trim();
  recorder.add('W10', '換人登入後個人資料是新登入的人', account === 'e2e-user', `個人資料顯示帳號「${account}」`);

  // W11：被要求改密碼的帳號打開個人資料：導到「變更密碼」頁（Server 頁面）。
  execute("UPDATE MyUser SET MustChangePassword = 1 WHERE Account = 'e2e-user'");
  await page.goto('/Profile');
  await page.waitForURL((url) => url.pathname === '/ChangePassword', { timeout: 60000 });
  execute("UPDATE MyUser SET MustChangePassword = 0 WHERE Account = 'e2e-user'");
  recorder.add('W11', '強制改密碼時從個人資料頁導到變更密碼頁', true, `停在 ${new URL(page.url()).pathname}`);

  // W13：跨模式換頁的開頁稽核只記一筆：Server→WASM 由 WASM 頁面記、WASM→Server 由 Server 電路記，離開的那一邊不記。
  // Start-E2EHost.ps1 已開啟瀏覽類稽核（AuditLogSettings__RecordBrowsing=true）。
  const marker = query("SELECT COALESCE(MAX(Id), 0) AS n FROM AuditLog")[0].n;
  await page.goto('/App');
  await page.locator('.user-menu-trigger').waitFor({ timeout: 30000 });
  await openProfile(page, async () => {
    await page.locator('.user-menu-trigger').click();
    await page.getByRole('menuitem', { name: '個人資料' }).click();
  });
  await page.locator('.user-menu-trigger').click();
  await page.locator('.user-menu-item', { hasText: '變更密碼' }).click();
  await page.waitForURL((url) => url.pathname === '/ChangePassword', { timeout: 60000 });
  await page.waitForTimeout(2000);
  // 從這次 /App 那一筆之後開始數（前一個情境的開頁稽核是非同步寫入，可能晚於 marker 才落庫）。
  const appRow = query("SELECT MIN(Id) AS n FROM AuditLog WHERE Action = 'Page.View' AND TargetId = '/App' AND Id > ?", marker)[0].n ?? marker;
  const views = (target) => query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Page.View' AND Id > ? AND TargetId = ?", appRow, target)[0].n;
  const profileViews = views('/Profile');
  const changePasswordViews = views('/ChangePassword');
  recorder.add('W13', '跨模式換頁的開頁稽核各只記一筆', profileViews === 1 && changePasswordViews === 1,
    `/App →（整頁載入）/Profile →（整頁載入）/ChangePassword：/Profile ${profileViews} 筆、/ChangePassword ${changePasswordViews} 筆`);

  recorder.add('W12', 'WASM 頁面沒有未處理的頁面錯誤', pageErrors.length === 0, pageErrors.length === 0 ? '無' : pageErrors.join('；'));
  await ctx.close();

  // 全部路由煙霧測試（/Profile 已是 WASM）。
  await runSmoke(browser, smoke);
} finally {
  await browser.close();
}

const ok = recorder.save() & smoke.save();
process.exit(ok ? 0 : 1);
