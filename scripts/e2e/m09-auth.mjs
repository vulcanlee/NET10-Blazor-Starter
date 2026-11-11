// WASM 遷移 M9（0.10.8）的實機情境：純 Token 登入（不再發登入 Cookie）。
// 記住我有／無時重開與開新分頁、跨分頁登出、連錯 5 次鎖定、兩步驟驗證＋記住這台裝置、強制改密碼無法直打 API 繞過、
// 忘記密碼信件（測試主機把信存成 .eml）、Token 重用偵測、兩分頁同時刷新不被登出，最後跑全部路由的煙霧測試。
// 結果寫到 E2E_OUT_DIR/m09-results.json 與 smoke-results.json。
import { createHmac } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { baseUrl, createRecorder, createUser, databaseFile, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { watchErrors } from './crud-ui.mjs';
import { mustChangeAccount, seedAuthUsers, setupAccount, userPassword } from './m09-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m09-results.json');
const smoke = createRecorder('smoke-results.json');
const lockAccount = 'e2e-m9-lock';
const resetAccount = 'e2e-m9-reset';
const resetEmail = 'e2e-m9-reset@e2e.test';

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

/** RFC 6238 TOTP（SHA1、6 碼、30 秒）。金鑰是 Base32（畫面上每 4 字空一格）。 */
function totp(secret, offsetSteps = 0) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const clean = secret.replace(/[\s=]/g, '').toUpperCase();
  let bits = '';
  for (const char of clean) {
    bits += alphabet.indexOf(char).toString(2).padStart(5, '0');
  }

  const key = Buffer.from(bits.match(/.{8}/g).map((b) => parseInt(b, 2)));
  const counter = Math.floor(Date.now() / 1000 / 30) + offsetSteps;
  const message = Buffer.alloc(8);
  message.writeBigUInt64BE(BigInt(counter));
  const hmac = createHmac('sha1', key).update(message).digest();
  const offset = hmac[hmac.length - 1] & 0x0f;
  const code = ((hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, '0');
  return code;
}

/** 等到下一個 30 秒時間步（同一個時間步的驗證碼只能用一次）。 */
async function nextTimeStep() {
  const wait = 30_000 - (Date.now() % 30_000) + 1500;
  await new Promise((resolve) => setTimeout(resolve, wait));
}

async function storedTokens(page) {
  return page.evaluate(() => {
    const raw = localStorage.getItem('myproject.auth') ?? sessionStorage.getItem('myproject.auth');
    return raw ? { ...JSON.parse(raw), where: localStorage.getItem('myproject.auth') ? 'local' : 'session' } : null;
  });
}

const field = (tokens, name) => tokens?.[name] ?? tokens?.[name[0].toUpperCase() + name.slice(1)];

async function isSignedIn(page, route = '/App') {
  await page.goto(route);
  await page.waitForTimeout(5000);
  return !new URL(page.url()).pathname.toLowerCase().startsWith('/auths/login');
}

async function fillLogin(page, account, password) {
  await page.goto('/Auths/Login');
  await page.waitForSelector('#account', { timeout: 60000 });
  await page.fill('#account', account);
  await page.fill('#password', password);
  await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
}

seedAuthUsers();
for (const account of [lockAccount, resetAccount]) {
  if (query('SELECT Id FROM MyUser WHERE Account = ?', account).length === 0) {
    createUser(account, userPassword);
  }
}

execute('UPDATE MyUser SET Email = ? WHERE Account = ?', resetEmail, resetAccount);

const browser = await launchBrowser();
try {
  const since = utcNow();

  await step('M9-T1', '沒有登入 Cookie：登入後瀏覽器只有 Token，整頁開登入後頁面正常', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await login(page, { rememberMe: true });
    const cookies = (await context.cookies()).map((x) => x.name);
    check(!cookies.some((name) => /Auth|MyProject\.(?!External)/i.test(name) && !/Antiforgery/i.test(name)), `還有登入 Cookie：${cookies.join(', ')}`);
    const tokens = await storedTokens(page);
    check(tokens?.where === 'local', `記住我卻沒有存 localStorage：${tokens?.where}`);
    check(await isSignedIn(page, '/projects'), '整頁開 /projects 被導回登入頁');
    await context.close();
    return `Cookie：${cookies.join(', ') || '（無）'}；Token 在 localStorage`;
  });

  await step('M9-T2', '記住我：關掉所有分頁再開，仍維持登入', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await login(page, { rememberMe: true });
    await page.close();
    const reopened = await context.newPage();
    check(await isSignedIn(reopened), '重開後要重新登入');
    await context.close();
    return '重開後直接進入';
  });

  await step('M9-T3', '不記住我：開新分頁由已開分頁交接 Token；全部關掉後要重新登入', async () => {
    const context = await newContext(browser);
    const first = await context.newPage();
    await login(first);
    const tokens = await storedTokens(first);
    check(tokens?.where === 'session', `不記住我卻存在 ${tokens?.where}`);
    const second = await context.newPage();
    const started = Date.now();
    check(await isSignedIn(second, '/Profile'), '新分頁沒有拿到 Token');
    const secondTokens = await storedTokens(second);
    check(field(secondTokens, 'refreshToken') !== field(tokens, 'refreshToken'), '新分頁沿用同一串刷新 Token（應該另開一串）');
    await first.close();
    await second.close();
    const reopened = await context.newPage();
    check(!(await isSignedIn(reopened)), '關掉所有分頁後仍維持登入');
    await context.close();
    return `新分頁自動進入（含開頁與等待共 ${Math.round((Date.now() - started) / 100) / 10} 秒）、另開一串；全部關掉後回登入頁`;
  });

  await step('M9-T4', '跨分頁登出：一個分頁登出，另一個分頁也回登入頁', async () => {
    const context = await newContext(browser);
    const a = await context.newPage();
    await login(a, { rememberMe: true });
    const b = await context.newPage();
    check(await isSignedIn(b, '/Profile'), '分頁 B 沒登入');
    await a.goto('/Auths/Logout');
    await a.waitForURL(/\/Auths\/Login/i, { timeout: 30000 });
    await b.waitForURL(/\/Auths\/Login/i, { timeout: 30000 });
    const revoked = query("SELECT COUNT(*) AS N FROM RefreshSession WHERE RevokedReason = 'Logout' AND RevokedAtUtc >= ?", since)[0].N;
    await context.close();
    check(revoked >= 1, '伺服器沒有撤銷刷新 Token');
    return `兩個分頁都回登入頁；撤銷 ${revoked} 串`;
  });

  await step('M9-T5', '連錯 5 次鎖定：第 6 次密碼正確也進不去', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    for (let i = 0; i < 5; i++) {
      await fillLogin(page, lockAccount, 'Wrong!Pass999');
      await page.click('button.login-button');
      await page.waitForTimeout(1500);
    }

    await fillLogin(page, lockAccount, userPassword);
    await page.click('button.login-button');
    await page.waitForTimeout(3000);
    check(new URL(page.url()).pathname.toLowerCase().startsWith('/auths/login'), '鎖定後還能登入');
    const locked = query('SELECT LockoutEndUtc FROM MyUser WHERE Account = ?', lockAccount)[0].LockoutEndUtc;
    check(locked, '資料庫沒有鎖定時間');
    check(query("SELECT COUNT(*) AS N FROM AuditLog WHERE Action = 'Login.LockedOut' AND OccurredAt >= ?", since)[0].N >= 1, '沒有鎖定稽核');
    await screenshot(page, 'm09-t5-鎖定');
    await context.close();
    return `鎖定到 ${locked}（UTC）`;
  });

  await step('M9-T6', '兩步驟驗證：設定 → 登入要驗證碼 → 記住這台裝置 → 下次不用再輸入', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await login(page, { account: setupAccount, password: userPassword, rememberMe: true });
    await page.goto('/TwoFactorSetup');
    await page.locator('button', { hasText: '開始設定' }).click();
    const secret = (await page.locator('.tfa-key').innerText()).trim();
    await page.locator('input[placeholder="6 位數驗證碼"]').fill(totp(secret));
    await page.locator('input[placeholder="6 位數驗證碼"]').press('Tab');
    await page.locator('button', { hasText: /^\s*啟\s*用\s*$/ }).click();
    await page.waitForSelector('.tfa-codes', { timeout: 30000 });
    await page.locator('button', { hasText: '我已保存，繼續' }).click();
    await page.waitForTimeout(3000);
    check(!new URL(page.url()).pathname.toLowerCase().startsWith('/auths/login'), '啟用後被登出了');
    await page.goto('/Auths/Logout');
    await page.waitForURL(/\/Auths\/Login/i, { timeout: 30000 });

    await nextTimeStep();
    await fillLogin(page, setupAccount, userPassword);
    await page.check('#rememberMe');
    await Promise.all([page.waitForURL(/TwoFactor/i, { timeout: 30000 }), page.click('button.login-button')]);
    await page.waitForSelector('#code', { timeout: 60000 });
    await page.fill('#code', totp(secret));
    await page.check('#rememberDevice');
    await Promise.all([page.waitForURL((url) => !/\/Auths\//i.test(url.pathname), { timeout: 30000 }), page.click('button.login-button')]);
    await page.goto('/Auths/Logout');
    await page.waitForURL(/\/Auths\/Login/i, { timeout: 30000 });

    await fillLogin(page, setupAccount, userPassword);
    await Promise.all([page.waitForURL((url) => !/\/Auths\/Login/i.test(url.pathname), { timeout: 30000 }), page.click('button.login-button')]);
    check(!/TwoFactor/i.test(page.url()), '記住裝置後仍要求驗證碼');
    const audits = query("SELECT Detail FROM AuditLog WHERE Action = 'Login.Success' AND ActorAccount = ? ORDER BY Id", setupAccount).map((x) => x.Detail ?? '');
    await context.close();
    check(audits.some((x) => x.includes('totp')) && audits.some((x) => x.includes('device')), `登入稽核：${audits.join(' | ')}`);
    return `驗證碼登入一次、記住裝置後直接進入（稽核含 totp、device）`;
  });

  await step('M9-T7', '強制改密碼：直接呼叫業務 API 被擋（403），/me 可用', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await fillLogin(page, mustChangeAccount, userPassword);
    await Promise.all([page.waitForURL(/ChangePassword/i, { timeout: 30000 }), page.click('button.login-button')]);
    await page.waitForTimeout(2000);
    const tokens = await storedTokens(page);
    const access = field(tokens, 'accessToken');
    const call = (path) => page.evaluate(async ([p, t]) => {
      const r = await fetch(p, { method: p.endsWith('search') ? 'POST' : 'GET', headers: { Authorization: `Bearer ${t}`, 'Content-Type': 'application/json' }, body: p.endsWith('search') ? '{}' : undefined });
      return { status: r.status, body: await r.text() };
    }, [path, access]);
    const blocked = await call('/api/v1/Project/options');
    const me = await call('/api/v1/Auth/me');
    await page.waitForTimeout(3000);
    const stillOnPage = /ChangePassword/i.test(page.url());
    await context.close();
    check(blocked.status === 403 && /PASSWORD_CHANGE_REQUIRED/i.test(blocked.body), `業務 API：${blocked.status} ${blocked.body.slice(0, 120)}`);
    check(me.status === 200, `/me：${me.status}`);
    check(stillOnPage, '強制改密碼頁不穩定（被導走）');
    return '業務 API 403 PASSWORD_CHANGE_REQUIRED；/me 200；頁面穩定不重新載入';
  });

  await step('M9-T8', '忘記密碼：信件連結可重設密碼，之後以新密碼登入', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await page.goto('/Auths/ForgotPassword');
    await page.waitForSelector('#identifier', { timeout: 60000 });
    await page.fill('#identifier', resetAccount);
    await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
    await page.click('button.login-button');
    await page.waitForTimeout(3000);
    const mails = join(dirname(dirname(databaseFile)), 'Mails');
    let link = null;
    for (let i = 0; i < 40 && !link; i++) {
      await page.waitForTimeout(500);
      try {
        for (const file of readdirSync(mails)) {
          const text = readFileSync(join(mails, file), 'utf8').replace(/=\r?\n/g, '').replace(/=3D/g, '=');
          const match = text.match(/https:\/\/localhost:7144\/Auths\/ResetPassword\?token=[A-Za-z0-9_-]+/);
          if (match && text.includes(resetEmail)) {
            link = match[0];
          }
        }
      } catch { /* 資料夾還沒建立 */ }
    }

    check(link, '找不到重設密碼信件');
    await page.goto(link.replace('https://localhost:7144', baseUrl));
    await page.waitForSelector('#newPassword', { timeout: 60000 });
    const newPassword = 'Reset!E2e2026y';
    await page.fill('#newPassword', newPassword);
    await page.fill('#confirmPassword', newPassword);
    await page.click('button.login-button');
    await page.waitForURL(/\/Auths\/Login\?reset=1/i, { timeout: 30000 });
    await page.waitForTimeout(1500);
    const notice = await page.locator('.success-message').innerText().catch(() => '');
    await login(page, { account: resetAccount, password: newPassword });
    await context.close();
    check(notice.includes('密碼已重設'), `登入頁沒有提示：${notice}`);
    return '信件連結 → 重設 → 登入頁提示「密碼已重設」→ 新密碼登入成功';
  });

  await step('M9-T9', 'Token 重用偵測：被換掉的刷新 Token 超過寬限期再用 → 整串撤銷、頁面被登出', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await login(page, { rememberMe: true });
    const old = field(await storedTokens(page), 'refreshToken');
    const refresh = (token) => page.evaluate(async (t) => (await fetch('/api/v1/Auth/refresh', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ refreshToken: t }),
    })).status, token);
    const first = await refresh(old);
    check(first === 200, `第一次刷新：${first}`);
    await page.waitForTimeout(32000);
    const reused = await refresh(old);
    check(reused === 401, `重用舊 Token：${reused}`);
    const reason = query("SELECT RevokedReason FROM RefreshSession WHERE RevokedAtUtc >= ? ORDER BY Id DESC LIMIT 1", since)[0]?.RevokedReason;
    await page.evaluate(() => {
      const key = 'myproject.auth';
      const raw = localStorage.getItem(key);
      const value = JSON.parse(raw);
      for (const name of ['accessTokenExpiresAt', 'AccessTokenExpiresAt']) {
        if (name in value) value[name] = '2000-01-01T00:00:00Z';
      }
      localStorage.setItem(key, JSON.stringify(value));
    });
    check(!(await isSignedIn(page)), '整串撤銷後頁面仍登入');
    await context.close();
    return `重用 401；撤銷原因 ${reason}；頁面回登入頁`;
  });

  await step('M9-T10', '兩個分頁同時刷新 Token 不會被登出', async () => {
    const context = await newContext(browser);
    const a = await context.newPage();
    await login(a, { rememberMe: true });
    const b = await context.newPage();
    check(await isSignedIn(b, '/Profile'), '分頁 B 沒登入');
    await a.evaluate(() => {
      const key = 'myproject.auth';
      const value = JSON.parse(localStorage.getItem(key));
      for (const name of ['accessTokenExpiresAt', 'AccessTokenExpiresAt']) {
        if (name in value) value[name] = '2000-01-01T00:00:00Z';
      }
      localStorage.setItem(key, JSON.stringify(value));
    });
    await Promise.all([a.reload(), b.reload()]);
    await a.waitForTimeout(6000);
    const ok = [a, b].every((p) => !new URL(p.url()).pathname.toLowerCase().startsWith('/auths/login'));
    const reused = query("SELECT COUNT(*) AS N FROM RefreshSession WHERE RevokedReason = 'Reused' AND RevokedAtUtc >= ?", utcNow().slice(0, 16))[0].N;
    await context.close();
    check(ok, '有分頁被登出');
    return `兩個分頁都維持登入（重用撤銷 ${reused} 筆）`;
  });

  await step('M9-T11', 'Google 登入', async () => {
    const context = await newContext(browser);
    const page = await context.newPage();
    await page.goto('/Auths/Login');
    await page.waitForSelector('#account', { timeout: 60000 });
    const hasGoogle = await page.locator('a, button', { hasText: /Google/ }).count();
    await context.close();
    // 真的 Google 帳號無法自動化；設定齊備時才顯示按鈕，兌換 API 由整合測試涵蓋（一次性、60 秒、綁定發起的瀏覽器）。
    return hasGoogle ? '有 Google 按鈕（實際登入需人工驗證）' : '開發機未設定 Google，按鈕不顯示（兌換流程由整合測試涵蓋）';
  });

  await runSmoke(browser, smoke);
} finally {
  await browser.close();
}

const okSmoke = smoke.save();
process.exit(recorder.save() && okSmoke ? 0 : 1);
