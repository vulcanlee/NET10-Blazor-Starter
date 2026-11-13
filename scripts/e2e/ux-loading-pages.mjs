// 首次載入畫面、系統錯誤頁、找不到頁面、開發環境 CSP（0.10.13 起）的實機情境。
//   L1：未登入、沒有快取、網路限速下開登入頁：從第一個畫面到登入表單出現，每 150 毫秒取樣一次，畫面不可有整片空白；
//       下載期間顯示載入卡片，進度百分比會前進；連拍截圖。
//   L2：已登入、限速下整頁重新整理 /projects：下載完成到外框出現之間（登入檢查期間）顯示「正在確認登入狀態…」，不空白。
//   L2b：/me 延遲 3 秒時看得到「正在確認登入狀態…」卡片（截圖用）。
//   L3：/Error?trace=…：繁中卡片、追蹤碼可複製、回上一頁回到出錯前的頁面、回首頁。
//   L4：登入後開不存在的網址與 /not-found：外框內的繁中卡片，回首頁。
//   L5：未登入開 /Error：照樣顯示（匿名頁）。
//   L6：開發環境（E2E 主機是 Development）前端連到 localhost 的開發工具（VS 瀏覽器連結、熱重載）不再觸發 CSP 違規。
// 結果寫到 E2E_OUT_DIR/ux-loading-pages-results.json，截圖在 screenshots/ux-*.png。
import { apiUrl, baseUrl, createRecorder, execute, launchBrowser, login, newContext, screenshot } from './common.mjs';

execute("UPDATE MyUser SET Email = 'support@e2e.test' WHERE Account = 'support'");

const recorder = createRecorder('ux-loading-pages-results.json');
const browser = await launchBrowser();

/** 以 CDP 限速（約 4 Mbps、40 毫秒延遲），並關閉快取：模擬第一次在一般網路開啟。 */
async function throttle(context, page) {
  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
  await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 40, downloadThroughput: 500 * 1024, uploadThroughput: 500 * 1024 });
  return cdp;
}

/** 開始取樣：每 150 毫秒記錄畫面上看得到的文字與載入卡片狀態，直到 done() 成立。 */
async function sample(page, done, { name, maxMs = 180000 } = {}) {
  const samples = [];
  const started = Date.now();
  let shot = 0;
  while (Date.now() - started < maxMs) {
    const state = await page.evaluate(() => {
      const card = document.querySelector('.app-loading');
      const fill = document.querySelector('.app-loading-bar-fill');
      return {
        text: (document.body?.innerText ?? '').trim().slice(0, 80),
        card: card ? (card.classList.contains('app-loading-done') ? 'layout' : 'index') : null,
        percent: getComputedStyle(document.documentElement).getPropertyValue('--blazor-load-percentage-text').trim(),
        fillWidth: fill ? fill.getBoundingClientRect().width : null,
      };
    }).catch(() => null);
    if (state) {
      samples.push({ ms: Date.now() - started, ...state });
      if (name && (shot < 6) && (samples.length === 2 || samples.length % 12 === 0)) {
        await screenshot(page, `${name}-${String(++shot).padStart(2, '0')}`);
      }
    }
    if (await done()) {
      break;
    }
    await page.waitForTimeout(150);
  }
  return { samples, elapsed: Date.now() - started };
}

try {
  // L1：第一次開啟（未登入、無快取、限速）。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    await throttle(context, page);
    await page.goto('/Auths/Login', { waitUntil: 'commit' });
    const { samples, elapsed } = await sample(page, async () => (await page.locator('#account').count()) > 0, { name: 'ux-L1-首次載入' });
    await screenshot(page, 'ux-L1-登入表單出現');
    const afterFirstPaint = samples.slice(samples.findIndex((s) => s.text.length > 0));
    const blanks = afterFirstPaint.filter((s) => s.text.length === 0);
    const indexCard = samples.filter((s) => s.card === 'index');
    const percents = [...new Set(indexCard.map((s) => s.percent).filter(Boolean))];
    // 只看有百分比之後的取樣：最早的取樣可能在樣式表還沒套用前（寬度是整個畫面，使用者看不到，瀏覽器在樣式表載入前不繪製）。
    const widths = indexCard.filter((s) => s.percent).map((s) => s.fillWidth ?? 0);
    const growing = widths.length > 1 && widths[widths.length - 1] > widths[0];
    recorder.add('L1', '第一次開啟（無快取、限速）：載入期間顯示載入卡片與進度，不空白',
      samples.length > 0 && blanks.length === 0 && indexCard.length > 0 && growing,
      `取樣 ${samples.length} 次、共 ${(elapsed / 1000).toFixed(1)} 秒；第一次有畫面在 ${samples.find((s) => s.text.length > 0)?.ms ?? '—'} 毫秒；之後空白 ${blanks.length} 次；`
      + `載入卡片出現 ${indexCard.length} 次，進度 ${percents.slice(0, 3).join('→')}…${percents.slice(-2).join('→')}（共 ${percents.length} 種）、進度條寬 ${Math.round(widths[0] ?? 0)}→${Math.round(widths[widths.length - 1] ?? 0)} 像素`);
    await context.close();
  }

  // L2：已登入、限速下重新整理：登入檢查期間顯示外框的載入卡片。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    await login(page);
    await page.goto('/projects');
    await page.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
    await throttle(context, page);
    await page.goto('/projects', { waitUntil: 'commit' });
    const { samples, elapsed } = await sample(page, async () => (await page.locator('.user-menu-trigger').count()) > 0, { name: 'ux-L2-重新整理' });
    await screenshot(page, 'ux-L2-外框出現');
    const afterFirstPaint = samples.slice(samples.findIndex((s) => s.text.length > 0));
    const blanks = afterFirstPaint.filter((s) => s.text.length === 0);
    const layoutCard = samples.filter((s) => s.card === 'layout');
    const checkingText = layoutCard.some((s) => s.text.includes('正在確認登入狀態'));
    recorder.add('L2', '已登入重新整理：下載卡片接著登入檢查卡片，中間不空白',
      blanks.length === 0 && samples.some((s) => s.card === 'index'),
      `取樣 ${samples.length} 次、共 ${(elapsed / 1000).toFixed(1)} 秒；空白 ${blanks.length} 次；下載卡片 ${samples.filter((s) => s.card === 'index').length} 次、`
      + `登入檢查卡片 ${layoutCard.length} 次（「正在確認登入狀態」=${checkingText}；本機 API 很快時可能一閃而過）`);

    // L2b：伺服器回應慢時（/me 延遲 3 秒）看得到外框的「正在確認登入狀態…」卡片。
    {
      const slow = await context.newPage();
      await slow.route('**/api/v1/Auth/me', async (route) => { await new Promise((r) => setTimeout(r, 3000)); await route.continue(); });
      await slow.goto('/projects');
      const shown = await slow.locator('.app-loading.app-loading-done').waitFor({ timeout: 60000 }).then(() => true).catch(() => false);
      const text = shown ? await slow.locator('.app-loading-text').innerText() : '';
      await screenshot(slow, 'ux-L2b-正在確認登入狀態');
      await slow.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
      recorder.add('L2b', '伺服器回應慢時：外框顯示「正在確認登入狀態…」卡片，之後正常進入',
        shown && text.includes('正在確認登入狀態'), `卡片=${shown}「${text}」`);
      await slow.close();
    }

    // L3：系統錯誤頁。
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    const unthrottled = await context.newPage();
    await unthrottled.goto('/projects');
    await unthrottled.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
    await unthrottled.goto('/Error?trace=A1B2C3D4');
    await unthrottled.locator('.status-card-title').waitFor({ timeout: 60000 });
    await unthrottled.waitForTimeout(800);
    await screenshot(unthrottled, 'ux-L3-錯誤頁');
    const title = await unthrottled.locator('.status-card-title').innerText();
    const trace = await unthrottled.locator('.status-card-trace code').innerText();
    await unthrottled.locator('button.status-card-copy').click();
    await unthrottled.locator('.status-card-copied').waitFor({ timeout: 5000 }).catch(() => {});
    const copied = await unthrottled.locator('.status-card-copied').innerText().catch(() => '');
    // 看得到的瀏覽器（E2E_HEADED=1）讀剪貼簿要分頁在前景、有焦點，否則 readText 被拒（0.10.14 實機）。
    await unthrottled.bringToFront();
    const clipboard = await unthrottled.evaluate(async () => {
      try { return await navigator.clipboard.readText(); } catch (e) { return `讀取失敗：${e.name} ${e.message}`; }
    }).catch((e) => `讀取失敗：${e.message}`);
    await screenshot(unthrottled, 'ux-L3-錯誤頁-已複製');
    await unthrottled.locator('button.status-card-secondary', { hasText: '回上一頁' }).click();
    await unthrottled.waitForURL((url) => url.pathname === '/projects', { timeout: 15000 }).catch(() => {});
    const backPath = new URL(unthrottled.url()).pathname;
    await unthrottled.goto('/Error');
    await unthrottled.locator('.status-card-title').waitFor({ timeout: 60000 });
    const traceHidden = (await unthrottled.locator('.status-card-trace').count()) === 0;
    await unthrottled.locator('a.status-card-primary', { hasText: '回首頁' }).click();
    await unthrottled.waitForURL((url) => url.pathname !== '/Error', { timeout: 15000 }).catch(() => {});
    const homePath = new URL(unthrottled.url()).pathname;
    recorder.add('L3', '錯誤頁：繁中、追蹤碼可複製、回上一頁、回首頁',
      title.includes('系統發生未預期的錯誤') && trace === 'A1B2C3D4' && copied === '已複製' && clipboard === 'A1B2C3D4' && backPath === '/projects' && traceHidden && homePath !== '/Error',
      `標題「${title}」、追蹤碼 ${trace}、複製後「${copied}」剪貼簿=${clipboard}；回上一頁到 ${backPath}；沒帶追蹤碼時不顯示=${traceHidden}；回首頁到 ${homePath}`);

    // L4：找不到頁面（外框內）。
    const notFound = [];
    for (const route of ['/no-such-page-e2e', '/not-found']) {
      await unthrottled.goto(route);
      await unthrottled.locator('.status-card-title').waitFor({ timeout: 60000 });
      await unthrottled.waitForTimeout(800);
      await screenshot(unthrottled, `ux-L4-找不到${route.replace(/[^a-z]+/g, '-')}`);
      notFound.push({
        route,
        title: await unthrottled.locator('.status-card-title').innerText(),
        inLayout: (await unthrottled.locator('.user-menu-trigger').count()) > 0,
        english: (await unthrottled.getByText('Not Found').count()) > 0,
      });
    }
    await unthrottled.locator('a.status-card-primary', { hasText: '回首頁' }).click();
    await unthrottled.waitForURL((url) => url.pathname !== '/not-found', { timeout: 15000 }).catch(() => {});
    const notFoundHome = new URL(unthrottled.url()).pathname;
    recorder.add('L4', '找不到頁面：外框內的繁中卡片、回首頁',
      notFound.every((x) => x.title.includes('找不到這個頁面') && x.inLayout && !x.english) && notFoundHome !== '/not-found',
      notFound.map((x) => `${x.route}：「${x.title}」外框=${x.inLayout} 英文=${x.english}`).join('；') + `；回首頁到 ${notFoundHome}`);

    // L6：開發環境的 CSP 允許連到 localhost 的開發工具（以 WebSocket 與 fetch 模擬熱重載與瀏覽器連結；連線本身失敗沒關係，只看有沒有 CSP 違規）。
    await unthrottled.goto('/projects');
    await unthrottled.locator('.user-menu-trigger').waitFor({ timeout: 60000 });
    const violations = await unthrottled.evaluate(async () => {
      const found = [];
      document.addEventListener('securitypolicyviolation', (e) => found.push(`${e.violatedDirective} ${e.blockedURI}`));
      try { new WebSocket('ws://localhost:65011/e2e'); } catch (e) { found.push(`throw ${e.message}`); }
      try { new WebSocket('wss://localhost:65012/e2e'); } catch (e) { found.push(`throw ${e.message}`); }
      await fetch('http://localhost:65013/browserLinkSignalR/negotiate', { method: 'POST' }).catch(() => {});
      await new Promise((r) => setTimeout(r, 1500));
      return found;
    });
    const response = await unthrottled.request.get('/projects');
    const csp = response.headers()['content-security-policy'] ?? '';
    const connectSrc = (csp.split(';').find((d) => d.trim().startsWith('connect-src')) ?? '').trim();
    if (apiUrl === baseUrl) {
      recorder.add('L6', '開發環境：連到 localhost 的開發工具不再觸發 CSP 違規',
        violations.length === 0 && csp.includes('ws://localhost:*') && csp.includes('http://localhost:*'),
        `CSP 違規 ${violations.length} 則${violations.length ? `：${violations.join('；')}` : ''}；${connectSrc}`);
    } else {
      // 雙站台：前端是發佈後的靜態站（web.config 的正式 CSP），不可放寬。
      recorder.add('L6', '雙站台（正式設定）：靜態站的 CSP 不放寬 localhost',
        !csp.includes('ws://localhost:*') && violations.length > 0,
        `CSP 違規 ${violations.length} 則（預期被擋）；${connectSrc}`);
    }
    await context.close();
  }

  // L5：未登入開錯誤頁。
  {
    const context = await newContext(browser);
    const page = await context.newPage();
    await page.goto('/Error?trace=ZZ9');
    await page.locator('.status-card-title').waitFor({ timeout: 60000 });
    await page.waitForTimeout(800);
    await screenshot(page, 'ux-L5-未登入錯誤頁');
    const path = new URL(page.url()).pathname;
    recorder.add('L5', '未登入開錯誤頁：照樣顯示', path === '/Error', `停在 ${path}`);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForTimeout(500);
    await screenshot(page, 'ux-L5-手機錯誤頁');
    await context.close();
  }
} finally {
  await browser.close();
}

process.exit(recorder.save() ? 0 : 1);
