// M9 外觀比對用的固定畫面（0.10.8 起，WASM 遷移 M9）：登入、登入失敗、忘記密碼、重設密碼（無效連結）、Google 待審、兩步驟驗證、
// 強制改密碼、兩步驟驗證設定、找不到頁面、錯誤頁。改版前（Server 版）與改版後（WASM 版）各跑一次，再逐像素比對。
// ⚠️ 驗證碼數字與兩步驟驗證設定的 QR 碼每次不同，這兩處的差異比對時另外說明。
// ⚠️ 不可用 waitForLoadState('networkidle')：M8 起 WASM 頁面一直開著通知推播連線，網路永遠不會閒置（會等到逾時）。一律等畫面元素出現。
import { createUser, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';

export const userPassword = 'User!E2e2026x';
export const mustChangeAccount = 'e2e-m9-change';
export const twoFactorAccount = 'e2e-m9-2fa';
export const setupAccount = 'e2e-m9-setup';

export function seedAuthUsers() {
  for (const account of [mustChangeAccount, twoFactorAccount, setupAccount]) {
    if (query('SELECT Id FROM MyUser WHERE Account = ?', account).length === 0) {
      createUser(account, userPassword);
    }
  }

  execute('UPDATE MyUser SET MustChangePassword = 1 WHERE Account = ?', mustChangeAccount);
  // 金鑰是加密存的、測試無法產生；截圖只需要「要第二步驗證」這個狀態，放一個非空的佔位值（不會通過驗證）。
  execute("UPDATE MyUser SET TwoFactorEnabled = 1, TwoFactorSecret = 'e2e-placeholder' WHERE Account = ?", twoFactorAccount);
}

async function settle(page) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(1200);
}

async function shot(page, name, files) {
  await settle(page);
  files.push(await screenshot(page, name));
}

/** 填登入表單（不等導頁），驗證碼答案在隱藏欄位。 */
async function fillLogin(page, account, password) {
  await page.goto('/Auths/Login');
  await page.waitForSelector('#account', { timeout: 60000 });
  await page.fill('#account', account);
  await page.fill('#password', password);
  await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
}

export async function captureViews(browser, prefix) {
  const files = [];
  const anonymous = await newContext(browser);
  const page = await anonymous.newPage();
  try {
    await page.goto('/Auths/Login');
    await page.waitForSelector('#account', { timeout: 60000 });
    await shot(page, `${prefix}-01-登入`, files);

    await fillLogin(page, 'e2e-nobody', 'Wrong!Pass123');
    await page.click('button.login-button');
    await page.waitForSelector('.error-message, .validation-message, [role="alert"]', { timeout: 30000 });
    await shot(page, `${prefix}-02-登入失敗`, files);

    await page.goto('/Auths/ForgotPassword');
    await page.waitForSelector('.login-card .auth-links', { timeout: 60000 });
    await shot(page, `${prefix}-03-忘記密碼`, files);

    await page.goto('/Auths/ResetPassword?userId=0&token=invalid');
    await page.waitForSelector('.login-card .auth-links', { timeout: 60000 });
    await shot(page, `${prefix}-04-重設密碼無效連結`, files);

    await page.goto('/Auths/Pending');
    await page.waitForSelector('.pending-card', { timeout: 60000 });
    await shot(page, `${prefix}-05-Google待審`, files);

    await fillLogin(page, twoFactorAccount, userPassword);
    await Promise.all([page.waitForURL(/TwoFactor/i, { timeout: 30000 }), page.click('button.login-button')]);
    await page.waitForSelector('#code', { timeout: 60000 });
    await shot(page, `${prefix}-06-兩步驟驗證`, files);
  } catch (error) {
    await screenshot(page, `${prefix}-失敗當下`);
    throw error;
  }

  await anonymous.close();

  const changeContext = await newContext(browser);
  const changePage = await changeContext.newPage();
  await fillLogin(changePage, mustChangeAccount, userPassword);
  await Promise.all([changePage.waitForURL(/ChangePassword/i, { timeout: 60000 }), changePage.click('button.login-button')]);
  await changePage.waitForSelector('.change-password-page', { timeout: 60000 });
  await shot(changePage, `${prefix}-07-強制改密碼`, files);
  await changeContext.close();

  const setupContext = await newContext(browser);
  const setupPage = await setupContext.newPage();
  await login(setupPage, { account: setupAccount, password: userPassword });
  await setupPage.goto('/TwoFactorSetup');
  await setupPage.waitForSelector('.tfa-page', { timeout: 60000 });
  await shot(setupPage, `${prefix}-08-兩步驟驗證設定`, files);
  await setupPage.goto('/no-such-page-e2e');
  await setupPage.getByText('Not Found').waitFor({ timeout: 60000 });
  await shot(setupPage, `${prefix}-09-找不到頁面`, files);
  await setupPage.goto('/Error');
  await setupPage.waitForSelector('h1.text-danger', { timeout: 60000 });
  await shot(setupPage, `${prefix}-10-錯誤頁`, files);
  await setupContext.close();
  return files;
}

// 直接執行：node m09-views.mjs <前綴>（預設 view）
if (import.meta.url === `file:///${process.argv[1].replaceAll('\\', '/')}`) {
  seedAuthUsers();
  const browser = await launchBrowser();
  try {
    console.log((await captureViews(browser, process.argv[2] ?? 'view')).join('\n'));
  } finally {
    await browser.close();
  }
}
