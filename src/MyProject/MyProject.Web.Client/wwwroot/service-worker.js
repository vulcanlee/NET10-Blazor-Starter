// 開發用的 Service Worker（0.10.11 起，WASM 遷移 M12）：什麼都不快取、不攔截任何請求，改了程式重新整理就看得到。
// 發佈時 SDK 以 service-worker.published.js 取代這一份（見 MyProject.Web.Client.csproj 的 ServiceWorker）。
//
// ⚠️ Debug 建置時 SDK 會在這份的檔頭加上清單版本（/* Manifest version: … */），程式一改內容就變，瀏覽器每次都當成新版。
// 所以這一份要表明身分（0.10.20 起）：js/pwa.js 送 GET_KIND 來問，回答 'development'，pwa.js 就送 SKIP_WAITING 讓它默默接手、
// 不顯示「有新版本」提示列（這一份不快取、不攔截，換上它不會改變畫面）。發佈版不回答 GET_KIND。
// 同一個網址先前裝過發佈版時，也一樣默默換回這一份；下次重新整理起走網路、看得到最新的程式。
self.addEventListener('message', event => {
    if (!event.data) { return; }
    if (event.data.type === 'SKIP_WAITING') { self.skipWaiting(); }
    if (event.data.type === 'GET_KIND' && event.ports[0]) { event.ports[0].postMessage('development'); }
});

// 換上這一份時，刪掉同一範圍發佈版留下的離線快取（前綴與 service-worker.published.js 相同；別的範圍的不動）。只刪不寫。
const cacheNamePrefix = `offline-cache-${encodeURIComponent(new URL(self.registration.scope).pathname)}:`;

self.addEventListener('activate', event => event.waitUntil(onActivate()));

async function onActivate() {
    const keys = await caches.keys();
    await Promise.all(keys
        .filter(key => key.startsWith(cacheNamePrefix))
        .map(key => caches.delete(key)));
}
