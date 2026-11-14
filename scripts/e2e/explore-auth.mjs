// .NET 11 全面實機探索（0.10.16 起）：帳號流程中 m09-auth 沒涵蓋的部分。
//   U1 改密碼：目前密碼打錯、新密碼與確認不一致、新密碼太弱、新密碼與目前相同，一律擋下並說明；合法的改得成功，舊密碼不能再登入、新密碼可以。
//   U2 重設密碼的連結用過一次後再開：顯示連結無效，不能再設一次。
//   U3 登出後按瀏覽器「上一頁」：不會看到需要登入的內容（回登入頁）。
//   U4 登入的帳號大小寫：SUPPORT 與 support 的結果（記錄實際行為）；錯誤密碼的訊息不透露帳號是否存在（不存在的帳號與錯誤密碼訊息相同）。
// 結果寫到 E2E_OUT_DIR/explore-auth-results.json，截圖 explore-auth-*.png。
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { baseUrl, createRecorder, createUser, databaseFile, execute, launchBrowser, login, newContext, screenshot, supportPassword } from './common.mjs';
import { collectProblems } from './explore-common.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const stamp = Date.now().toString(36).slice(-4);
const account = `e2e-auth${stamp}`;
const password = 'Auth!E2e-' + stamp + 'Aa9';
createUser(account, password);
execute('UPDATE MyUser SET Email = ? WHERE Account = ?', `${account}@e2e.test`, account);

const recorder = createRecorder('explore-auth-results.json');
const browser = await launchBrowser();
const frontendRoot = new URL('/', baseUrl).href;

/** 在登入頁送出帳號密碼，回傳停留的路徑與錯誤訊息。 */
async function tryLogin(page, acc, pwd) {
  await page.goto('/Auths/Login');
  await page.locator('#account').waitFor({ timeout: 60000 });
  await page.fill('#account', acc);
  await page.fill('#password', pwd);
  await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
  await page.click('button.login-button');
  await page.waitForTimeout(3000);
  const error = await page.locator('.error-message, .alert-danger, .login-error').first().innerText().catch(() => '');
  return { path: new URL(page.url()).pathname, error: error.replace(/\s+/g, ' ').trim() };
}

try {
  // U1 改密碼。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    const problems = collectProblems(page);
    await login(page, { account, password });
    const attempt = async (current, next, confirm) => {
      await page.goto('/ChangePassword');
      await page.locator('input[placeholder="請輸入目前密碼"]').waitFor({ timeout: 60000 });
      await page.fill('input[placeholder="請輸入目前密碼"]', current);
      await page.fill('input[placeholder="請輸入新密碼"]', next);
      await page.fill('input[placeholder="請再次輸入新密碼"]', confirm);
      await page.locator('.change-password-page button[type="submit"]').click();
      await page.waitForTimeout(2500);
      const shown = (await page.locator('.ant-notification-notice, .alert-danger, .validation-message, .ant-form-item-explain-error').allInnerTexts()).join(' ').replace(/\s+/g, ' ');
      await page.locator('.ant-notification-notice-close').first().click().catch(() => {});
      return shown;
    };
    const newPassword = 'Changed!E2e-' + stamp + 'Bb8';
    const wrongCurrent = await attempt('Wrong!Pass2026x', newPassword, newPassword);
    const mismatch = await attempt(password, newPassword, newPassword + 'x');
    const weak = await attempt(password, 'abc', 'abc');
    const same = await attempt(password, password, password);
    await screenshot(page, 'explore-auth-U1-擋下');
    const ok = await attempt(password, newPassword, newPassword);
    await screenshot(page, 'explore-auth-U1-成功');
    const issues = await problems.take();
    await context.close();
    const page2 = await (await newContext(browser)).newPage();
    const oldLogin = await tryLogin(page2, account, password);
    const newLogin = await tryLogin(page2, account, newPassword);
    recorder.add('U1', '改密碼：錯的目前密碼、確認不一致、太弱、與目前相同都擋下；合法的成功，舊密碼失效、新密碼可登入',
      [wrongCurrent, mismatch, weak, same].every((x) => x.length > 0) && oldLogin.path.toLowerCase().startsWith('/auths/login') && !newLogin.path.toLowerCase().startsWith('/auths/login') && issues.length === 0,
      `目前密碼錯「${wrongCurrent.slice(0, 40)}」；不一致「${mismatch.slice(0, 40)}」；太弱「${weak.slice(0, 40)}」；相同「${same.slice(0, 40)}」；合法「${ok.slice(0, 40)}」；舊密碼登入停在 ${oldLogin.path}；新密碼登入到 ${newLogin.path}${issues.length ? '；問題：' + issues.join('；') : ''}`);
    await page2.context().close();
  }

  // U2 重設連結用過一次後再開。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    await page.goto('/Auths/ForgotPassword');
    await page.waitForSelector('#identifier', { timeout: 60000 });
    await page.fill('#identifier', account);
    await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
    await page.click('button.login-button');
    const mails = join(dirname(dirname(databaseFile)), 'Mails');
    let link = null;
    for (let i = 0; i < 40 && !link; i++) {
      await page.waitForTimeout(500);
      try {
        for (const file of readdirSync(mails)) {
          const text = readFileSync(join(mails, file), 'utf8').replace(/=\r?\n/g, '').replace(/=3D/g, '=');
          const prefix = `${frontendRoot}Auths/ResetPassword?token=`;
          const at = text.indexOf(prefix);
          const token = at < 0 ? '' : /^[A-Za-z0-9_-]+/.exec(text.slice(at + prefix.length))?.[0] ?? '';
          if (token && text.includes(`${account}@e2e.test`)) {
            link = prefix + token;
          }
        }
      } catch { /* 資料夾還沒建立 */ }
    }

    let firstOk = false;
    let secondText = '';
    let secondHasForm = true;
    if (link) {
      await page.goto(link);
      await page.waitForSelector('#newPassword', { timeout: 60000 });
      const reset = 'Reset!E2e-' + stamp + 'Cc7';
      await page.fill('#newPassword', reset);
      await page.fill('#confirmPassword', reset);
      await page.click('button.login-button');
      firstOk = await page.waitForURL(/\/Auths\/Login\?reset=1/i, { timeout: 30000 }).then(() => true).catch(() => false);
      await page.goto(link);
      await page.locator('.login-card').waitFor({ timeout: 60000 });
      await page.waitForTimeout(2000);
      secondHasForm = await page.locator('#newPassword').isVisible().catch(() => false);
      secondText = (await page.locator('.login-card').innerText()).replace(/\s+/g, ' ');
      await screenshot(page, 'explore-auth-U2-連結用過');
    }
    recorder.add('U2', '重設密碼的連結用過一次後再開：顯示無效、不能再設', Boolean(link) && firstOk && !secondHasForm,
      `信件連結=${Boolean(link)}；第一次重設成功=${firstOk}；第二次開啟還有表單=${secondHasForm}；畫面「${secondText.slice(0, 80)}」`);
    await context.close();
  }

  // U3 登出後按上一頁。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    await login(page);
    await page.goto('/categories');
    await page.waitForSelector('.ant-table-wrapper', { timeout: 60000 });
    await page.goto('/Auths/Logout');
    await page.waitForURL(/\/Auths\/Login/i, { timeout: 30000 });
    await page.waitForTimeout(1500);
    await page.goBack();
    await page.waitForTimeout(4000);
    const path = new URL(page.url()).pathname;
    const tableVisible = await page.locator('.ant-table-row').count();
    await screenshot(page, 'explore-auth-U3-登出後上一頁');
    recorder.add('U3', '登出後按上一頁：看不到需要登入的內容', path.toLowerCase().startsWith('/auths/') && tableVisible === 0,
      `停在 ${path}；表格列 ${tableVisible}`);
    await context.close();
  }

  // U4 帳號大小寫、錯誤訊息不透露帳號是否存在。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    const upper = await tryLogin(page, 'SUPPORT', supportPassword);
    await context.clearCookies();
    await page.evaluate(() => { sessionStorage.clear(); localStorage.clear(); });
    const noSuch = await tryLogin(page, `nobody-${stamp}`, 'Wrong!Pass2026x');
    const wrongPwd = await tryLogin(page, account, 'Wrong!Pass2026x');
    await screenshot(page, 'explore-auth-U4-錯誤訊息');
    recorder.add('U4', '錯誤密碼與不存在的帳號訊息相同（不透露帳號是否存在）；記錄帳號大小寫的行為',
      noSuch.error !== '' && noSuch.error === wrongPwd.error,
      `大寫 SUPPORT 登入後停在 ${upper.path}；不存在的帳號「${noSuch.error}」；錯誤密碼「${wrongPwd.error}」`);
    await context.close();
  }
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
