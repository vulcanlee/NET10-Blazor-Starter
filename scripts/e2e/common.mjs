// 實機驗證的共用工具（0.10.0 起，WASM 遷移 M1）。用法見同資料夾的 README.md。
// playwright-core 不放在 repo 裡：由環境變數 PLAYWRIGHT_CORE_DIR 指向已安裝的套件資料夾，瀏覽器用本機的 Chrome。
import { pbkdf2Sync, randomBytes, randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { DatabaseSync } from 'node:sqlite';

const playwrightDir = process.env.PLAYWRIGHT_CORE_DIR;
if (!playwrightDir) {
  throw new Error('請設定 PLAYWRIGHT_CORE_DIR（playwright-core 套件資料夾，例如 <暫存>/node_modules/playwright-core）。');
}

const { chromium } = await import(pathToFileURL(join(playwrightDir, 'index.mjs')).href);

export const baseUrl = process.env.E2E_BASE_URL ?? 'https://localhost:7144';
// API 主機（0.10.10 起，M11）：雙站台時前端與 API 在不同網址；沒設定時與前端同一個網址（同站台）。
export const apiUrl = process.env.E2E_API_URL ?? baseUrl;

/** `/api/...` 這類路徑在頁面裡要打到 API 主機（雙站台時跨來源，實際經過 CORS）。 */
export function apiTarget(path) {
  return path.startsWith('/api/') ? new URL(path, apiUrl).href : path;
}
export const supportPassword = process.env.E2E_SUPPORT_PASSWORD;
export const outDir = process.env.E2E_OUT_DIR ?? join(process.cwd(), 'e2e-out');
export const databaseFile = process.env.E2E_DB_FILE;
mkdirSync(join(outDir, 'screenshots'), { recursive: true });

if (!supportPassword) {
  throw new Error('請設定 E2E_SUPPORT_PASSWORD（Start-E2EHost.ps1 印出的 support 暫時密碼）。');
}

export async function launchBrowser() {
  return chromium.launch({
    executablePath: process.env.E2E_CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe',
    headless: process.env.E2E_HEADED !== '1',
  });
}

export async function newContext(browser) {
  return browser.newContext({ ignoreHTTPSErrors: true, baseURL: baseUrl, viewport: { width: 1440, height: 900 }, locale: 'zh-TW' });
}

/**
 * 以登入頁登入（驗證碼的答案在隱藏欄位，見登入頁）。0.10.8 起登入頁在 WASM：先等 WASM 載入、表單出現，選擇器與隱藏欄位不變。
 * 登入成功後 Token 存在瀏覽器（勾記住我 localStorage，否則 sessionStorage；同一個分頁換頁仍在），再整頁導到 returnUrl。
 */
export async function login(page, { account = 'support', password = supportPassword, rememberMe = false } = {}) {
  await page.goto('/Auths/Login');
  await page.locator('#account').waitFor({ timeout: 60000 });
  await page.fill('#account', account);
  await page.fill('#password', password);
  await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
  if (rememberMe) {
    await page.check('#rememberMe');
  }

  await Promise.all([
    page.waitForURL((url) => !url.pathname.startsWith('/Auths/Login'), { timeout: 30000 }),
    page.click('button.login-button'),
  ]);
}

/** 在頁面裡呼叫 API（同源、不帶 Token；0.10.8 起沒有登入 Cookie，需要登入的 API 用 apiAsPageUser）。 */
export async function callApi(page, path, { method = 'GET', headers = {}, body } = {}) {
  return page.evaluate(async ({ path, method, headers, body }) => {
    const response = await fetch(path, {
      method,
      headers: body === undefined ? headers : { 'Content-Type': 'application/json', ...headers },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    let json = null;
    try { json = await response.json(); } catch { /* 沒有內容 */ }
    return { status: response.status, json };
  }, { path: apiTarget(path), method, headers, body });
}

/** 唯讀查詢隔離資料庫（Start-E2EHost.ps1 印出的資料庫目錄下的 BackendDB.db）。 */
export function query(sql, ...params) {
  const db = new DatabaseSync(requireDatabase(), { readOnly: true });
  try { return db.prepare(sql).all(...params); } finally { db.close(); }
}

/** 寫入隔離資料庫：只用來佈置情境（例如把帳號標成必須改密碼）。 */
export function execute(sql, ...params) {
  const db = new DatabaseSync(requireDatabase());
  try { return db.prepare(sql).run(...params); } finally { db.close(); }
}

function requireDatabase() {
  if (!databaseFile) {
    throw new Error('請設定 E2E_DB_FILE（隔離資料庫檔案的完整路徑）。');
  }
  return databaseFile;
}

export async function screenshot(page, name) {
  const file = join(outDir, 'screenshots', `${name}.png`);
  await page.screenshot({ path: file, fullPage: false });
  return file;
}

/** 收集情境結果，最後寫成 results.json，並在主控台印出一行。 */
export function createRecorder(fileName) {
  const results = [];
  return {
    results,
    add(id, title, passed, actual) {
      results.push({ id, title, passed, actual });
      console.log(`${passed ? 'PASS' : 'FAIL'} ${id} ${title} — ${actual}`);
    },
    save() {
      writeFileSync(join(outDir, fileName), JSON.stringify(results, null, 2), 'utf8');
      return results.every((x) => x.passed);
    },
  };
}

/**
 * 在隔離資料庫建立一個一般帳號（非管理員，主要角色沿用 support 的），密碼雜湊格式同 SecurePasswordHasher。
 * 以 support 那一列為範本複製，不必知道每個欄位的預設值；只用於 Start-E2EHost.ps1 的隔離資料庫。
 */
export function createUser(account, password) {
  const salt = randomBytes(16);
  const hash = pbkdf2Sync(password, salt, 100000, 32, 'sha256');
  const overrides = {
    Account: account,
    Name: account,
    Password: `PBKDF2.SHA256$100000$${salt.toString('base64')}$${hash.toString('base64')}`,
    Email: null,
    IsAdmin: 0,
    Status: 1,
    MustChangePassword: 0,
    PasswordChangedAtUtc: new Date().toISOString().replace('T', ' ').replace('Z', ''),
    SecurityStamp: randomUUID().replaceAll('-', ''),
    ConcurrencyStamp: randomUUID().replaceAll('-', ''),
    AccessFailedCount: 0,
    LockoutEndUtc: null,
    TwoFactorEnabled: 0,
    TwoFactorSecret: null,
  };
  const db = new DatabaseSync(requireDatabase());
  try {
    const columns = db.prepare('PRAGMA table_info(MyUser)').all().map((x) => x.name).filter((x) => x !== 'Id');
    const select = columns.map((x) => (x in overrides ? '?' : `"${x}"`)).join(', ');
    const values = columns.filter((x) => x in overrides).map((x) => overrides[x]);
    db.prepare(`INSERT INTO MyUser (${columns.map((x) => `"${x}"`).join(', ')}) SELECT ${select} FROM MyUser WHERE Account = 'support'`).run(...values);
  } finally {
    db.close();
  }
}

// ── 0.10.3 起從 m03-crud.mjs 移來，M3、M4 情境共用 ──

/** 建一個角色（指定權限鍵）與使用者；團隊給名稱。 */
export function createScopedUser(account, password, roleName, keys, teamNames = []) {
  if (query('SELECT Id FROM MyUser WHERE Account = ?', account).length > 0) {
    return;
  }

  execute(
    `INSERT INTO RoleView (Name, TabViewJson, DefaultTeamsJson, RequireTwoFactor, CreateAt, UpdateAt, ConcurrencyStamp, IsDeleted)
     VALUES (?, '[]', '[]', 0, datetime('now'), datetime('now'), lower(hex(randomblob(16))), 0)`, roleName);
  const roleId = query('SELECT Id FROM RoleView WHERE Name = ?', roleName)[0].Id;
  for (const key of keys) {
    if (query('SELECT Id FROM Permission WHERE Key = ?', key).length === 0) {
      execute('INSERT INTO Permission (Key, DisplayName, SortOrder) VALUES (?, ?, 0)', key, key);
    }
    execute('INSERT INTO RolePermissionMap (RoleViewId, PermissionId) VALUES (?, ?)', roleId, query('SELECT Id FROM Permission WHERE Key = ?', key)[0].Id);
  }

  createUser(account, password);
  const userId = query('SELECT Id FROM MyUser WHERE Account = ?', account)[0].Id;
  execute('UPDATE MyUser SET RoleViewId = ? WHERE Id = ?', roleId, userId);
  execute('DELETE FROM UserRole WHERE MyUserId = ?', userId);
  execute('INSERT INTO UserRole (MyUserId, RoleViewId) VALUES (?, ?)', userId, roleId);
  for (const team of teamNames) {
    execute('INSERT INTO UserTeam (MyUserId, TeamId) VALUES (?, ?)', userId, query('SELECT Id FROM Team WHERE Name = ?', team)[0].Id);
  }
}

export async function isWasmPage(page) {
  return page.evaluate(() => performance.getEntriesByType('resource').some((x) => x.name.includes('.wasm')));
}

/**
 * 取這個分頁登入的存取 Token（0.10.8 起從瀏覽器的 storage 讀，與 WASM 頁面相同；快到期就先刷新並寫回）。
 * 必須在登入過、同一個分頁（sessionStorage）或勾了記住我（localStorage）的頁面上呼叫。
 */
export async function pageAccessToken(page) {
  return page.evaluate(async (refreshUrl) => {
    const key = 'myproject.auth';
    const persistent = localStorage.getItem(key) !== null;
    const raw = localStorage.getItem(key) ?? sessionStorage.getItem(key);
    if (!raw) {
      throw new Error('這個分頁沒有登入的 Token（請先 login，或在同一個分頁）。');
    }

    let tokens = JSON.parse(raw);
    if (new Date(tokens.AccessTokenExpiresAt).getTime() < Date.now() + 30000) {
      const response = await fetch(refreshUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: tokens.RefreshToken }),
      });
      const data = (await response.json()).data;
      tokens = { ...tokens, AccessToken: data.accessToken, AccessTokenExpiresAt: data.accessTokenExpiresAt, RefreshToken: data.refreshToken, RefreshTokenExpiresAt: data.refreshTokenExpiresAt };
      (persistent ? localStorage : sessionStorage).setItem(key, JSON.stringify(tokens));
    }

    return tokens.AccessToken;
  }, apiTarget('/api/v1/Auth/refresh'));
}

/** 這個分頁存的 Token（0.10.8 起登入後存在瀏覽器）：{ AccessToken, RefreshToken, IsPersistent, UserId, storage: 'local'|'session' }；沒有回 null。 */
export async function pageTokens(page) {
  return page.evaluate(() => {
    const key = 'myproject.auth';
    const local = localStorage.getItem(key);
    const raw = local ?? sessionStorage.getItem(key);
    return raw ? { ...JSON.parse(raw), storage: local ? 'local' : 'session' } : null;
  });
}

/** 以這個分頁登入的存取 Token（與 WASM 頁面相同）直接呼叫 API，回傳狀態碼。 */
export async function apiAsPageUser(page, path, { method = 'GET', body } = {}) {
  return (await apiJsonAsPageUser(page, path, { method, body })).status;
}

/** 同上，回傳狀態碼、JSON 與內容長度。 */
export async function apiJsonAsPageUser(page, path, { method = 'GET', body } = {}) {
  const token = await pageAccessToken(page);
  return page.evaluate(async ({ path, method, body, token }) => {
    const response = await fetch(path, {
      method,
      headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    let json = null;
    try { json = JSON.parse(text); } catch { /* 檔案或空白 */ }
    return { status: response.status, json, length: text.length };
  }, { path: apiTarget(path), method, body, token });
}

/**
 * 取代 page.waitForFunction（0.10.11 起）：Playwright 的 waitForFunction 第一次判斷不成立、開始輪詢時要在頁面裡 eval，
 * 會被前端的 CSP（script-src 沒有 'unsafe-eval'）擋下 —— 改在 Node 端以 page.evaluate 輪詢（不受 CSP 影響）。用法與參數同 waitForFunction。
 */
export async function waitForPredicate(page, predicate, arg = null, { timeout = 30000, polling = 200 } = {}) {
  const deadline = Date.now() + timeout;
  for (;;) {
    let value;
    try {
      value = await page.evaluate(predicate, arg);
    } catch (error) {
      // 換頁中執行環境被換掉：下一輪再試。
      if (!/Execution context was destroyed|navigation/i.test(error.message)) {
        throw error;
      }
    }

    if (value) {
      return value;
    }

    if (Date.now() > deadline) {
      throw new Error(`waitForPredicate 逾時（${timeout} ms）：${predicate.toString().slice(0, 120)}`);
    }

    await new Promise((resolve) => setTimeout(resolve, polling));
  }
}
