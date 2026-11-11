// 全部 37 個路由的煙霧測試（0.10.0 起，WASM 遷移 M1）：每頁都要開得起來、沒有錯誤畫面，並截圖。
// 以 support（管理員）登入跑需要登入的頁面，另開一個未登入的視窗跑登入相關頁面。
import { basename } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createRecorder, launchBrowser, login, newContext, screenshot } from './common.mjs';

export const authenticatedRoutes = [
  '/', '/App', '/Profile', '/ChangePassword', '/TwoFactorSetup',
  '/projects', '/categories', '/teams', '/myusers', '/roleviews', '/audit-logs', '/email-logs',
  '/logs', '/log-level-setting', '/system-exceptions', '/ai-call-logs', '/token-usage', '/prompt-templates',
  '/system-health', '/database-usage', '/scheduled-jobs', '/backups', '/data-retention', '/system-parameters', '/announcements',
  '/agent-playground', '/agent-workflows', '/admin-assistant', '/Error', '/not-found',
];

export const anonymousRoutes = ['/Auths/Login', '/Auths/ForgotPassword', '/Auths/ResetPassword', '/Auths/Pending', '/Auths/TwoFactor', '/Auths/RefreshSession'];

// 登出頁最後跑（會登出）。
export const logoutRoute = '/Auths/Logout';

// 以 WebAssembly 執行的頁面（0.10.1 起）：等到這個元素出現才算載入完成，再截圖。
const readySelectors = { '/Profile': '.profile-page' };

async function visit(page, route, recorder, index, { signedIn = false } = {}) {
  const errors = [];
  const onError = (error) => errors.push(error.message);
  page.on('pageerror', onError);
  try {
    const response = await page.goto(route, { waitUntil: 'load', timeout: 45000 });
    if (readySelectors[route]) {
      await page.locator(readySelectors[route]).waitFor({ timeout: 60000 }).catch(() => {});
    }
    await page.waitForTimeout(2500);
    const status = response?.status() ?? 0;
    const blazorError = await page.locator('#blazor-error-ui').isVisible().catch(() => false);
    const denied = route !== '/Error' && (await page.getByText('你沒有權限存取此頁面').count()) > 0;
    const finalPath = new URL(page.url()).pathname;
    // 0.10.5 起：登入後的頁面不可以被丟回登入頁（M6 實機發現連續快速換頁時被限流、WASM 誤判成登入失效），也不可以停在限流提示。
    const kickedOut = signedIn && finalPath.toLowerCase() === '/auths/login';
    const throttled = (await page.locator('.session-throttled').count()) > 0;
    const file = await screenshot(page, `smoke-${String(index).padStart(2, '0')}-${route.replace(/[^A-Za-z0-9]+/g, '_') || 'root'}`);
    const passed = status > 0 && status < 500 && !blazorError && !denied && !kickedOut && !throttled && errors.length === 0;
    recorder.add(`R${String(index).padStart(2, '0')}`, `開啟 ${route}`, passed,
      `HTTP ${status}，最後停在 ${finalPath}${blazorError ? '，出現錯誤列' : ''}${denied ? '，顯示無權限' : ''}${kickedOut ? '，被丟回登入頁' : ''}${throttled ? '，顯示限流提示' : ''}${errors.length ? `，頁面錯誤：${errors.join('；')}` : ''}（${basename(file)}）`);
  } finally {
    page.off('pageerror', onError);
  }
}

export async function runSmoke(browser, recorder) {
  let index = 1;
  const signedIn = await newContext(browser);
  const page = await signedIn.newPage();
  await login(page);
  for (const route of authenticatedRoutes) {
    await visit(page, route, recorder, index++, { signedIn: true });
  }

  const anonymous = await newContext(browser);
  const anonymousPage = await anonymous.newPage();
  for (const route of anonymousRoutes) {
    await visit(anonymousPage, route, recorder, index++);
  }

  await visit(page, logoutRoute, recorder, index++);
  await signedIn.close();
  await anonymous.close();
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  const browser = await launchBrowser();
  const recorder = createRecorder('smoke-results.json');
  try {
    await runSmoke(browser, recorder);
  } finally {
    await browser.close();
  }
  process.exit(recorder.save() ? 0 : 1);
}
