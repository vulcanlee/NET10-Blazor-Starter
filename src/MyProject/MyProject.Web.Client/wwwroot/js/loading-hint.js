// 載入卡片的文字（0.10.35 起）：
// - 「第一次開啟需要下載程式，之後會更快」只在這個瀏覽器還沒成功啟動過時顯示。
//   以前每次整頁載入（登出後、F5）都看得到這句，已經開過的人會以為程式又被重新下載了一次。
//   成功啟動才記下來：Blazor 掛上根元件時會取代 #app 裡的卡片；啟動失敗時卡片不會被取代，就不記。
// - 剛登出（ClientSession.LogoutAsync 在這個分頁的 sessionStorage 寫下一次性記號 app.signedOut）：在 <html> 加 app-signed-out，
//   載入卡片（index.html 的與登入頁讀設定時的 ClientLoadingScreen）改說「已登出，正在回到登入頁…」。
//   登出刻意整頁載入（清掉前一位使用者留在記憶體的資料），這樣看起來是登出的過程，而不是程式重新初始化。
//   記號讀到就刪（之後重新整理照常顯示「正在載入」）；畫面上不再有任何載入卡片（登入表單出來了）就拿掉 class，之後的卡片照常顯示自己的文字。
// - 排在 index.html 的 <head>：卡片畫出來之前就在 <html> 加好 class，文字不會先出現再換掉。
// - 外部檔而不是行內腳本：CSP 只允許 import map 這一個行內腳本。
// storage 在隱私模式或被封鎖時可能拋例外，讀不到就照常顯示。
(() => {
    const loadedBeforeKey = 'app.loadedBefore';
    const signedOutKey = 'app.signedOut';
    const root = document.documentElement;

    try {
        if (localStorage.getItem(loadedBeforeKey) === '1') {
            root.classList.add('app-loaded-before');
        }
    } catch {
        // 讀不到就當作第一次。
    }

    try {
        if (sessionStorage.getItem(signedOutKey) === '1') {
            sessionStorage.removeItem(signedOutKey);
            root.classList.add('app-signed-out');
        }
    } catch {
        // 讀不到就照常顯示「正在載入」。
    }

    document.addEventListener('DOMContentLoaded', () => {
        const app = document.getElementById('app');
        const card = app?.querySelector('.app-loading');
        if (!card) {
            return;
        }

        let started = false;
        const observer = new MutationObserver(() => {
            if (!started && !card.isConnected) {
                started = true;
                try {
                    localStorage.setItem(loadedBeforeKey, '1');
                } catch {
                    // 寫不進去下次照樣顯示提示，不影響使用。
                }
            }

            if (started && !app.querySelector('.app-loading')) {
                root.classList.remove('app-signed-out');
                observer.disconnect();
            }
        });
        observer.observe(app, { childList: true, subtree: true });
    });
})();
