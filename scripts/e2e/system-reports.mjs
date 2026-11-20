// 系統巡檢報告的實機驗證（0.10.39 起）。用法見同資料夾的 README.md。
// 需要 Start-E2EHost.ps1 的隔離主機（查資料庫用 E2E_DB_FILE）；建議以 EmailSettings__Provider=Pickup 啟動，才能看寄出的信。
//
// R1 「系統巡檢報告」頁打開，清單與工具列都在
// R2 「立即產生」昨天的日報（不勾寄送）：背景產生、資料庫多一份報告、產生者收到「報告已產生」的站內通知、沒有通知其他管理員
// R3 「查看報告」打開內容視窗：最前面是全面摘要（燈號＋一句話結論、下期預測），有磁碟容量表
// R4 網址 ?report={id}（通知與 Email 的連結）直接打開那一份
// R5 舊連結 /admin-assistant?patrolReport={id} 轉到 /system-reports?legacyReport={id}
// R6 「排程作業」頁有「系統巡檢日報」與「容量快照」，沒有「系統管理助理巡檢」
// R7 立即產生寫一筆 Job.Trigger 稽核（via=system-reports）
import { createRecorder, launchBrowser, login, newContext, query, screenshot } from './common.mjs';

const recorder = createRecorder('system-reports-results.json');

async function waitFor(check, timeout = 180000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const value = check();
    if (value) {
      return value;
    }

    if (Date.now() > deadline) {
      return null;
    }

    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

const browser = await launchBrowser();
try {
  const context = await newContext(browser);
  const page = await context.newPage();
  await login(page);

  // R1
  await page.goto('/system-reports');
  await page.locator('.system-report-toolbar').waitFor({ timeout: 60000 });
  const toolbarText = (await page.locator('.system-report-toolbar').textContent()) ?? '';
  recorder.add('R1', '系統巡檢報告頁打開', await page.locator('.system-report-table').count() === 1, toolbarText.replace(/\s+/g, ' ').trim().slice(0, 120));
  await screenshot(page, 'system-reports-list');

  // R2
  const before = query('SELECT COUNT(*) AS N FROM SystemReport')[0].N;
  const supportId = query("SELECT Id FROM MyUser WHERE Account = 'support'")[0].Id;
  await page.locator('button.toolbar-icon-button', { hasText: '立即產生' }).click();
  const generateModal = page.locator('.system-report-generate-modal .ant-modal-content');
  await generateModal.waitFor({ state: 'visible', timeout: 15000 });
  await screenshot(page, 'system-reports-generate');
  await generateModal.locator('button', { hasText: /^\s*產\s*生\s*$/ }).click();
  const created = await waitFor(() => {
    const rows = query('SELECT ReportId, Level, Trigger, DeliveryStatus FROM SystemReport ORDER BY Id DESC LIMIT 1');
    return query('SELECT COUNT(*) AS N FROM SystemReport')[0].N > before ? rows[0] : null;
  });
  const reportId = created?.ReportId;
  const notices = reportId
    ? await waitFor(() => {
      const rows = query("SELECT RecipientUserId, Title FROM Notification WHERE Category = 'SystemReport' AND Link LIKE ?", `%${reportId.replaceAll('-', '').toLowerCase()}%`);
      return rows.length > 0 ? rows : null;
    }, 30000)
    : null;
  recorder.add('R2', '立即產生：背景產生報告，只通知產生者',
    created !== null && created.Trigger === 'Manual' && created.DeliveryStatus === 'NotRequired'
      && notices !== null && notices.length === 1 && notices[0].RecipientUserId === supportId && notices[0].Title.startsWith('報告已產生：'),
    JSON.stringify({ created, notices }));

  // R3
  await page.goto('/system-reports');
  await page.locator('.system-report-table tbody tr.ant-table-row').first().waitFor({ timeout: 60000 });
  await page.locator('.system-report-table tbody tr.ant-table-row').first().locator('button', { hasText: '查看報告' }).click().catch(async () => {
    await page.locator('.system-report-table tbody tr.ant-table-row').first().locator('button').last().click();
  });
  const detail = page.locator('.system-report-modal .ant-modal-content');
  await detail.waitFor({ state: 'visible', timeout: 30000 });
  await page.locator('.system-report-verdict').waitFor({ timeout: 30000 });
  const verdict = ((await page.locator('.system-report-verdict').textContent()) ?? '').trim();
  const bodyText = (await detail.textContent()) ?? '';
  recorder.add('R3', '內容視窗最前面是全面摘要，有下期預測與磁碟容量',
    /燈/.test(verdict) && bodyText.includes('下期預測') && bodyText.includes('磁碟容量') && bodyText.includes('AI 說明'),
    verdict.slice(0, 120));
  await screenshot(page, 'system-reports-detail');

  // R4
  if (reportId) {
    await page.goto(`/system-reports?report=${reportId}`);
    const deep = await page.locator('.system-report-modal .system-report-verdict').waitFor({ timeout: 60000 }).then(() => true).catch(() => false);
    recorder.add('R4', '網址 ?report= 直接打開那一份', deep, reportId);
  } else {
    recorder.add('R4', '網址 ?report= 直接打開那一份', false, '沒有產生報告');
  }

  // R5
  const legacyId = '00000000-0000-0000-0000-000000000001';
  await page.goto(`/admin-assistant?patrolReport=${legacyId}`);
  const redirected = await page.waitForURL(/\/system-reports\?legacyReport=/, { timeout: 60000 }).then(() => true).catch(() => false);
  recorder.add('R5', '舊連結轉到系統巡檢報告的舊版巡檢報告', redirected, page.url());

  // R6
  await page.goto('/scheduled-jobs');
  await page.locator('text=系統巡檢日報').first().waitFor({ timeout: 60000 }).catch(() => null);
  const jobsText = (await page.locator('body').textContent()) ?? '';
  recorder.add('R6', '排程作業有系統巡檢日報與容量快照，沒有系統管理助理巡檢',
    jobsText.includes('系統巡檢日報') && jobsText.includes('容量快照') && !jobsText.includes('系統管理助理巡檢'),
    '');
  await screenshot(page, 'system-reports-jobs');

  // R7
  const audits = query("SELECT Detail FROM AuditLog WHERE Action = 'Job.Trigger' AND TargetId = 'SystemReportDaily' ORDER BY Id DESC LIMIT 1");
  recorder.add('R7', '立即產生寫 Job.Trigger 稽核', audits.length === 1 && audits[0].Detail.includes('via=system-reports'), audits[0]?.Detail ?? '沒有稽核');

  await context.close();
} finally {
  await browser.close();
}

process.exitCode = recorder.save() ? 0 : 1;
