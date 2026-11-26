// 前端診斷記錄的實機驗證（0.10.37 起）。用法見同資料夾的 README.md。
// 需要 Start-E2EHost.ps1 的隔離主機：建一般帳號、查稽核（E2E_DB_FILE）與日誌檔（同一個隔離資料夾的 logs）。
//
// D1 管理員在「日誌等級設定」頁對一般帳號開啟前端診斷（TRACE、15 分鐘），清單列出這一筆
// D2 那位使用者畫面底部出現「系統管理員已開啟診斷記錄（至 HH:mm）」
// D3 他換頁後，日誌檔出現 MyProject.Browser.* 的 TRACE（換頁、API 呼叫）
// D4 另一位使用者（support）的前端日誌不會送來
// D5 管理員按「停止」：清單清空；那位使用者下一次呼叫 API 後提示列消失，之後的操作不再送來
// D6 開啟與關閉各有一筆稽核
import { allLogText, createRecorder, createUser, launchBrowser, login, newContext, query, screenshot, waitForPredicate, waitForValue } from './common.mjs';

const recorder = createRecorder('client-diagnostics-results.json');
const account = `diag-${Date.now().toString(36)}`;
const password = 'Diag-Pass-2026!';

let logStart = allLogText().length;
const logText = () => allLogText().slice(logStart);

const browserLines = (text, who) => text.split('\n').filter((line) => line.includes('|MyProject.Browser.') && line.includes(`Account=${who},`));

createUser(account, password);
const browser = await launchBrowser();
try {
  // D1：管理員開啟。
  const adminContext = await newContext(browser);
  const admin = await adminContext.newPage();
  await login(admin);
  await admin.goto('/log-level-setting');
  await admin.locator('.client-diagnostics').waitFor({ timeout: 60000 });
  await admin.fill('.client-diagnostics input', account);
  await admin.locator('.client-diagnostics label:has-text("TRACE")').click();
  await admin.locator('.client-diagnostics-form').locator('.ant-select').click();
  await admin.locator('.ant-select-item-option:has-text("15 分鐘")').click();
  await admin.locator('.client-diagnostics button:has-text("開始前端診斷記錄")').click();
  const row = admin.locator('.client-diagnostics-table tbody tr', { hasText: account });
  await row.waitFor({ timeout: 30000 });
  const rowText = (await row.textContent()) ?? '';
  recorder.add('D1', '管理員開啟前端診斷，清單列出這一筆', rowText.includes('TRACE') && rowText.includes('support'), rowText.replace(/\s+/g, ' ').trim());
  await screenshot(admin, 'client-diagnostics-admin');

  // D2：那位使用者看到提示列（登入時的 API 回應就帶標頭）。
  const userContext = await newContext(browser);
  const user = await userContext.newPage();
  await login(user, { account, password });
  const bar = user.locator('.app-diagnostics-bar');
  await bar.waitFor({ timeout: 60000 }).catch(() => null);
  const barText = (await bar.count()) > 0 ? ((await bar.textContent()) ?? '').trim() : '';
  recorder.add('D2', '被診斷的使用者看到提示列', /^系統管理員已開啟診斷記錄（至 \d{2}:\d{2}）/.test(barText), barText || '沒有提示列');
  await screenshot(user, 'client-diagnostics-user');

  // D3：換頁後，前端日誌（TRACE）寫進伺服器的日誌檔。
  await user.evaluate(() => window.Blazor.navigateTo('/Profile'));
  const forwarded = await waitForValue(() => {
    const lines = browserLines(logText(), account);
    return lines.some((x) => x.includes('|TRACE|') && x.includes('Navigated. Path=/Profile'))
      && lines.some((x) => x.includes('|TRACE|') && x.includes('API call completed.')) ? lines : null;
  }, 60000);
  recorder.add('D3', '換頁與 API 呼叫以 TRACE 寫進日誌檔（MyProject.Browser.*）', forwarded !== null,
    forwarded ? `${forwarded.length} 行，例：${forwarded.find((x) => x.includes('Navigated'))?.slice(0, 200)}` : '找不到日誌');

  // D4：support（沒開啟）的前端日誌不會送來。
  await admin.evaluate(() => window.Blazor.navigateTo('/Profile'));
  await admin.waitForTimeout(8000);
  const supportLines = browserLines(logText(), 'support');
  recorder.add('D4', '沒開啟的使用者不送前端日誌', supportLines.length === 0, `${supportLines.length} 行`);

  // D5：管理員停止。
  await admin.goto('/log-level-setting');
  await admin.locator('.client-diagnostics').waitFor({ timeout: 60000 });
  await admin.locator(`.client-diagnostics button:has-text("停止 ${account} 的前端診斷記錄")`).click();
  await admin.locator('.client-diagnostics-empty').waitFor({ timeout: 30000 });
  await user.evaluate(() => window.Blazor.navigateTo('/App'));
  const hidden = await waitForPredicate(user, () => document.querySelector('.app-diagnostics-bar') === null, null, { timeout: 30000 }).then(() => true).catch(() => false);
  await user.waitForTimeout(6000);   // 已經在路上的那一批送完
  logStart = allLogText().length;
  await user.evaluate(() => window.Blazor.navigateTo('/Profile'));
  await user.waitForTimeout(10000);
  const afterStop = browserLines(logText(), account);
  recorder.add('D5', '停止後提示列消失、不再送前端日誌', hidden && afterStop.length === 0, JSON.stringify({ hidden, linesAfterStop: afterStop.length }));

  // D6：稽核。
  const audits = query("SELECT Action, Detail FROM AuditLog WHERE Action LIKE 'LogLevel.ClientDiagnostics%' AND Detail LIKE ?", `%${account}%`);
  recorder.add('D6', '開啟與關閉各有一筆稽核',
    audits.some((x) => x.Action === 'LogLevel.ClientDiagnosticsStart') && audits.some((x) => x.Action === 'LogLevel.ClientDiagnosticsStop'),
    JSON.stringify(audits.map((x) => x.Action)));

  await userContext.close();
  await adminContext.close();
} finally {
  await browser.close();
}

process.exitCode = recorder.save() ? 0 : 1;
