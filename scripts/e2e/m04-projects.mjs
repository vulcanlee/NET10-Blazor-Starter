// WASM 遷移 M4（0.10.3）的實機情境：專案頁在 WebAssembly 執行，資料走 Web API（與畫面共用 ProjectService）；
// 附件由瀏覽器直接上傳（不經 WASM 記憶體），下載改用一次性票證。
// 完整流程與多檔上傳、下載內容比對、票證重放與範圍外、約 200 MB 的大檔、超過 1 GB 被擋、修改時移除附件、
// 永久刪除後實體檔移除、重名、兩個分頁同時修改、唯讀角色、匯出、批次刪除、異動歷程、資料保留頁的「前往」、
// Server／WASM 切換、沒有權限開頁的稽核，最後跑全部路由的煙霧測試。
// 用法見同資料夾的 README.md。結果寫到 E2E_OUT_DIR/m04-results.json 與 smoke-results.json。
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import {
  apiAsPageUser, createRecorder, createScopedUser, databaseFile, execute, isWasmPage, launchBrowser, login, newContext, outDir, query, screenshot,
} from './common.mjs';
import {
  clickRowAction, clickToolbar, closeForm, closeToasts, confirm, confirmIfShown, deleteRecord, formField, openList, purgeRecord,
  restoreRecord, rowTexts, submitForm, waitForToast, watchErrors,
} from './crud-ui.mjs';
import { readerAccount, readerPassword } from './m03-views.mjs';
import { seedProjects } from './m04-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const recorder = createRecorder('m04-results.json');
const smoke = createRecorder('smoke-results.json');
const projectFileRoot = join(dirname(dirname(databaseFile)), 'ProjectFile');
const filesDir = join(outDir, 'upload-files');
mkdirSync(filesDir, { recursive: true });

const sha256 = (path) => createHash('sha256').update(readFileSync(path)).digest('hex');

/** 準備要上傳的檔案（內容含時間戳，每次執行都不同）。 */
function makeFile(name, content) {
  const path = join(filesDir, name);
  writeFileSync(path, content);
  return path;
}

/**
 * 按確認窗的「儲存」；未指定團隊時緊接著會跳「確認團隊設定」，按「仍要儲存」。
 * 兩個確認窗前後緊接（專案頁沒有存檔前的伺服器檢查），不能用共用的 confirm() 等第一個關掉。
 */
async function confirmSave(page) {
  await page.locator('.ant-modal-confirm .ant-modal-confirm-btns button', { hasText: /^\s*儲\s*存\s*$/ }).last().click();
  const team = page.locator('.ant-modal-confirm', { hasText: '確認團隊設定' });
  try {
    await team.waitFor({ state: 'visible', timeout: 2000 });
  } catch {
    return;
  }

  await team.locator('.ant-modal-confirm-btns button', { hasText: '仍要儲存' }).click();
  await team.waitFor({ state: 'hidden', timeout: 15000 });
}

/** 某個專案目前的附件（檔名、大小、實體檔路徑）。 */
function filesOf(title) {
  return query(
    `SELECT f.Id, f.OriginalFileName, f.FileSize, f.RelativePath FROM ProjectFile f JOIN Project p ON p.Id = f.ProjectId WHERE p.Title = ? ORDER BY f.Id`,
    title).map((x) => ({ ...x, path: join(projectFileRoot, ...x.RelativePath.split('/')) }));
}

/** 開新增窗、填欄位、選檔、儲存（未指定團隊時按「仍要儲存」）。 */
async function addProject(page, title, filePaths = [], { expect = '新增成功' } = {}) {
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor({ state: 'visible', timeout: 15000 });
  await formField(page, '標題').fill(title);
  await formField(page, '負責人').fill('實機測試');
  if (filePaths.length > 0) {
    await page.locator('.form-modal input[type=file]').setInputFiles(filePaths);
  }

  await submitForm(page);
  await confirmSave(page);
  return waitForToast(page, expect, 300000);
}

/** 在修改窗裡點附件連結下載，回傳下載的檔案與網址。 */
async function downloadFromModal(page, fileName) {
  const [download] = await Promise.all([
    page.waitForEvent('download', { timeout: 120000 }),
    page.locator('.form-modal a', { hasText: fileName }).click(),
  ]);
  const saved = join(outDir, `dl-${Date.now()}-${download.suggestedFilename()}`);
  await download.saveAs(saved);
  return { saved, url: download.url(), suggested: download.suggestedFilename() };
}

seedProjects();
createScopedUser('e2e-sales', 'Sales!E2e2026x', 'E2E 業務專案檢視', ['專案項目:view'], ['E2E 業務部']);
createScopedUser('e2e-catonly', 'CatOnly!E2e2026', 'E2E 只看分類', ['分類清單:view']);

const browser = await launchBrowser();
try {
  const startedAt = new Date().toISOString().replace('T', ' ').replace('Z', '');
  const ctx = await newContext(browser);
  const page = await ctx.newPage();
  const errors = watchErrors(page);
  await login(page);

  // P1：完整流程（WASM）：帶兩個附件新增、開修改窗看附件、下載比對內容。
  await openList(page, '/projects');
  const wasm = await isWasmPage(page);
  const stamp = Date.now() % 100000;
  const title = `E2E 附件專案${stamp}`;
  const specPath = makeFile(`規格${stamp}.txt`, `第一份內容 ${stamp}\n`.repeat(100));
  const quotePath = makeFile(`報價${stamp}.pdf`, `%PDF-1.4 假的報價 ${stamp}`);
  const added = await addProject(page, title, [specPath, quotePath]);
  const stored = filesOf(title);
  recorder.add('P1', '帶兩個附件新增（瀏覽器直接上傳）', wasm && stored.length === 2 && stored.every((x) => existsSync(x.path)),
    `WASM=${wasm}；通知「${added.replace(/\s+/g, ' ').replace('系統訊息', '').trim()}」；資料庫 ${stored.length} 個附件、實體檔都在`);

  // P2：下載內容與上傳一致；同一張票證再用一次得 404。
  await clickRowAction(page, title, '修改');
  await page.locator('.form-modal').waitFor();
  await page.locator('.form-modal a', { hasText: `規格${stamp}.txt` }).waitFor();
  await screenshot(page, 'm04-p2-修改窗附件');
  const dl = await downloadFromModal(page, `規格${stamp}.txt`);
  const sameContent = sha256(dl.saved) === sha256(specPath);
  const replay = await page.request.get(dl.url);
  recorder.add('P2', '下載內容與上傳一致、票證只能用一次', sameContent && dl.suggested === `規格${stamp}.txt` && replay.status() === 404,
    `下載檔名「${dl.suggested}」、SHA-256 相同=${sameContent}；網址 ${new URL(dl.url).pathname.slice(0, 22)}…；再用一次回 ${replay.status()}`);

  // P3：修改時移除一個附件、加一個新的：實體檔跟著刪除。
  const removedPath = stored.find((x) => x.OriginalFileName.startsWith('規格')).path;
  await page.locator('.form-modal .list-group-item', { hasText: `規格${stamp}.txt` }).locator('button.crud-action-button').click();
  const notePath = makeFile(`會議${stamp}.txt`, `會議紀錄 ${stamp}`);
  await page.locator('.form-modal input[type=file]').setInputFiles([notePath]);
  await page.locator('.form-modal .list-group-item', { hasText: `會議${stamp}.txt` }).waitFor();
  await submitForm(page);
  await confirmSave(page);
  await waitForToast(page, '修改成功');
  const afterEdit = filesOf(title).map((x) => x.OriginalFileName).sort();
  recorder.add('P3', '修改時移除附件、加新附件', !existsSync(removedPath) && afterEdit.join() === [`會議${stamp}.txt`, `報價${stamp}.pdf`].sort().join(),
    `附件剩 ${afterEdit.join('、')}；移除的實體檔已刪除=${!existsSync(removedPath)}`);
  await closeToasts(page);

  // P4：約 200 MB 的附件（超過 IIS／Kestrel 預設的 30 MB；檔案不經過 WASM 記憶體）。
  const bigTitle = `E2E 大檔專案${stamp}`;
  const bigPath = join(filesDir, `大檔${stamp}.zip`);
  writeFileSync(bigPath, Buffer.alloc(200 * 1024 * 1024, stamp % 251));
  const bigStart = Date.now();
  await addProject(page, bigTitle, [bigPath]);
  const bigSeconds = ((Date.now() - bigStart) / 1000).toFixed(1);
  const bigStored = filesOf(bigTitle)[0];
  const heap = await page.evaluate(() => performance.memory?.usedJSHeapSize ?? 0);
  await clickRowAction(page, bigTitle, '修改');
  await page.locator('.form-modal a', { hasText: `大檔${stamp}.zip` }).waitFor();
  const bigDl = await downloadFromModal(page, `大檔${stamp}.zip`);
  const bigSame = statSync(bigDl.saved).size === statSync(bigPath).size && sha256(bigDl.saved) === sha256(bigPath);
  await closeForm(page);
  recorder.add('P4', '約 200 MB 的附件上傳與下載', bigStored?.FileSize === 200 * 1024 * 1024 && bigSame,
    `上傳＋存檔 ${bigSeconds} 秒；資料庫大小 ${bigStored?.FileSize}；下載後 SHA-256 相同=${bigSame}；頁面 JS heap ${(heap / 1048576).toFixed(0)} MB`);

  // P5：超過 1 GB 的檔案在選檔時就被擋（以 fsutil 建 1.1 GB 的檔案，不實際上傳）。
  const hugePath = join(filesDir, 'huge.bin');
  if (!existsSync(hugePath)) {
    execFileSync('fsutil', ['file', 'createnew', hugePath, String(1181116006)]);
  }
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor();
  await page.locator('.form-modal input[type=file]').setInputFiles([hugePath]);
  const hugeToast = await waitForToast(page, '超過 1GB 限制').then(() => true, () => false);
  const pendingAfterHuge = await page.locator('.form-modal .list-group-item', { hasText: 'huge.bin' }).count();
  await screenshot(page, 'm04-p5-超過上限');
  await closeForm(page);
  await closeToasts(page);
  recorder.add('P5', '超過 1 GB 的附件被擋', hugeToast && pendingAfterHuge === 0, `顯示「huge.bin 超過 1GB 限制」=${hugeToast}；待上傳清單 ${pendingAfterHuge} 筆`);

  // P6：副檔名不在白名單：選檔當下就擋（不必整份上傳完才被伺服器拒絕），不會進待上傳清單。
  const htmlPath = makeFile(`攻擊${stamp}.html`, '<script>alert(1)</script>');
  await clickToolbar(page, '新增');
  await page.locator('.form-modal').waitFor();
  await page.locator('.form-modal input[type=file]').setInputFiles([htmlPath]);
  const badToast = await waitForToast(page, '類型不在允許清單').then(() => true, () => false);
  const badPending = await page.locator('.form-modal .list-group-item', { hasText: `攻擊${stamp}.html` }).count();
  await closeForm(page);
  await closeToasts(page);
  recorder.add('P6', '不允許的附件類型在選檔時就被擋', badToast && badPending === 0, `顯示「檔案 攻擊${stamp}.html 的類型不在允許清單中。」=${badToast}；待上傳清單 ${badPending} 筆`);

  // P7：重名（前後空白不算不同）被擋。
  await addProject(page, ' E2E 專案甲 ', [], { expect: '專案名稱已存在' }).catch(() => null);
  const dupToast = await page.locator('.ant-notification-notice', { hasText: '專案名稱已存在' }).count();
  await closeForm(page);
  await closeToasts(page);
  recorder.add('P7', '專案重名被擋', dupToast > 0, dupToast > 0 ? '顯示「專案名稱已存在，無法新增。」' : '沒有擋下');

  // P8：兩個分頁同時修改同一筆：後存的得到衝突訊息、附件不會存進去。
  const pageB = await ctx.newPage();
  await openList(pageB, '/projects');
  await clickRowAction(page, 'E2E 專案乙', '修改');
  await page.locator('.form-modal').waitFor();
  await clickRowAction(pageB, 'E2E 專案乙', '修改');
  await pageB.locator('.form-modal').waitFor();
  await formField(page, '描述').fill(`分頁 A 先存 ${stamp}`);
  await submitForm(page);
  await confirmSave(page);
  await waitForToast(page, '修改成功');
  await formField(pageB, '描述').fill(`分頁 B 後存 ${stamp}`);
  await pageB.locator('.form-modal input[type=file]').setInputFiles([makeFile(`晚到${stamp}.txt`, 'x')]);
  await submitForm(pageB);
  await confirmSave(pageB);
  const conflictShown = await waitForToast(pageB, '已被其他人修改').then(() => true, () => false);
  await screenshot(pageB, 'm04-p8-版本衝突');
  await pageB.close();
  const second = query("SELECT Description FROM Project WHERE Title = 'E2E 專案乙'")[0].Description;
  const lateFiles = filesOf('E2E 專案乙').length;
  await closeToasts(page);
  recorder.add('P8', '兩個分頁同時修改：後存的得到衝突訊息', conflictShown && second === `分頁 A 先存 ${stamp}` && lateFiles === 0,
    `分頁 B 顯示衝突=${conflictShown}；資料庫描述「${second}」；晚到的附件 ${lateFiles} 個`);

  // P9：刪除、還原、永久刪除：永久刪除後附件實體檔移除。
  const keptPaths = filesOf(title).map((x) => x.path);
  await deleteRecord(page, title);
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: title }).first().waitFor();
  await restoreRecord(page, title);
  await clickToolbar(page, '返回清單');
  await page.locator('.ant-table-row', { hasText: title }).first().waitFor();
  await deleteRecord(page, title);
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: title }).first().waitFor();
  await purgeRecord(page, title);
  await clickToolbar(page, '返回清單');
  const purgedLeft = keptPaths.filter((x) => existsSync(x)).length;
  const audits = query(`SELECT Action FROM AuditLog WHERE TargetType = 'Project' AND Detail LIKE ? ORDER BY Id`, `%${title}%`).map((x) => x.Action);
  await closeToasts(page);
  recorder.add('P9', '刪除、還原、永久刪除（實體檔一併移除）', purgedLeft === 0 && query('SELECT COUNT(*) AS n FROM Project WHERE Title = ?', title)[0].n === 0,
    `永久刪除前 ${keptPaths.length} 個實體檔，之後剩 ${purgedLeft} 個；稽核 ${audits.join('→')}`);

  // P9b：還原時已有同名的專案：擋下（0.10.3 起專案名稱不可重複）。
  const twin = `E2E 同名還原${stamp}`;
  await addProject(page, twin);
  await deleteRecord(page, twin);
  await addProject(page, twin);
  await clickToolbar(page, '顯示已刪除');
  await page.locator('.ant-table-row', { hasText: twin }).first().waitFor();
  await clickRowAction(page, twin, '還原');
  await confirm(page, '還原');
  const twinBlocked = await waitForToast(page, '已有同名的專案').then(() => true, () => false);
  await clickToolbar(page, '返回清單');
  await closeToasts(page);
  const twinActive = query('SELECT COUNT(*) AS n FROM Project WHERE Title = ? AND IsDeleted = 0', twin)[0].n;
  recorder.add('P9b', '還原時已有同名的專案被擋', twinBlocked && twinActive === 1, `顯示「已有同名的專案…」=${twinBlocked}；未刪除的同名專案 ${twinActive} 筆`);

  // P10：匯出 Excel。
  const [exported] = await Promise.all([page.waitForEvent('download', { timeout: 30000 }), clickToolbar(page, '匯出 Excel')]);
  const exportFile = join(outDir, exported.suggestedFilename());
  await exported.saveAs(exportFile);
  const head = readFileSync(exportFile).subarray(0, 2).toString('latin1');
  recorder.add('P10', '匯出 Excel', exportFile.endsWith('.xlsx') && head === 'PK' && query("SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Project.Export'")[0].n >= 1,
    `下載 ${exported.suggestedFilename()}（${head === 'PK' ? 'xlsx 格式' : '格式不對'}），寫了匯出稽核`);

  // P11：批次刪除（勾兩筆）。
  for (const name of [`E2E 批次專案一${stamp}`, `E2E 批次專案二${stamp}`]) {
    await addProject(page, name);
  }
  for (const name of [`E2E 批次專案一${stamp}`, `E2E 批次專案二${stamp}`]) {
    await page.locator('.ant-table-row', { hasText: name }).first().locator('.ant-checkbox-input').check();
  }
  await page.locator('button', { hasText: '刪除選取（2）' }).click();
  await confirm(page, '刪除');
  const batchToast = await waitForToast(page, '已刪除 2 筆').then(() => true, () => false);
  const batchLeft = query("SELECT COUNT(*) AS n FROM Project WHERE Title LIKE ? AND IsDeleted = 0", `E2E 批次專案%${stamp}`)[0].n;
  await closeToasts(page);
  recorder.add('P11', '批次刪除', batchToast && batchLeft === 0, `通知「已刪除 2 筆」=${batchToast}；未刪除的剩 ${batchLeft} 筆`);

  // P12：異動歷程（管理員）：E2E 專案乙剛被修改過。
  await clickRowAction(page, 'E2E 專案乙', '異動歷程');
  await page.locator('.audit-history-modal .audit-history-item').first().waitFor({ timeout: 15000 });
  const historyItems = await page.locator('.audit-history-modal .audit-history-item').count();
  await page.locator('.audit-history-modal .audit-history-item').first().click();
  await page.waitForTimeout(800);
  const historyDetail = await page.locator('.audit-history-modal .audit-history-detail').innerText();
  await screenshot(page, 'm04-p12-異動歷程');
  await page.keyboard.press('Escape');
  recorder.add('P12', '異動歷程（管理員）', historyItems >= 1 && historyDetail.includes('Project.Update'), `列出 ${historyItems} 筆，第一筆是 Project.Update`);

  // P13：資料保留頁的「前往」帶 ?deleted=1：一進來就是已刪除的清單。
  await page.goto('/data-retention');
  await page.locator('.ant-table-row', { hasText: '已刪除的專案' }).first().locator('a.crud-action-button').click();
  await page.waitForURL(/\/projects\?deleted=1/, { timeout: 30000 });
  await page.locator('button.toolbar-icon-button', { hasText: '返回清單' }).waitFor({ timeout: 30000 });
  await page.locator('.project-view-table .ant-table-row', { hasText: 'E2E 舊專案' }).first().waitFor({ timeout: 30000 });
  recorder.add('P13', '資料保留頁「前往」直接顯示已刪除的專案', await isWasmPage(page), `${new URL(page.url()).pathname}${new URL(page.url()).search}，看到「E2E 舊專案」`);

  // P14：Server 頁面與 WASM 頁面互相切換（側邊選單）。
  await page.goto('/myusers');
  await page.locator('.ant-table-wrapper').first().waitFor({ timeout: 30000 });
  const serverIsWasm = await isWasmPage(page);
  await page.locator('.ant-menu-submenu-title:visible', { hasText: '專案管理' }).first().click();
  await page.locator('a:visible', { hasText: '專案項目' }).first().click();
  await page.waitForURL(/\/projects$/, { timeout: 30000 });
  await page.locator('.project-view-table .ant-table-row').first().waitFor({ timeout: 60000 });
  const projectsIsWasm = await isWasmPage(page);
  recorder.add('P14', 'Server 與 WASM 頁面互相切換', !serverIsWasm && projectsIsWasm, '/myusers（Server）→ /projects（WASM，整頁載入）');

  recorder.add('P15', '管理員操作過程沒有未處理的錯誤', errors.length === 0, errors.length === 0 ? '無' : errors.join('；'));

  // P16 的準備：大檔專案改成只限研發部（實體檔確實存在，404 才是因為範圍）；管理員申請得到票證。
  execute('UPDATE Project SET Teams = ? WHERE Title = ?', '\nE2E 研發部\n', bigTitle);
  const secretFile = filesOf(bigTitle)[0];
  const adminTicket = await apiAsPageUser(page, `/api/v1/Project/files/${secretFile.Id}/ticket`, { method: 'POST' });
  await ctx.close();

  // P16：團隊範圍：業務部的人看不到研發部的專案，也申請不到它附件的下載票證。
  const salesCtx = await newContext(browser);
  const sales = await salesCtx.newPage();
  await login(sales, { account: 'e2e-sales', password: 'Sales!E2e2026x' });
  await openList(sales, '/projects');
  const salesRows = await rowTexts(sales);
  const ticketStatus = await apiAsPageUser(sales, `/api/v1/Project/files/${secretFile.Id}/ticket`, { method: 'POST' });
  const getStatus = await apiAsPageUser(sales, `/api/v1/Project/${query('SELECT Id FROM Project WHERE Title = ?', bigTitle)[0].Id}`);
  await salesCtx.close();
  recorder.add('P16', '團隊範圍（業務部看不到研發部的專案與附件）',
    !salesRows.some((x) => x.includes('E2E 專案甲')) && salesRows.some((x) => x.includes('E2E 專案丙')) && adminTicket === 200 && ticketStatus === 404 && getStatus === 404,
    `清單有「E2E 專案丙」、沒有「E2E 專案甲」；同一個附件管理員申請票證回 ${adminTicket}、業務部的人回 ${ticketStatus}；讀專案回 ${getStatus}`);

  // P17：唯讀角色：看不到新增、修改、刪除與勾選欄；直接呼叫 API 寫入回 403。
  const readerCtx = await newContext(browser);
  const reader = await readerCtx.newPage();
  await login(reader, { account: readerAccount, password: readerPassword });
  await openList(reader, '/projects');
  const writeButtons = await reader.locator('button.toolbar-icon-button', { hasText: '新增' }).count()
    + await reader.locator('button.crud-action-button', { hasText: '修改' }).count()
    + await reader.locator('button.crud-action-button', { hasText: '刪除' }).count()
    + await reader.locator('.ant-table-selection-column').count();
  const createStatus = await apiAsPageUser(reader, '/api/v1/Project', { method: 'POST', body: { id: 0, title: '越權新增', status: '未開始', priority: '中', owner: 'x' } });
  const deleteStatus = await apiAsPageUser(reader, `/api/v1/Project/${query("SELECT Id FROM Project WHERE Title = 'E2E 專案乙'")[0].Id}`, { method: 'DELETE' });
  await screenshot(reader, 'm04-p17-唯讀角色');
  await readerCtx.close();
  recorder.add('P17', '唯讀角色看不到寫入按鈕、API 回 403', writeButtons === 0 && createStatus === 403 && deleteStatus === 403,
    `寫入按鈕與勾選欄 ${writeButtons} 個；API 新增回 ${createStatus}、刪除回 ${deleteStatus}`);

  // P18：沒有專案權限的人開 /projects：顯示沒有權限，伺服器記一筆權限拒絕稽核。
  const noCtx = await newContext(browser);
  const no = await noCtx.newPage();
  await login(no, { account: 'e2e-catonly', password: 'CatOnly!E2e2026' });
  await no.goto('/projects');
  await no.locator('.alert-danger').waitFor({ timeout: 60000 });
  const deniedText = await no.locator('.alert-danger').innerText();
  await no.waitForTimeout(1500);
  const deniedAudit = query(`SELECT COUNT(*) AS n FROM AuditLog WHERE Action = 'Permission.Denied' AND TargetId = '/projects' AND Detail = 'via=WASM' AND OccurredAt >= ?`, startedAt)[0].n;
  await noCtx.close();
  recorder.add('P18', '沒有權限開頁：顯示沒有權限並記稽核', deniedText.includes('沒有權限') && deniedAudit >= 1, `畫面「${deniedText.trim()}」；權限拒絕稽核 ${deniedAudit} 筆`);

  // 全部路由煙霧測試（/projects 已是 WASM）。
  await runSmoke(browser, smoke);
} catch (error) {
  for (const [i, open] of browser.contexts().flatMap((c) => c.pages()).entries()) {
    await screenshot(open, `m04-failed-${i}`).catch(() => {});
  }
  throw error;
} finally {
  await browser.close();
}

const ok = recorder.save() & smoke.save();
process.exit(ok ? 0 : 1);
