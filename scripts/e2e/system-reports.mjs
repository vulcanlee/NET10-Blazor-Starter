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
// R8 報告視窗按「下載 PDF」得到 PDF 檔（0.10.40 起），寫一筆 SystemReport.Download 稽核
// R9 「立即產生」勾選寄給所有管理員：Pickup 的信附上 PDF，寄信紀錄的種類是 SystemReport（0.10.40 起）
// R10 週報（0.10.41 起）：先備份一次，「立即產生」選週報，產生上週的週報，內容有趨勢比較、容量成長、備份可還原驗證（通過）
// R11 月報（0.10.41 起）：產生最近一個季末月份的月報並寄給管理員：有 AI 費用與帳號與權限清查（表格列出 support），信件附 monthly 的 PDF
// R12 立即產生的檢查：這一週的週報、這個月的月報都回 400（還沒過完）
import { existsSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { apiJsonAsPageUser, createRecorder, databaseFile, launchBrowser, login, newContext, outDir, query, screenshot } from './common.mjs';

const mailDir = process.env.E2E_MAIL_DIR ?? join(dirname(dirname(databaseFile)), 'Mails');
const mailFiles = () => (existsSync(mailDir) ? readdirSync(mailDir).filter((x) => x.endsWith('.eml')) : []);

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

  // R8
  if (reportId) {
    await page.goto(`/system-reports?report=${reportId}`);
    await page.locator('.system-report-modal .system-report-verdict').waitFor({ timeout: 60000 });
    const downloadPromise = page.waitForEvent('download', { timeout: 60000 });
    await page.locator('.system-report-modal button.toolbar-icon-button', { hasText: '下載 PDF' }).click();
    const download = await downloadPromise;
    const pdfPath = join(outDir, download.suggestedFilename());
    await download.saveAs(pdfPath);
    const head = readFileSync(pdfPath).subarray(0, 5).toString('ascii');
    const downloads = query("SELECT COUNT(*) AS N FROM AuditLog WHERE Action = 'SystemReport.Download' AND TargetId = ?", reportId.replaceAll('-', '').toLowerCase())[0].N;
    recorder.add('R8', '下載 PDF：得到 PDF 檔並寫稽核',
      head === '%PDF-' && /^system-report-daily-\d{8}\.pdf$/.test(download.suggestedFilename()) && downloads >= 1,
      JSON.stringify({ file: download.suggestedFilename(), head, downloads, saved: pdfPath }));
  } else {
    recorder.add('R8', '下載 PDF：得到 PDF 檔並寫稽核', false, '沒有產生報告');
  }

  // R9
  const mailsBefore = new Set(mailFiles());
  await page.goto('/system-reports');
  await page.locator('.system-report-toolbar').waitFor({ timeout: 60000 });
  await page.locator('button.toolbar-icon-button', { hasText: '立即產生' }).click();
  const sendModal = page.locator('.system-report-generate-modal .ant-modal-content');
  await sendModal.waitFor({ state: 'visible', timeout: 15000 });
  await sendModal.locator('.ant-checkbox-wrapper', { hasText: '寄給所有管理員' }).click();
  await sendModal.locator('button', { hasText: /^\s*產\s*生\s*$/ }).click();
  const newMail = await waitFor(() => mailFiles().find((x) => !mailsBefore.has(x) && readFileSync(join(mailDir, x), 'utf8').includes('application/pdf')) ?? null);
  const mailText = newMail ? readFileSync(join(mailDir, newMail), 'utf8') : '';
  const outbox = query("SELECT Kind, Status FROM EmailOutbox ORDER BY Id DESC LIMIT 1")[0];
  recorder.add('R9', '勾選寄送：信件附上 PDF，寄信紀錄種類是 SystemReport',
    newMail !== null && /filename="?system-report-daily-\d{8}\.pdf/.test(mailText) && outbox?.Kind === 'SystemReport',
    JSON.stringify({ mail: newMail, outbox }));

  // R10
  const ymd = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  await apiJsonAsPageUser(page, '/api/v1/backups/run', { method: 'POST', body: {} });
  await waitFor(() => query("SELECT COUNT(*) AS N FROM JobRun WHERE JobName = 'SystemBackup' AND Status = 'Succeeded'")[0].N > 0 || null);
  await page.goto('/system-reports');
  await page.locator('.system-report-toolbar').waitFor({ timeout: 60000 });
  await page.locator('button.toolbar-icon-button', { hasText: '立即產生' }).click();
  const weeklyModal = page.locator('.system-report-generate-modal .ant-modal-content');
  await weeklyModal.waitFor({ state: 'visible', timeout: 15000 });
  await weeklyModal.locator('.ant-radio-wrapper', { hasText: '週報' }).click();
  const weeklyLabel = (await weeklyModal.locator('label[for="system-report-generate-date"]').textContent()) ?? '';
  await screenshot(page, 'system-reports-generate-weekly');
  await weeklyModal.locator('button', { hasText: /^\s*取\s*消\s*$/ }).click();
  const lastWeek = new Date();
  lastWeek.setDate(lastWeek.getDate() - 7);
  const weeklyCall = await apiJsonAsPageUser(page, '/api/v1/system-reports/generate', { method: 'POST', body: { frequency: 'Weekly', date: ymd(lastWeek), sendToAdmins: false } });
  const weekly = await waitFor(() => query("SELECT ReportId FROM SystemReport WHERE Frequency = 'Weekly' ORDER BY Id DESC LIMIT 1")[0] ?? null, 600000);
  let weeklyText = '';
  if (weekly) {
    const id = weekly.ReportId.replaceAll('-', '').toLowerCase();
    await page.goto(`/system-reports?report=${id}`);
    await page.locator('.system-report-modal .system-report-verdict').waitFor({ timeout: 60000 });
    weeklyText = (await page.locator('.system-report-modal').textContent()) ?? '';
    await screenshot(page, 'system-reports-weekly');
  }

  recorder.add('R10', '週報：趨勢比較、容量成長、備份可還原驗證（通過）',
    weeklyLabel.includes('那一週') && weeklyCall.status === 200 && weekly !== null
      && ['本期重點', '趨勢比較', '容量成長與規劃', '備份可還原驗證', '可以還原'].every((x) => weeklyText.includes(x)),
    JSON.stringify({ weeklyLabel, status: weeklyCall.status, verify: (weeklyText.match(/最新的備份[^。]*。/) ?? [''])[0] }));

  // R11
  const now = new Date();
  let quarterEnd = new Date(now.getFullYear(), now.getMonth() - 1, 1);
  while ((quarterEnd.getMonth() + 1) % 3 !== 0) {
    quarterEnd = new Date(quarterEnd.getFullYear(), quarterEnd.getMonth() - 1, 1);
  }

  const monthlyMailsBefore = new Set(mailFiles());
  const monthlyCall = await apiJsonAsPageUser(page, '/api/v1/system-reports/generate', { method: 'POST', body: { frequency: 'Monthly', date: ymd(quarterEnd), sendToAdmins: true } });
  const monthly = await waitFor(() => query("SELECT ReportId, DeliveryStatus FROM SystemReport WHERE Frequency = 'Monthly' ORDER BY Id DESC LIMIT 1")[0] ?? null, 600000);
  let monthlyText = '';
  let monthlyPdf = '';
  if (monthly) {
    const id = monthly.ReportId.replaceAll('-', '').toLowerCase();
    await page.goto(`/system-reports?report=${id}`);
    await page.locator('.system-report-modal .system-report-verdict').waitFor({ timeout: 60000 });
    monthlyText = (await page.locator('.system-report-modal').textContent()) ?? '';
    await screenshot(page, 'system-reports-monthly');
    const downloadPromise = page.waitForEvent('download', { timeout: 60000 });
    await page.locator('.system-report-modal button.toolbar-icon-button', { hasText: '下載 PDF' }).click();
    const download = await downloadPromise;
    monthlyPdf = join(outDir, download.suggestedFilename());
    await download.saveAs(monthlyPdf);
  }

  const monthlyMail = await waitFor(() => mailFiles().find((x) => !monthlyMailsBefore.has(x) && readFileSync(join(mailDir, x), 'utf8').includes('system-report-monthly-')) ?? null);
  recorder.add('R11', '月報：AI 費用與帳號清查、寄出附 monthly PDF',
    monthlyCall.status === 200 && monthly !== null && ['AI 費用', '帳號與權限清查', '費用最高的', '管理員', 'support'].every((x) => monthlyText.includes(x)) && monthlyMail !== null,
    JSON.stringify({ quarterEnd: ymd(quarterEnd), status: monthlyCall.status, delivery: monthly?.DeliveryStatus, mail: monthlyMail, pdf: monthlyPdf }));

  // R12
  const thisWeek = await apiJsonAsPageUser(page, '/api/v1/system-reports/generate', { method: 'POST', body: { frequency: 'Weekly', date: ymd(now), sendToAdmins: false } });
  const thisMonth = await apiJsonAsPageUser(page, '/api/v1/system-reports/generate', { method: 'POST', body: { frequency: 'Monthly', date: ymd(now), sendToAdmins: false } });
  recorder.add('R12', '這一週的週報、這個月的月報不能產生（400）',
    thisWeek.status === 400 && thisMonth.status === 400,
    JSON.stringify({ week: [thisWeek.status, thisWeek.json?.message], month: [thisMonth.status, thisMonth.json?.message] }));
  writeFileSync(join(outDir, 'system-reports-monthly.txt'), monthlyText);

  await context.close();
} finally {
  await browser.close();
}

process.exitCode = recorder.save() ? 0 : 1;
