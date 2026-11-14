// 正式發佈用的 Service Worker（0.10.11 起，WASM 遷移 M12）。發佈時 SDK 以這份取代 service-worker.js，檔頭加上清單版本的註解
// （內容一變，瀏覽器就知道有新版）。開發時用的是 service-worker.js（不快取）。
//
// ⚠️ 只快取前端自己的靜態檔，絕不快取 API 回應、任何資料或 Token：
//   - 只在安裝時把 service-worker-assets.js 列出的檔案（帶完整性雜湊）放進快取；執行期間從不寫快取。
//   - 跨來源的請求（雙站台的 API、Google 字型）、不是 GET 的請求一律不攔截，由瀏覽器自己走網路。
//   - networkOnlyPaths 的伺服器端點一律不攔截：同站台時 API 主機一併提供前端，這些端點都在本 Service Worker 的範圍裡；
//     導覽到這些網址也不可回 index.html，否則 Google 登入、下載、Swagger 都會壞。
// 導覽（使用者開網址、重新整理）回快取的 index.html，所以離線時外框照樣開得起來；新版本由下面的更新流程換上。
//
// 更新流程：瀏覽器發現 service-worker.js 變了 → 新版安裝（下載新的檔案到新的快取）→ 停在 waiting，**不自動 skipWaiting**
// （使用者可能正在編輯）→ js/pwa.js 通知畫面顯示新版本提示列 → 使用者按「立即更新」時送 SKIP_WAITING → 新版啟用、刪掉舊快取
// → 頁面收到 controllerchange 後整頁重新載入一次。
// 新版安裝失敗（某個檔案下載失敗，或與清單的完整性雜湊不符 —— 例如有人手改了發佈輸出裡的檔案）時新版直接作廢、繼續用舊版，
// js/pwa.js 會經瀏覽器錯誤回報記進伺服器日誌。
self.importScripts('./service-worker-assets.js');

self.addEventListener('install', event => event.waitUntil(onInstall()));
self.addEventListener('activate', event => event.waitUntil(onActivate()));
self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') { self.skipWaiting(); }
});

// 快取名稱：offline-cache-<範圍路徑>:<清單版本>（範圍路徑以 encodeURIComponent 編碼，裡面不會有分隔用的「:」）。
// 帶範圍：同一個來源的不同路徑部署兩套系統（https://host/a/ 與 https://host/b/）時，快取是整個來源共用的，
// 啟用時只刪「自己範圍、但版本不同」的快取，不會刪到對方的。開發用的 service-worker.js 用同一個前綴（PwaConventionTests 守門）。
const cacheNamePrefix = `offline-cache-${encodeURIComponent(new URL(self.registration.scope).pathname)}:`;
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// 要放進快取的檔案：WebAssembly 執行環境與組件、地區資料、腳本、樣式、圖片、入口頁、前端設定與 manifest。
const offlineAssetsInclude = [/\.wasm$/, /\.dll$/, /\.dat$/, /\.blat$/, /\.js$/, /\.json$/, /\.css$/, /\.html$/, /\.png$/, /\.woff2?$/, /\.webmanifest$/];
// 不放進快取的：Service Worker 自己、沒有用到的 Bootstrap 與 AntDesign 主題（只用 bootstrap.min.css 與 ant-design-blazor.css）。
const offlineAssetsExclude = [
    /^service-worker\.js$/,
    /^lib\/bootstrap\/(?!dist\/css\/bootstrap\.min\.css$)/,
    /^_content\/AntDesign\/css\/(?!ant-design-blazor\.css$)/,
];

// 伺服器端點（小寫；整段相符或其下的路徑，所以 /health 不會誤中 /healthy）。新增伺服器端點時要加進來（PwaConventionTests 守門）。
const networkOnlyPaths = [
    '/api',                 // Web API（含下載票證 /api/v1/downloads/…）
    '/signin-google',       // Google 登入的 OAuth 回呼
    '/auths/google',        // Google 登入的導向與兌換（ExternalAuthController）
    '/swagger',             // API 文件
    '/health',              // 健康檢查
    '/auths/resetpassword', // 網址帶 Token 的前端頁：一律向伺服器拿，讓伺服器加的 no-referrer、no-store 生效
];

function isNetworkOnly(pathname) {
    const path = pathname.toLowerCase();
    return networkOnlyPaths.some(prefix => path === prefix || path.startsWith(prefix + '/'));
}

const offlineAssets = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)));
const offlineUrls = new Set(offlineAssets.map(asset => new URL(asset.url, self.location).href));
const indexUrl = new URL('index.html', self.location).href;

async function onInstall() {
    // cache: 'no-cache'：向伺服器確認過才放進快取（帶指紋的檔案通常 304，不必重新下載）。
    const requests = offlineAssets.map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    // 同站台發佈時入口頁由 API 主機回（不在清單裡）：另外放一份，離線時外框才開得起來。
    if (!offlineUrls.has(indexUrl)) {
        requests.push(new Request('index.html', { cache: 'no-cache' }));
    }

    const cache = await caches.open(cacheName);
    await cache.addAll(requests);
}

async function onActivate() {
    const keys = await caches.keys();
    await Promise.all(keys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
}

self.addEventListener('fetch', event => {
    const request = event.request;
    if (request.method !== 'GET') { return; }
    const url = new URL(request.url);
    if (url.origin !== self.location.origin) { return; }
    if (isNetworkOnly(url.pathname)) { return; }

    if (request.mode === 'navigate' && !offlineUrls.has(url.href)) {
        // 前端的頁面網址：一律回入口頁，由前端路由決定顯示哪一頁。
        event.respondWith(fromCache(indexUrl, request));
    } else if (offlineUrls.has(url.href)) {
        event.respondWith(fromCache(request, request));
    }
    // 其他（清單之外的檔案）不攔截。
});

async function fromCache(key, request) {
    const cache = await caches.open(cacheName);
    return (await cache.match(key)) || fetch(request);
}
