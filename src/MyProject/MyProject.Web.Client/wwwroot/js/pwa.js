// PWA（0.10.11 起，WASM 遷移 M12）：註冊 Service Worker、偵測新版本、回報瀏覽器的連線狀態。
// 由 index.html 以一般的 <script src> 載入（CSP 不允許行內腳本），頁面一打開就註冊，不等 WebAssembly 下載完。
// .NET 端：ClientConnectivity（watchConnectivity）、ClientUpdateBar（watchUpdates、applyUpdate）。
//
// 新版本的流程：
//   1. 檢查新版：瀏覽器在開頁（導覽）時自己會檢查；另外在分頁回到前景時（visibilitychange）呼叫 registration.update()，最多每 30 分鐘一次。
//   2. 新版的 Service Worker 安裝完、停在 waiting（registration.waiting、updatefound／statechange）→ 通知 .NET 顯示提示列。
//      第一次安裝（還沒有 Service Worker 控制這個頁面）不算新版本。開發用的 service-worker.js 內容固定，不會出現提示。
//   3. 使用者按下提示列：送 SKIP_WAITING 給等待中的 Service Worker → 收到 controllerchange 後整頁重新載入，只重新載入一次。
//      別的分頁先套用了新版（這個分頁的 controller 被換掉、但不是這裡按的）：也顯示提示列，按下時直接重新載入（不強制，可能正在編輯）。
//   安裝失敗（從 installing 直接變成 redundant）：經瀏覽器錯誤回報記進伺服器日誌（reportInstallFailure）。
(() => {
    const updateCheckInterval = 30 * 60 * 1000;
    let registration = null;
    let lastUpdateCheck = Date.now();
    let updateListener = null;
    let updateReady = false;
    let applying = false;
    let reloading = false;

    function notifyUpdate() {
        updateReady = true;
        if (updateListener) {
            updateListener.invokeMethodAsync('OnUpdateAvailable').catch(() => { /* 元件已關閉 */ });
        }
    }

    // 新版安裝失敗：某個檔案下載失敗，或與資產清單的完整性雜湊不符（例如有人手改了發佈輸出裡的檔案）時，新版直接變成 redundant，
    // 使用者靜靜地留在舊版（或第一次就沒有離線功能）。經瀏覽器錯誤回報（client-error-reporter.js）送到伺服器日誌，讓維運看得到；
    // 回報要登入，登入前先暫存、登入後才送出。
    function reportInstallFailure(worker) {
        const message = 'Service worker installation failed; the browser keeps using the previous version. Check that the deployed files match service-worker-assets.js.';
        console.error(message, worker.scriptURL);
        if (window.appClientErrors) {
            window.appClientErrors.report('error', message, '', worker.scriptURL);
        }
    }

    function watchInstalling(worker) {
        if (!worker) {
            return;
        }

        let installed = false;
        worker.addEventListener('statechange', () => {
            if (worker.state === 'installed' || worker.state === 'activated') {
                installed = true;
            }

            // 已經有 Service Worker 控制這個頁面時，新版裝好會停在 waiting。
            if (worker.state === 'installed' && navigator.serviceWorker.controller) {
                notifyUpdate();
            } else if (worker.state === 'redundant' && !installed) {
                reportInstallFailure(worker);
            }
        });
    }

    async function register() {
        if (!('serviceWorker' in navigator)) {
            return;
        }

        const hadController = !!navigator.serviceWorker.controller;
        navigator.serviceWorker.addEventListener('controllerchange', () => {
            if (applying) {
                if (!reloading) {
                    reloading = true;
                    window.location.reload();
                }
            } else if (hadController) {
                notifyUpdate();
            }
        });

        try {
            // updateViaCache: 'none'：檢查新版時連 service-worker-assets.js 也一律向伺服器確認。
            registration = await navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' });
        } catch (error) {
            console.warn('Service worker registration failed.', error);
            return;
        }

        if (registration.waiting && navigator.serviceWorker.controller) {
            notifyUpdate();
        }

        watchInstalling(registration.installing);
        registration.addEventListener('updatefound', () => watchInstalling(registration.installing));

        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState !== 'visible' || Date.now() - lastUpdateCheck < updateCheckInterval) {
                return;
            }

            lastUpdateCheck = Date.now();
            registration.update().catch(() => { /* 離線或伺服器暫時連不上：下次再查 */ });
        });
    }

    window.appPwa = {
        // 瀏覽器的連線狀態：回傳目前是否連線，之後每次變化呼叫 .NET 的 OnConnectivityChanged。
        watchConnectivity: (dotnetRef) => {
            const notify = () => dotnetRef.invokeMethodAsync('OnConnectivityChanged', navigator.onLine).catch(() => { /* 已關閉 */ });
            window.addEventListener('online', notify);
            window.addEventListener('offline', notify);
            return navigator.onLine;
        },

        // 新版本提示列訂閱；.NET 還沒準備好之前就偵測到的新版，訂閱時立即通知。
        watchUpdates: (dotnetRef) => {
            updateListener = dotnetRef;
            if (updateReady) {
                notifyUpdate();
            }
        },

        // 使用者按下「有新版本」：請等待中的 Service Worker 接手，換好後（controllerchange）重新載入一次。
        applyUpdate: () => {
            const waiting = registration && registration.waiting;
            if (!waiting) {
                // 沒有等待中的新版（別的分頁已經套用）：直接重新載入就是新版。
                window.location.reload();
                return;
            }

            applying = true;
            waiting.postMessage({ type: 'SKIP_WAITING' });
        },
    };

    register();
})();
