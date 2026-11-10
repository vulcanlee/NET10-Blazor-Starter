// WASM 遷移 M1（0.10.0）的實機情境：網頁 Cookie 換 Token、刷新輪替與重用偵測、登出撤銷、
// 強制改密碼期間擋 API、改密碼後舊登入立即失效、登入 Token 清理排程，最後跑全部路由的煙霧測試。
// 用法見同資料夾的 README.md。結果寫到 E2E_OUT_DIR/m01-results.json 與 smoke-results.json，截圖在 screenshots/。
import { callApi, createRecorder, createUser, execute, launchBrowser, login, newContext, query, screenshot } from './common.mjs';
import { runSmoke } from './smoke-routes.mjs';

const clientHeader = { 'X-MyProject-Client': '1' };
const recorder = createRecorder('m01-results.json');
const smoke = createRecorder('smoke-results.json');
const browser = await launchBrowser();

async function bridge(page, headers = clientHeader) {
  return callApi(page, '/api/v1/Auth/bridge', { method: 'POST', headers });
}

async function refresh(page, refreshToken) {
  return callApi(page, '/api/v1/Auth/refresh', { method: 'POST', body: { refreshToken } });
}

function auditCount(action, since) {
  return query('SELECT COUNT(*) AS n FROM AuditLog WHERE Action = ? AND OccurredAt >= ?', action, since)[0].n;
}

try {
  // EF Core 在 SQLite 以「yyyy-MM-dd HH:mm:ss.fffffff」存時間（UTC），比較前換成同樣的格式。
  const startedAt = new Date().toISOString().replace('T', ' ').replace('Z', '');

  // S1：勾「記住我」登入，Cookie 換出的 Token 標為持久（前端存 localStorage）。
  const persistent = await newContext(browser);
  const persistentPage = await persistent.newPage();
  await login(persistentPage, { rememberMe: true });
  const s1 = await bridge(persistentPage);
  recorder.add('S1', '勾「記住我」登入後以 Cookie 換 Token', s1.status === 200 && s1.json?.data?.isPersistent === true,
    `HTTP ${s1.status}，isPersistent=${s1.json?.data?.isPersistent}，帳號=${s1.json?.data?.user?.account}`);

  // S2：不勾「記住我」，Token 標為非持久（前端存 sessionStorage）。
  const session = await newContext(browser);
  const sessionPage = await session.newPage();
  await login(sessionPage);
  const s2 = await bridge(sessionPage);
  recorder.add('S2', '不勾「記住我」登入後以 Cookie 換 Token', s2.status === 200 && s2.json?.data?.isPersistent === false,
    `HTTP ${s2.status}，isPersistent=${s2.json?.data?.isPersistent}`);
  const rows = query("SELECT ClientKind, IsPersistent, LoginSessionId FROM RefreshSession WHERE ClientKind = 'web-bridge' ORDER BY Id");
  recorder.add('S2a', '資料庫只存雜湊，且記下登入工作階段', rows.length === 2 && rows.every((x) => x.LoginSessionId) && rows[0].IsPersistent === 1 && rows[1].IsPersistent === 0,
    `web-bridge 紀錄 ${rows.length} 筆，持久旗標 ${rows.map((x) => x.IsPersistent).join('/')}`);

  // S3：不帶自訂標頭被拒（CSRF 防護）。
  const s3 = await bridge(sessionPage, {});
  recorder.add('S3', 'bridge 不帶 X-MyProject-Client 標頭', s3.status === 400, `HTTP ${s3.status}，${s3.json?.message ?? ''}`);

  // S4：刷新一次成功並換新；寬限期內重送舊的（同時刷新、回應遺失）回 401 但不撤銷，新的照常可用；
  // 超過寬限期（30 秒）再用舊的，判定為重用、整串撤銷（新的也失效），稽核留下 Token.ReuseDetected。
  const first = s2.json.data.refreshToken;
  const rotated = await refresh(sessionPage, first);
  const quickReplay = await refresh(sessionPage, first);
  const rotatedAgain = await refresh(sessionPage, rotated.json?.data?.refreshToken ?? '');
  recorder.add('S4a', '刷新輪替；寬限期內重送舊的不撤銷',
    rotated.status === 200 && rotated.json.data.refreshToken !== first && quickReplay.status === 401 && rotatedAgain.status === 200,
    `第一次刷新 HTTP ${rotated.status}（換到新 Token）；立刻重送舊的 HTTP ${quickReplay.status}；新的仍可刷新 HTTP ${rotatedAgain.status}`);
  await sessionPage.waitForTimeout(31000);
  const lateReplay = await refresh(sessionPage, first);
  const afterReplay = await refresh(sessionPage, rotatedAgain.json?.data?.refreshToken ?? '');
  const reuseAudits = auditCount('Token.ReuseDetected', startedAt);
  recorder.add('S4b', '超過寬限期重用舊 Token，整串撤銷',
    lateReplay.status === 401 && afterReplay.status === 401 && reuseAudits >= 1,
    `31 秒後再用最早的 Token HTTP ${lateReplay.status}；同一串最新的 Token 也 HTTP ${afterReplay.status}；稽核 Token.ReuseDetected ${reuseAudits} 筆`);

  // S7：從網頁登出，這次登入換出的 Token 一併撤銷。
  const s7Tokens = (await bridge(sessionPage)).json.data;
  await sessionPage.goto('/Auths/Logout');
  await sessionPage.waitForURL((url) => url.pathname.startsWith('/Auths/Login'), { timeout: 30000 });
  const s7 = await refresh(sessionPage, s7Tokens.refreshToken);
  const webLogout = query("SELECT COUNT(*) AS n FROM RefreshSession WHERE RevokedReason = 'WebLogout'")[0].n;
  recorder.add('S7', '從網頁登出後，換出的刷新 Token 失效', s7.status === 401 && webLogout >= 1,
    `登出後刷新 HTTP ${s7.status}；撤銷原因 WebLogout 的紀錄 ${webLogout} 筆`);
  await session.close();

  // S9：排程作業頁出現「登入 Token 清理」並可立即執行；資料保留頁有「登入 Token」一列。
  await persistentPage.goto('/scheduled-jobs');
  const jobRow = persistentPage.locator('tr', { hasText: '登入 Token 清理' });
  await jobRow.first().waitFor({ timeout: 30000 });
  await jobRow.first().getByRole('button', { name: '立即執行' }).click();
  await persistentPage.locator('.ant-modal').getByRole('button', { name: '立即執行' }).click();
  let runs = [];
  for (let i = 0; i < 20 && runs.length === 0; i++) {
    await persistentPage.waitForTimeout(500);
    runs = query("SELECT Status, Message FROM JobRun WHERE JobName = 'RefreshSessionRetention' AND FinishedAtUtc IS NOT NULL");
  }
  await persistentPage.waitForTimeout(1500);
  await screenshot(persistentPage, 'S9-scheduled-jobs');
  await persistentPage.goto('/data-retention');
  const retentionRow = persistentPage.locator('tr', { hasText: '登入 Token' });
  await retentionRow.first().waitFor({ timeout: 30000 });
  await screenshot(persistentPage, 'S9-data-retention');
  recorder.add('S9', '排程與資料保留頁出現登入 Token 清理', runs.length === 1 && (await retentionRow.count()) >= 1,
    `立即執行結果：${runs.map((x) => `${x.Status}「${x.Message}」`).join('、') || '沒有執行紀錄'}；資料保留頁列數 ${await retentionRow.count()}`);

  // 一般帳號（support 不受「必須改密碼」規則約束，也不能在網頁改自己的密碼，所以 S5、S6、S8 用另一個帳號）。
  const userAccount = 'e2e-user';
  const userPassword = 'E2eUser-pass1';
  createUser(userAccount, userPassword);

  // S8：被要求改密碼的帳號，以 API 呼叫業務端點得到 403 與錯誤代碼；/me 仍可用；解除後恢復。
  execute('UPDATE MyUser SET MustChangePassword = 1 WHERE Account = ?', userAccount);
  const apiLogin = await callApi(persistentPage, '/api/v1/Auth/login', { method: 'POST', body: { account: userAccount, password: userPassword } });
  const bearer = { Authorization: `Bearer ${apiLogin.json?.data?.accessToken}` };
  const blocked = await callApi(persistentPage, '/api/v1/Category/search', { method: 'POST', headers: bearer, body: { pageIndex: 1, pageSize: 10 } });
  const me = await callApi(persistentPage, '/api/v1/Auth/me', { headers: bearer });
  execute('UPDATE MyUser SET MustChangePassword = 0 WHERE Account = ?', userAccount);
  const released = await callApi(persistentPage, '/api/v1/Category/search', { method: 'POST', headers: bearer, body: { pageIndex: 1, pageSize: 10 } });
  recorder.add('S8', '強制改密碼期間擋下業務 API',
    apiLogin.status === 200 && blocked.status === 403 && blocked.json?.errorCode === 'PASSWORD_CHANGE_REQUIRED'
      && me.status === 200 && me.json?.data?.mustChangePassword === true && released.status !== 403,
    `API 登入 HTTP ${apiLogin.status}；業務 API HTTP ${blocked.status}（${blocked.json?.errorCode}）；/me HTTP ${me.status}（mustChangePassword=${me.json?.data?.mustChangePassword}）；解除後業務 API HTTP ${released.status}（一般帳號沒有分類權限時為 403 但不帶錯誤代碼，這裡只確認不再被強制設定擋下）`);

  // S10：全部路由煙霧測試（support）。
  await runSmoke(browser, smoke);

  // S5：一般帳號在網頁登入、換出 Token 後，自己在「變更密碼」頁改密碼 → 舊的刷新 Token 被拒。
  const user = await newContext(browser);
  const userPage = await user.newPage();
  await login(userPage, { account: userAccount, password: userPassword });
  const beforeChange = (await bridge(userPage)).json.data;
  await userPage.goto('/ChangePassword');
  const newPassword = 'E2eUser-pass2';
  await userPage.getByPlaceholder('請輸入目前密碼').fill(userPassword);
  await userPage.getByPlaceholder('請輸入新密碼').fill(newPassword);
  await userPage.getByPlaceholder('請再次輸入新密碼').fill(newPassword);
  await userPage.getByRole('button', { name: /儲\s*存/ }).click();
  let changed = false;
  for (let i = 0; i < 40 && !changed; i++) {
    await userPage.waitForTimeout(500);
    changed = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Password.Changed' AND OccurredAt >= ?", startedAt)[0].n > 0;
  }
  await userPage.waitForTimeout(3000);
  const s5 = await refresh(userPage, beforeChange.refreshToken);
  const s5Audit = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Token.RefreshFailed' AND Detail = 'reason=SessionRevoked' AND OccurredAt >= ?", startedAt)[0].n;
  recorder.add('S5', '改密碼後舊的刷新 Token 被拒', changed && s5.status === 401 && s5Audit >= 1,
    `密碼已變更=${changed}；舊刷新 Token HTTP ${s5.status}；稽核 Token.RefreshFailed（SessionRevoked）${s5Audit} 筆`);

  // S6：同一個帳號改完密碼後重新換 Token 並用過一次（工作階段狀態進了快取）；管理員在「使用者管理」按「強制登出」，
  // 對方的存取 Token 下一個請求就被拒、網頁換頁就回到登入頁（以前最多還能用 5 分鐘）。
  const afterChange = (await bridge(userPage)).json.data;
  const userBearer = { Authorization: `Bearer ${afterChange?.accessToken}` };
  const beforeForce = await callApi(userPage, '/api/v1/Auth/me', { headers: userBearer });
  await persistentPage.goto('/myusers');
  const userRow = persistentPage.locator('tr', { hasText: userAccount });
  await userRow.first().waitFor({ timeout: 30000 });
  await userRow.first().getByRole('button', { name: '強制登出' }).click();
  await persistentPage.locator('.ant-modal').getByRole('button', { name: '強制登出' }).click();
  let forced = false;
  for (let i = 0; i < 40 && !forced; i++) {
    await persistentPage.waitForTimeout(250);
    forced = query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'User.ForceLogout' AND OccurredAt >= ?", startedAt)[0].n > 0;
  }
  const afterForce = await callApi(userPage, '/api/v1/Auth/me', { headers: userBearer });
  await screenshot(persistentPage, 'S6-force-logout');
  await userPage.goto('/App');
  await userPage.waitForTimeout(3000);
  const kickedOut = new URL(userPage.url()).pathname.startsWith('/Auths/Login');
  await screenshot(userPage, 'S6-kicked-out');
  recorder.add('S6', '管理員強制登出後對方立即失效（快取修正）', forced && beforeForce.status === 200 && afterForce.status === 401 && kickedOut,
    `強制登出已執行=${forced}；之前存取 Token HTTP ${beforeForce.status}，之後立刻 HTTP ${afterForce.status}；對方網頁換頁後${kickedOut ? '回到登入頁' : `停在 ${userPage.url()}`}`);
  await user.close();
  await persistent.close();
} finally {
  await browser.close();
}

const ok = recorder.save() & smoke.save();
process.exit(ok ? 0 : 1);
