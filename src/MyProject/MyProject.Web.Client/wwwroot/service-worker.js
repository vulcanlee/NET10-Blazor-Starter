// 開發用的 Service Worker（0.10.11 起，WASM 遷移 M12）：什麼都不快取、不攔截任何請求，改了程式重新整理就看得到，
// 也就不會出現「有新版本」提示列。發佈時 SDK 以 service-worker.published.js 取代這一份（見 MyProject.Web.Client.csproj 的 ServiceWorker）。
// 只留下 SKIP_WAITING：同一個網址先前裝過發佈版時，按提示列就能換回這一份。
self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') { self.skipWaiting(); }
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
