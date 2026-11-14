// .NET 11 全面實機探索（0.10.16 起）：權限與每一個 API 端點。
//   X1 只有「專案項目」檢視權限的帳號，逐一開每個登入後的路由：可以開的照常顯示，其他一律「你沒有權限存取此頁面」，
//      不會被丟回登入頁、沒有頁面錯誤；被擋的頁面都寫了權限拒絕稽核。
//   X2 從 Controllers 的原始碼列出每一個 API 端點（路由＋方法），以「沒登入」呼叫：除了標 [AllowAnonymous] 的控制器，一律 401；任何端點都不可以 5xx。
//   X3 同一份清單以 X1 的低權限帳號呼叫：允許的控制器（個人資料、外框、通知、首頁、登入相關、下載票證、專案）以外一律 403（或 404），任何端點都不可以 5xx。
// 結果寫到 E2E_OUT_DIR/explore-perm-results.json，截圖 explore-perm-*.png。
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { apiTarget, createRecorder, createScopedUser, execute, launchBrowser, login, newContext, pageAccessToken, query, screenshot } from './common.mjs';
import { authenticatedRoutes } from './smoke-routes.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
const password = 'Perm!E2e-' + Date.now().toString(36) + 'Aa9';
// 一般角色都有「首頁」（沒有的話登入後的首頁也顯示沒有權限，見測試報告）。
createScopedUser('e2e-perm-low', password, 'E2E 只看專案X', ['首頁', '專案項目:view']);

const recorder = createRecorder('explore-perm-results.json');

// ── 從原始碼列出端點 ──
const controllersDir = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'MyProject', 'MyProject.Web', 'Controllers');
function endpoints() {
  const list = [];
  for (const file of readdirSync(controllersDir).filter((x) => x.endsWith('Controller.cs'))) {
    const code = readFileSync(join(controllersDir, file), 'utf8');
    const controller = file.replace(/Controller\.cs$/, '');
    const base = (code.match(/\[Route\("([^"]+)"\)\]/)?.[1] ?? '').replace('[controller]', controller);
    if (!base.startsWith('api')) {
      continue; // 不是 API（Google 登入的導向）
    }

    const classAnonymous = /\[AllowAnonymous\][\s\S]{0,200}?class /.test(code);
    for (const m of code.matchAll(/\[Http(Get|Post|Put|Delete)(?:\("([^"]*)"[^)]*\))?[^\]]*\]([\s\S]{0,400}?)public /g)) {
      const template = m[2] ?? '';
      const path = '/' + [base, template].filter(Boolean).join('/')
        .replace(/\{[^}:]+:guid\}/gi, '00000000-0000-0000-0000-000000000001')
        .replace(/\{[^}]*id[^}]*\}/gi, '1')
        .replace(/\{[^}]+\}/g, 'x');
      const before = code.slice(Math.max(0, m.index - 300), m.index);
      const attributes = before.slice(before.lastIndexOf('}') + 1) + m[3];
      const anonymous = classAnonymous || /\[AllowAnonymous\]/.test(attributes);
      // [SignedInUser]：每個登入的人都可以用（例如公告橫幅、關閉公告）。
      const signedIn = /\[SignedInUser\]/.test(attributes);
      list.push({ controller, method: m[1].toUpperCase(), path, anonymous, signedIn });
    }
  }

  return list;
}

// 錯誤回報端點收到的內容會寫進系統例外紀錄（設計如此），以空內容探測只會留下一筆假錯誤：跳過。
const allEndpoints = endpoints().filter((x) => !x.path.endsWith('/shell/client-errors'));
// 低權限帳號照常可以用的控制器（每個登入的人都有，或帳號有專案檢視權限）。
const allowedForLow = new Set(['Profile', 'Shell', 'Notifications', 'Dashboard', 'Auth', 'WebAuth', 'AuthPassword', 'TwoFactorSetup', 'Downloads', 'Project']);

async function call(page, { method, path }, token) {
  return page.evaluate(async ({ url, method, token }) => {
    try {
      const response = await fetch(url, {
        method,
        headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(method === 'GET' || method === 'DELETE' ? {} : { 'Content-Type': 'application/json' }) },
        body: method === 'GET' || method === 'DELETE' ? undefined : '{}',
      });
      await response.body?.cancel().catch(() => {});
      return response.status;
    } catch (error) {
      return `fetch-error ${error.message}`;
    }
  }, { url: apiTarget(path), method, token });
}

const browser = await launchBrowser();
try {
  // X1：每個路由。
  const context = await newContext(browser);
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', (e) => pageErrors.push(e.message));
  await login(page, { account: 'e2e-perm-low', password });
  const deniedBefore = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Permission.Denied'")[0].n;
  const openable = new Set(['/', '/App', '/Profile', '/ChangePassword', '/TwoFactorSetup', '/projects', '/Error', '/not-found']);
  const wrong = [];
  let deniedPages = 0;
  for (const route of authenticatedRoutes) {
    await page.goto(route);
    await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 }).catch(() => {});
    await page.waitForTimeout(1500);
    const denied = (await page.getByText('你沒有權限存取此頁面').count()) > 0 || (await page.getByText('只有管理員').count()) > 0;
    const path = new URL(page.url()).pathname.toLowerCase();
    if (path === '/auths/login') {
      wrong.push(`${route} 被丟回登入頁`);
    } else if (openable.has(route) === denied) {
      wrong.push(`${route} ${denied ? '不該被擋' : '應該被擋卻開得起來'}`);
    }

    if (denied) {
      deniedPages++;
    }
  }

  await screenshot(page, 'explore-perm-X1-最後一頁');
  const deniedAfter = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Permission.Denied'")[0].n;
  recorder.add('X1', '低權限帳號逐一開每個路由：能開的照常、其他顯示沒有權限並寫稽核、沒有頁面錯誤',
    wrong.length === 0 && pageErrors.length === 0 && deniedAfter - deniedBefore >= deniedPages,
    `${authenticatedRoutes.length} 個路由，被擋 ${deniedPages} 個、權限拒絕稽核 +${deniedAfter - deniedBefore}；${wrong.length ? '不符：' + wrong.join('；') : '全部符合'}；頁面錯誤 ${pageErrors.length}${pageErrors.length ? '：' + pageErrors.slice(0, 3).join('；') : ''}`);

  // X2：沒登入呼叫每個端點。
  const anon = [];
  for (const endpoint of allEndpoints) {
    anon.push({ ...endpoint, status: await call(page, endpoint, null) });
  }

  // 429：登入相關端點的限流先於驗證，也算擋下。
  const anonBad = anon.filter((x) => typeof x.status !== 'number' || x.status >= 500 || (!x.anonymous && ![401, 429].includes(x.status)));
  recorder.add('X2', '沒登入呼叫每一個 API 端點：除了允許匿名的一律 401，沒有任何 5xx', anonBad.length === 0,
    `${anon.length} 個端點（允許匿名 ${anon.filter((x) => x.anonymous).length} 個）；不符 ${anonBad.length}${anonBad.length ? '：' + anonBad.map((x) => `${x.method} ${x.path}→${x.status}`).join('；') : ''}`);

  // X3：低權限帳號呼叫每個端點。
  const token = await pageAccessToken(page);
  const low = [];
  for (const endpoint of allEndpoints) {
    low.push({ ...endpoint, status: await call(page, endpoint, token) });
  }

  const lowBad = low.filter((x) => typeof x.status !== 'number' || x.status >= 500
    || (!allowedForLow.has(x.controller) && !x.anonymous && !x.signedIn && ![403, 404].includes(x.status)));
  recorder.add('X3', '低權限帳號呼叫每一個 API 端點：沒有權限的一律 403（或 404），沒有任何 5xx', lowBad.length === 0,
    `${low.length} 個端點；不符 ${lowBad.length}${lowBad.length ? '：' + lowBad.map((x) => `${x.method} ${x.path}→${x.status}`).join('；') : ''}；允許的控制器狀態碼分布 ${JSON.stringify(low.filter((x) => allowedForLow.has(x.controller)).reduce((a, x) => ({ ...a, [x.status]: (a[x.status] ?? 0) + 1 }), {}))}`);
  await context.close();
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
