// 啟動 Blazor WebAssembly（0.10.36 起改成手動啟動；index.html 的 blazor.webassembly 腳本設 autostart="false"）。
// 目的：啟動失敗（程式檔下載失敗、完整性驗證不符、瀏覽器不支援…）時接得到，交給 client-error-reporter.js 記成 boot 錯誤。
// ⚠️ .NET 11 的下載失敗多半不會讓 Blazor.start() 失敗，而是變成未處理的 Promise 拒絕；client-error-reporter.js 在
// 這裡通知「已啟動」之前收到的錯誤一律記成 boot，所以兩種情況都接得到。
// 這時還沒有登入，錯誤先存在瀏覽器，等下一次成功啟動並登入後才送到伺服器（同一個瀏覽器）。
// ⚠️ 不能改成行內腳本：CSP 只允許 import map 這一段行內腳本。
(function () {
    'use strict';

    function reportBoot(message, stack) {
        console.error('Blazor WebAssembly failed to start.', message);
        if (window.appClientErrors) {
            window.appClientErrors.report('boot', message, stack || '', 'app-start.js');
        }
    }

    if (!window.Blazor || typeof window.Blazor.start !== 'function') {
        reportBoot('The Blazor WebAssembly script did not load.', '');
        return;
    }

    window.Blazor.start().then(function () {
        if (window.appClientErrors) {
            window.appClientErrors.started();
        }
    }).catch(function (error) {
        reportBoot((error && error.message) || String(error), error && error.stack);
    });
})();
