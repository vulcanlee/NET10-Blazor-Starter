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

/** 以登入頁登入（驗證碼的答案在隱藏欄位，見登入頁）。 */
export async function login(page, { account = 'support', password = supportPassword, rememberMe = false } = {}) {
  await page.goto('/Auths/Login');
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

/** 在頁面裡呼叫 API（同源，自動帶 Cookie）。 */
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
  }, { path, method, headers, body });
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

/** 以網頁登入的 Cookie 換一組存取 Token（與 WASM 頁面相同的方式），再直接呼叫 API。 */
export async function apiAsPageUser(page, path, { method = 'GET', body } = {}) {
  return page.evaluate(async ({ path, method, body }) => {
    const bridge = await fetch('/api/v1/Auth/bridge', { method: 'POST', headers: { 'X-MyProject-Client': '1' } });
    const token = (await bridge.json()).data.accessToken;
    const response = await fetch(path, {
      method,
      headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    return response.status;
  }, { path, method, body });
}
