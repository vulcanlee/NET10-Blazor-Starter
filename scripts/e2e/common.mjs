// 實機驗證的共用工具（0.10.0 起，WASM 遷移 M1）。用法見同資料夾的 README.md。
// playwright-core 不放在 repo 裡：由環境變數 PLAYWRIGHT_CORE_DIR 指向已安裝的套件資料夾，瀏覽器用本機的 Chrome。
import { pbkdf2Sync, randomBytes, randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
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

// ---- 各腳本共用的小工具（2026/11/26 第四批整併起（腳本不隨網站發佈、不升版）：原本各腳本各寫一份、內容相同；只差數值的改成參數） ----

/** 條件不成立就丟出例外（情境的判定）。 */
export function check(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

/** 現在的 UTC 時間（資料庫格式 yyyy-MM-dd HH:mm:ss），用來只看這次執行之後寫入的資料。 */
export function utcNow() {
  return new Date().toISOString().replace('T', ' ').slice(0, 19);
}

/**
 * 情境步驟 step(id, title, action)：action 的回傳值（沒有時是「通過」）記為通過；丟出例外記為失敗，只留訊息第一行。
 * 有給 failurePage（回傳目前頁面的函式）時，失敗當下另存畫面 `<id>-失敗當下.png`，並在訊息後面標出擋住點擊的元素
 * （0.10.11 起 m06-ai 用），方便判斷是程式還是腳本的問題。
 */
export function createStep(recorder, { failurePage } = {}) {
  return async (id, title, action) => {
    try {
      const actual = await action();
      recorder.add(id, title, true, actual ?? '通過');
    } catch (error) {
      const message = String(error?.message ?? error);
      if (!failurePage) {
        recorder.add(id, title, false, message.split('\n')[0]);
        return;
      }

      const blocker = /<[^>]+> (?:from <[^>]+> subtree )?intercepts pointer events/.exec(message)?.[0] ?? '';
      await failurePage()?.screenshot({ path: join(outDir, 'screenshots', `${id}-失敗當下.png`) }).catch(() => {});
      recorder.add(id, title, false, message.split('\n')[0] + (blocker ? `；擋住的元素：${blocker.slice(0, 200)}` : ''));
    }
  };
}

/** 執行 trigger 觸發下載，回傳建議檔名與內容。 */
export async function download(page, trigger) {
  const [file] = await Promise.all([page.waitForEvent('download', { timeout: 120000 }), trigger()]);
  return { name: file.suggestedFilename(), bytes: readFileSync(await file.path()) };
}

/** since 之後寫入的某一種稽核（依寫入順序）。 */
export function audits(action, since) {
  return query('SELECT TargetId, Detail FROM AuditLog WHERE Action = ? AND OccurredAt >= ? ORDER BY Id', action, since);
}

/** since 之後寫入的某一種稽核有幾筆。 */
export function auditCount(action, since) {
  return query('SELECT COUNT(*) AS N FROM AuditLog WHERE Action = ? AND OccurredAt >= ?', action, since)[0].N;
}

/** 填登入表單（不等導頁），驗證碼答案在隱藏欄位。 */
export async function fillLogin(page, account, password) {
  await page.goto('/Auths/Login');
  await page.waitForSelector('#account', { timeout: 60000 });
  await page.fill('#account', account);
  await page.fill('#password', password);
  await page.fill('#captcha', await page.inputValue('input[name="Input.CaptchaCode"]'));
}

/**
 * 截圖前讓畫面穩定：滑鼠移到左上角（避免提示框、懸停才展開的使用者選單蓋住畫面），再等 wait 毫秒讓動畫結束。
 * 畫面腳本 m03～m06 等 800 ms，m07～m09 等 1200 ms。
 */
export async function settle(page, wait = 800) {
  await page.mouse.move(1, 1);
  await page.waitForTimeout(wait);
}

/** 等表格出現資料列（tableClass 是表格的 class），再 settle。 */
export async function waitForRows(page, tableClass, { timeout = 30000 } = {}) {
  await page.waitForSelector(`.${tableClass} .ant-table-row`, { timeout });
  await settle(page);
}

/** 按右上角的關閉鈕關掉對話窗，等它消失（m03-views 用 Esc 關、m06-ai 先避開提示框，那兩支各自保留自己的寫法）。 */
export async function closeModal(page) {
  await page.locator('.ant-modal-close').last().click();
  await page.waitForSelector('.ant-modal-content', { state: 'hidden', timeout: 15000 });
}

/** 按標籤「含有」title 的第一個工具列按鈕（畫面腳本用；m03-views 保留自己不加 .first() 的版本，符合的按鈕不只一個時會報錯；情境腳本用 crud-ui.mjs 的 clickToolbar，它先找標籤完全相符的）。 */
export async function toolbar(page, title) {
  await page.locator('button.toolbar-icon-button', { hasText: title }).first().click();
}

/** 隔離主機的應用程式日誌資料夾（E2E_LOG_DIR 可覆寫）。呼叫時才算：煙霧測試這類不讀資料庫的腳本也會載入本檔。 */
function serverLogDir() {
  return process.env.E2E_LOG_DIR ?? join(dirname(dirname(databaseFile)), 'logs', 'MyProject.Web');
}

/** 應用程式日誌全部的內容（不含 NLog 自己的 *-nlog-internal.log）；資料夾還不存在時是空字串。 */
export function allLogText() {
  const logDir = serverLogDir();
  if (!existsSync(logDir)) {
    return '';
  }

  return readdirSync(logDir).filter((x) => x.includes('-logfile-') && x.endsWith('.log')).sort()
    .map((x) => readFileSync(join(logDir, x), 'utf8')).join('\n');
}

/** 每 interval 毫秒呼叫一次 check（同步函式），回傳第一個為真的值；逾時回 null（不丟例外，由呼叫端判定）。 */
export async function waitForValue(check, timeout = 90000, interval = 500) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const value = check();
    if (value) {
      return value;
    }

    if (Date.now() > deadline) {
      return null;
    }

    await new Promise((resolve) => setTimeout(resolve, interval));
  }
}
