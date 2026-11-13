// WASM 遷移 M12（0.10.11）：PWA 與離線的實機驗證。必須以「發佈版」前端執行（開發用的 service-worker.js 不快取、不會出現新版本提示），
// 以雙站台跑：前端 E2E_BASE_URL（發佈輸出，web.config 規則）、API E2E_API_URL；E2E_STATIC_ROOT 是前端發佈根目錄（W5 模擬部署新版時要改檔案）。
//   W1 Service Worker 安裝並接管頁面；快取只有前端檔案，沒有任何 /api 回應。
//   W2 可安裝為 PWA（Chrome 的安裝條件檢查沒有錯誤）、manifest 的名稱與圖示。
//   W3 離線：已開的頁面出現離線提示列、操作時顯示「目前離線」訊息、離線重新整理外框仍開得起來且不被登出、側邊選單換頁不卡死。
//   W4 恢復連線：提示列消失、自動重新載入回到正常、推播重新連線。
//   W5 部署新版：改發佈輸出的 service-worker-assets.js 版本 → 出現「有新版本」提示列 → 按下後重新載入一次、換成新版快取；「稍後」可收起。
//   W6 英文瀏覽器（en-US）的日期仍是 zh-TW 格式（只載中日韓語系資料 icudt_CJK.dat 之後）。
//   W7 瀏覽器有網路、只是伺服器暫時連不上：顯示連不上的訊息、不登出；伺服器回來後 15 秒內自動重新載入（不靠瀏覽器的恢復連線事件）。
// ＋36 路由煙霧測試（Service Worker 接管下）。結果寫到 E2E_OUT_DIR/m12-results.json 與 smoke-results.json。
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { apiUrl, baseUrl, createRecorder, execute, launchBrowser, login, newContext, screenshot, waitForPredicate } from './common.mjs';
import { clickToolbar } from './crud-ui.mjs';
import { seedProjects } from './m04-views.mjs';
import { runSmoke } from './smoke-routes.mjs';

const staticRoot = process.env.E2E_STATIC_ROOT;
if (!staticRoot) {
  throw new Error('需要 E2E_STATIC_ROOT（前端發佈根目錄）。');
}

// 截圖不可出現開發機設定的個人信箱。
execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");
seedProjects();
const recorder = createRecorder('m12-results.json');
const smoke = createRecorder('smoke-results.json');
const browser = await launchBrowser();

async function swState(page) {
  return page.evaluate(async () => {
    const registration = await navigator.serviceWorker.getRegistration();
    const keys = await caches.keys();
    const entries = [];
    for (const key of keys) {
      for (const request of await (await caches.open(key)).keys()) {
        entries.push(new URL(request.url).pathname);
      }
    }
    return {
      controlled: !!navigator.serviceWorker.controller,
      waiting: !!registration?.waiting,
      keys,
      count: entries.length,
      api: entries.filter((x) => x.toLowerCase().startsWith('/api/')),
      hasIndex: entries.some((x) => x === '/' || x.endsWith('/index.html')),
      hasFramework: entries.filter((x) => x.includes('/_framework/')).length,
    };
  });
}

async function waitForController(page) {
  await waitForPredicate(page, () => navigator.serviceWorker.ready.then(() => true), null, { timeout: 120000 });
  if (!(await page.evaluate(() => !!navigator.serviceWorker.controller))) {
    await page.reload();
    await waitForPredicate(page, () => !!navigator.serviceWorker.controller, null, { timeout: 60000 });
  }
}

try {
  const context = await newContext(browser);
  const page = await context.newPage();
  const pushRequests = [];
  page.on('request', (request) => {
    if (request.url().includes('/api/v1/notifications/stream')) {
      pushRequests.push(Date.now());
    }
  });

  // W1：安裝並接管。
  await login(page);
  await page.goto('/projects');
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
  await waitForController(page);
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
  const w1 = await swState(page);
  recorder.add('W1', 'Service Worker 安裝並接管；快取只有前端檔案、沒有 /api', w1.controlled && w1.count > 0 && w1.api.length === 0 && w1.hasFramework > 0 && w1.keys.every((k) => k.startsWith('offline-cache-')),
    `接管=${w1.controlled}；快取 ${w1.keys.join('、')}，${w1.count} 個項目（_framework ${w1.hasFramework} 個、index.html=${w1.hasIndex}）；/api 項目 ${w1.api.length} 個`);

  // W2：可安裝為 PWA。Playwright 一般的瀏覽器內容是無痕模式（Chrome 規定無痕不能安裝 PWA），這裡另開一個有使用者資料夾的瀏覽器。
  {
    const { chromium } = await import(pathToFileURL(join(process.env.PLAYWRIGHT_CORE_DIR, 'index.mjs')).href);
    const profile = mkdtempSync(join(tmpdir(), 'm12-pwa-profile-'));
    const persistent = await chromium.launchPersistentContext(profile, {
      executablePath: process.env.E2E_CHROME ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe',
      headless: process.env.E2E_HEADED !== '1', ignoreHTTPSErrors: true, baseURL: baseUrl, viewport: { width: 1440, height: 900 }, locale: 'zh-TW',
    });
    try {
      const p = persistent.pages()[0] ?? await persistent.newPage();
      await p.goto('/Auths/Login');
      await p.locator('#account').waitFor({ timeout: 60000 });
      await waitForPredicate(p, () => navigator.serviceWorker.ready.then(() => true), null, { timeout: 120000 });
      await p.reload();
      await p.locator('#account').waitFor({ timeout: 60000 });
      const cdp = await persistent.newCDPSession(p);
      const installability = await cdp.send('Page.getInstallabilityErrors');
      const manifest = await cdp.send('Page.getAppManifest');
      const manifestJson = (() => { try { return JSON.parse(manifest.data ?? ''); } catch { return null; } })();
      recorder.add('W2', '可安裝為 PWA', installability.installabilityErrors.length === 0 && manifestJson !== null,
        `安裝條件錯誤：${installability.installabilityErrors.map((x) => x.errorId).join('、') || '沒有'}；manifest ${manifest.url?.replace(baseUrl, '')}：name「${manifestJson?.name}」、short_name「${manifestJson?.short_name}」、display=${manifestJson?.display}、圖示 ${manifestJson?.icons?.map((x) => x.sizes).join('／')}`);
    } finally {
      await persistent.close();
      rmSync(profile, { recursive: true, force: true });
    }
  }

  // W3：離線。
  await context.setOffline(true);
  const offlineBar = await page.locator('.app-offline-bar').waitFor({ timeout: 10000 }).then(() => true).catch(() => false);
  // 按清單的「重新整理」（一定會呼叫 API）。
  await clickToolbar(page, '重新整理').catch(() => {});
  const offlineToast = await page.locator('.ant-notification-notice', { hasText: '目前離線' }).first().innerText({ timeout: 10000 }).catch(() => '');
  // 0.10.11 實機發現：讀取失敗時不可再說「已更新最新資料」。
  await page.waitForTimeout(1500);
  const falseSuccess = await page.locator('.ant-notification-notice', { hasText: '已更新最新資料' }).count();
  await screenshot(page, 'W3-離線提示列');
  await page.reload({ timeout: 60000 }).catch(() => {});
  // 離線開頁從 Service Worker 的快取啟動 WebAssembly，再讀身分失敗；離線偵測在第一次渲染後才啟動，最多等 20 秒。
  await page.locator('.app-offline-bar').waitFor({ timeout: 20000 }).catch(() => {});
  const reloadedPath = new URL(page.url()).pathname;
  const unreachable = (await page.getByText('目前無法連線到伺服器，恢復連線後會自動重新載入。').count()) > 0;
  const barAfterReload = (await page.locator('.app-offline-bar').count()) > 0;
  const appRendered = (await page.locator('#app').innerText().catch(() => '')).trim().length > 0;
  await screenshot(page, 'W3-離線重新整理');
  recorder.add('W3', '離線：提示列、離線訊息、離線重新整理外框仍開得起來且不被登出',
    offlineBar && offlineToast.includes('目前離線') && falseSuccess === 0 && reloadedPath === '/projects' && appRendered && barAfterReload,
    `已開頁面的離線提示列=${offlineBar}；操作的通知「${offlineToast.replace(/\s+/g, ' ').replace('系統訊息', '').replace('複製', '').trim() || '沒有'}」、誤報「已更新最新資料」${falseSuccess} 則；離線重新整理後停在 ${reloadedPath}、畫面有內容=${appRendered}、提示列=${barAfterReload}、「目前無法連線到伺服器…」=${unreachable}`);

  // W4：恢復連線。
  const before = pushRequests.length;
  await context.setOffline(false);
  const barGone = await page.locator('.app-offline-bar').waitFor({ state: 'detached', timeout: 15000 }).then(() => true).catch(() => false);
  // 自動重新載入（瀏覽器恢復連線的事件，或每 15 秒試一次伺服器）後回到正常的外框。
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 }).catch(() => {});
  await page.locator('.ant-table-row').first().waitFor({ timeout: 30000 }).catch(() => {});
  await page.waitForTimeout(3000);
  const restored = (await page.locator('.user-menu-trigger').count()) > 0 && new URL(page.url()).pathname === '/projects';
  const rows = await page.locator('.ant-table-row').count();
  recorder.add('W4', '恢復連線：提示列消失、自動回到正常、推播重新連線', barGone && restored && rows > 0 && pushRequests.length > before,
    `提示列消失=${barGone}；停在 ${new URL(page.url()).pathname}、外框正常=${restored}、清單 ${rows} 列；恢復後推播連線請求 ${pushRequests.length - before} 次`);

  // W5：部署新版。
  const assetsFile = join(staticRoot, 'wwwroot', 'service-worker-assets.js');
  const original = readFileSync(assetsFile, 'utf8');
  const oldVersion = /"version":\s*"([^"]+)"/.exec(original)[1];
  const newVersion = `${oldVersion}e2e`;
  writeFileSync(assetsFile, original.replace(`"version": "${oldVersion}"`, `"version": "${newVersion}"`), 'utf8');
  for (const suffix of ['.br', '.gz']) {
    if (existsSync(assetsFile + suffix)) {
      rmSync(assetsFile + suffix);
    }
  }
  await page.reload();
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
  const barShown = await page.locator('.app-update-bar').waitFor({ timeout: 90000 }).then(() => true).catch(() => false);
  await screenshot(page, 'W5-新版本提示列');
  // 「稍後」收起，再重新整理一次提示仍會出現（新版還在等待）。
  await page.locator('.app-update-later').click().catch(() => {});
  const hidden = (await page.locator('.app-update-bar').count()) === 0;
  await page.reload();
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
  const shownAgain = await page.locator('.app-update-bar').waitFor({ timeout: 30000 }).then(() => true).catch(() => false);
  let loads = 0;
  page.on('load', () => loads++);
  await page.locator('.app-update-apply').click();
  await page.waitForTimeout(8000);
  await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 }).catch(() => {});
  const w5 = await swState(page);
  recorder.add('W5', '部署新版：出現提示、稍後可收起、按下後重新載入一次並換成新版快取',
    barShown && hidden && shownAgain && loads === 1 && w5.keys.some((k) => k.endsWith(newVersion)) && !w5.keys.some((k) => k.endsWith(oldVersion)) && w5.controlled && !w5.waiting,
    `提示列出現=${barShown}；按「稍後」收起=${hidden}；重新整理後再出現=${shownAgain}；按下後整頁載入 ${loads} 次；快取 ${w5.keys.join('、')}；仍有等待中的新版=${w5.waiting}`);
  // W7：伺服器暫時連不上（瀏覽器沒有離線）。攔下所有送往 API 主機的請求，重新整理；解除後等自動重新載入。
  const apiOrigin = new URL(apiUrl).origin;
  await page.route(`${apiOrigin}/**`, (route) => route.abort('connectionrefused'));
  await page.reload();
  const shownUnreachable = await page.getByText('目前無法連線到伺服器，恢復連線後會自動重新載入。').waitFor({ timeout: 30000 }).then(() => true).catch(() => false);
  const stillOnline = await page.evaluate(() => navigator.onLine);
  const noOfflineBar = (await page.locator('.app-offline-bar').count()) === 0;
  await page.unroute(`${apiOrigin}/**`);
  const unblockedAt = Date.now();
  const recovered = await page.locator('.user-menu-trigger').waitFor({ timeout: 40000 }).then(() => true).catch(() => false);
  const seconds = ((Date.now() - unblockedAt) / 1000).toFixed(1);
  recorder.add('W7', '伺服器暫時連不上：顯示訊息不登出，伺服器回來後自動重新載入',
    shownUnreachable && stillOnline && noOfflineBar && recovered && new URL(page.url()).pathname === '/projects',
    `連不上的訊息=${shownUnreachable}；瀏覽器仍在線=${stillOnline}、沒有離線提示列=${noOfflineBar}；解除攔截後 ${seconds} 秒回到外框=${recovered}、停在 ${new URL(page.url()).pathname}`);
  await context.close();

  // W6：英文瀏覽器的日期格式。
  {
    const en = await browser.newContext({ ignoreHTTPSErrors: true, baseURL: baseUrl, viewport: { width: 1440, height: 900 }, locale: 'en-US' });
    const p = await en.newPage();
    await login(p);
    await p.goto('/projects');
    await p.locator('.ant-table-row').first().waitFor({ timeout: 60000 });
    const text = await p.locator('.ant-table-row').first().innerText();
    const zhTime = /上午|下午/.test(text);
    const enTime = /\b(AM|PM)\b/.test(text);
    await screenshot(p, 'W6-英文瀏覽器專案清單');
    recorder.add('W6', '英文瀏覽器（en-US）的日期仍是 zh-TW 格式（只載中日韓語系資料）', zhTime && !enTime,
      `瀏覽器語言 ${await p.evaluate(() => navigator.language)}；第一列「${text.replace(/\s+/g, ' ').slice(0, 90)}」；含上午／下午=${zhTime}、含 AM／PM=${enTime}`);
    await en.close();
  }

  // 36 路由煙霧（Service Worker 接管下）。
  await runSmoke(browser, smoke);
} finally {
  await browser.close();
}

const ok = recorder.save();
const okSmoke = smoke.save();
process.exit(ok && okSmoke ? 0 : 1);
