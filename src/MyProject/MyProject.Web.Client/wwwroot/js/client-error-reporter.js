// 瀏覽器端錯誤與事件回報（0.9.79 起，日誌與例外處理 PRD LOG-20；0.10.36 起改成「暫存區＋批次送出」，LOG-23）。
//
// 這支 JS 是前端唯一的回報管線，排在所有腳本之前載入（index.html），WASM 還沒啟動前發生的錯誤也接得住：
// - 錯誤（Error，伺服器記進系統例外紀錄）：window 'error'、'unhandledrejection'、WASM 啟動失敗（app-start.js）、
//   Service Worker 安裝失敗（pwa.js），以及 .NET 端交過來的例外（ClientErrorCapture：錯誤邊界、框架記下的、頁面 catch 到的）。
// - 事件（伺服器只寫日誌檔）：資源載入失敗（<img>／<script>／<link>，Warning）、內容安全政策擋下的內容（Warning）、
//   一段連線中斷（ApiClient 每次呼叫都告訴這裡；有網路卻連不上伺服器 Warning，瀏覽器自己離線 Information）。
//
// 每筆先存進 localStorage（重新整理、登入前、離線時都不會遺失；上限 30 筆、7 天、約 256KB，超過丟最舊的並計數），
// 登入後由 ClientMainLayout 註冊送件者（Layout/ClientErrorReporter.SendAsync），一批最多 20 筆送到伺服器，處理完的才移除。
// 追蹤碼在這裡（或 .NET 端）產生，畫面立即顯示；伺服器沿用同一個碼，管理員查得到。
// 錯誤附上最近 20 筆操作軌跡（換頁、API 呼叫；只有路徑，不含查詢字串與輸入內容）。
// 長度上限與過濾在這裡先做一次，伺服器端還會再做一次（不信任前端）。localStorage 不能用時（私密模式、被封鎖）改存在記憶體。
(function () {
    'use strict';

    const storageKey = 'app.clientReports.v1';
    const episodeKey = 'app.networkEpisode.v1';
    const maxItems = 30;
    const maxAgeMs = 7 * 24 * 60 * 60 * 1000;
    const maxStorageChars = 256 * 1024;
    const maxBatch = 20;
    const maxMessageLength = 1000;
    const maxStackLength = 4000;
    const maxSourceLength = 500;
    const maxBreadcrumbs = 20;
    const maxEpisodePaths = 10;
    const dedupeWindowMs = 10000;
    const retryDelayMs = 60000;
    // 與伺服器 TraceCode、.NET 端 ClientErrorCapture 相同的字元（Crockford Base32，去掉 I／L／O／U；TraceCodeAlphabetTests 守門）。
    const traceCodeAlphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
    const errorKinds = ['error', 'unhandledrejection', 'boundary', 'unhandled', 'caught', 'sw-install', 'boot'];

    // 瀏覽器外掛注入的腳本、已知無害的瀏覽器訊息，不是本系統的錯誤。
    const extensionPattern = /(chrome|moz|safari|ms-browser|edge)-extension:\/\//i;
    const ignoredMessages = [/ResizeObserver loop/i, /^Script error\.?$/i];

    let sender = null;
    // Blazor 啟動完成前（app-start.js 呼叫 started）發生的 JS 錯誤一律記成 boot：.NET 11 的啟動失敗（程式檔下載失敗、
    // 完整性驗證不符）不會讓 Blazor.start() 失敗，而是變成幾筆未處理的 Promise 拒絕（0.10.36 實機驗證）。
    let blazorStarted = false;
    let signedInUserId = null;
    let appBuild = '';
    let memoryState = { items: [], dropped: 0 };
    let memoryEpisode = null;
    let flushing = false;
    let retryTimer = null;
    const breadcrumbs = [];
    const recent = new Map();
    const reportedOnce = new Set();

    function newTraceCode() {
        const bytes = new Uint8Array(8);
        crypto.getRandomValues(bytes);
        let code = '';
        for (const value of bytes) {
            code += traceCodeAlphabet[value % traceCodeAlphabet.length];
        }

        return code;
    }

    function isTraceCode(code) {
        return typeof code === 'string' && code.length === 8 && [...code].every(ch => traceCodeAlphabet.includes(ch));
    }

    // 只留路徑：查詢字串與片段可能含個資或 token。
    function pathOf(url) {
        try {
            return new URL(String(url || ''), document.baseURI).pathname;
        } catch {
            const text = String(url || '');
            const cut = text.search(/[?#]/);
            return cut >= 0 ? text.slice(0, cut) : text;
        }
    }

    function clip(value, max) {
        return String(value || '').slice(0, max);
    }

    // ---- 暫存區（localStorage；不能用時改存在記憶體）----

    function readJson(key, fallback) {
        try {
            const raw = window.localStorage.getItem(key);
            return raw ? JSON.parse(raw) : fallback;
        } catch {
            // 讀不到（私密模式、被封鎖、內容損毀）：用記憶體裡的那份。
            return fallback;
        }
    }

    function writeJson(key, value) {
        try {
            if (value === null) {
                window.localStorage.removeItem(key);
            } else {
                window.localStorage.setItem(key, JSON.stringify(value));
            }
        } catch {
            // 寫不進去（配額滿、被封鎖）：只留在記憶體，這個分頁關掉前照樣送得出去。
        }
    }

    function readState() {
        const state = readJson(storageKey, memoryState);
        return {
            items: Array.isArray(state.items) ? state.items : [],
            dropped: Number(state.dropped) || 0
        };
    }

    function prune(state) {
        const now = Date.now();
        const fresh = state.items.filter(item => now - Date.parse(item.occurredAt || 0) <= maxAgeMs);
        state.dropped += state.items.length - fresh.length;
        state.items = fresh;
        while (state.items.length > maxItems
            || (state.items.length > 0 && JSON.stringify(state).length > maxStorageChars)) {
            state.items.shift();
            state.dropped += 1;
        }
    }

    // 讀出最新的一份再改（其他分頁可能剛改過），改完寫回。
    function updateState(change) {
        const state = readState();
        change(state);
        prune(state);
        memoryState = state;
        writeJson(storageKey, state);
        return state;
    }

    function enqueue(item) {
        item.id = Date.now().toString(36) + '-' + newTraceCode();
        item.occurredAt = new Date().toISOString();
        item.appBuild = appBuild;
        item.userAgent = clip(navigator.userAgent, 300);
        item.signedInUserId = signedInUserId;
        item.path = item.path || window.location.pathname;
        updateState(state => state.items.push(item));
        scheduleFlush(0);
    }

    // ---- 錯誤 ----

    function shouldIgnore(message, stack, source) {
        if (extensionPattern.test(source || '') || extensionPattern.test(stack || '')) {
            return true;
        }

        return ignoredMessages.some(pattern => pattern.test(message || ''));
    }

    // 回傳追蹤碼；被過濾掉的回 null。同一個錯誤 10 秒內只記一次（回傳第一次的碼）。
    function capture(kind, message, stack, source, traceCode) {
        if (shouldIgnore(message, stack, source)) {
            return null;
        }

        let safeKind = errorKinds.includes(kind) ? kind : 'error';
        if (!blazorStarted && (safeKind === 'error' || safeKind === 'unhandledrejection')) {
            safeKind = 'boot';
        }

        const text = clip(message || '(no message)', maxMessageLength);
        const key = safeKind + '|' + text + '|' + window.location.pathname;
        const now = Date.now();
        const previous = recent.get(key);
        if (previous && now - previous.at < dedupeWindowMs) {
            return previous.code;
        }

        const code = isTraceCode(traceCode) ? traceCode : newTraceCode();
        recent.set(key, { at: now, code: code });
        enqueue({
            kind: safeKind,
            message: text,
            stack: clip(stack, maxStackLength),
            source: clip(source, maxSourceLength),
            traceCode: code,
            breadcrumbs: breadcrumbs.slice()
        });
        return code;
    }

    // ---- 事件：資源載入失敗、內容安全政策 ----

    function reportOnce(key, item) {
        if (reportedOnce.has(key)) {
            return;
        }

        reportedOnce.add(key);
        enqueue(item);
    }

    function resourceFailed(element) {
        const tag = (element.tagName || '').toLowerCase();
        if (!['img', 'script', 'link'].includes(tag)) {
            return;
        }

        const url = element.currentSrc || element.src || element.href;
        // 離線時載入失敗是預期的（連線中斷另有紀錄），不算資源問題。
        if (!url || navigator.onLine === false) {
            return;
        }

        const path = clip(pathOrOrigin(url), maxSourceLength);
        reportOnce('resource|' + path, { kind: 'resource', message: tag, source: path });
    }

    // 已知、無害的違規：AntDesign 頁籤 HTML 上的 ondragover="event.preventDefault();"（本專案頁籤不開放拖曳；速查表 §6.33）。
    function isKnownCspNoise(event) {
        const target = event.target;
        return event.effectiveDirective === 'script-src-attr'
            && event.blockedURI === 'inline'
            && target && typeof target.getAttribute === 'function'
            && target.getAttribute('ondragover') === 'event.preventDefault();';
    }

    function cspViolated(event) {
        if (isKnownCspNoise(event)) {
            return;
        }

        const directive = clip(event.effectiveDirective || event.violatedDirective, 100);
        const blocked = clip(event.blockedURI === 'inline' || event.blockedURI === 'eval' ? event.blockedURI : pathOrOrigin(event.blockedURI), maxSourceLength);
        reportOnce('csp|' + directive + '|' + blocked, { kind: 'csp', message: directive, source: blocked });
    }

    // 別的網站的網址留來源加路徑（看得出是哪一個外部網站），一律不留查詢字串。
    function pathOrOrigin(url) {
        try {
            const parsed = new URL(String(url || ''), document.baseURI);
            return parsed.origin === window.location.origin ? parsed.pathname : parsed.origin + parsed.pathname;
        } catch {
            return String(url || '').split(/[?#]/)[0];
        }
    }

    // ---- 操作軌跡與連線中斷 ----

    function addBreadcrumb(crumb) {
        crumb.at = new Date().toISOString();
        breadcrumbs.push(crumb);
        if (breadcrumbs.length > maxBreadcrumbs) {
            breadcrumbs.shift();
        }
    }

    // 一段連線中斷：第一次連不上開始，之後第一次有回應時結束並記一筆。存在 localStorage：
    // 中斷期間重新整理（例如恢復連線時自動重新載入）也接得上。
    function trackConnectivity(path, status) {
        const episode = readJson(episodeKey, memoryEpisode);
        const now = new Date().toISOString();
        if (status === 0) {
            const next = episode || { startedAt: now, failedCalls: 0, paths: [], browserOnline: false };
            next.lastFailureAt = now;
            next.failedCalls += 1;
            // 只要有一次失敗發生在「瀏覽器認為有網路」時，就是伺服器那一端的問題。
            next.browserOnline = next.browserOnline || navigator.onLine !== false;
            if (!next.paths.includes(path) && next.paths.length < maxEpisodePaths) {
                next.paths.push(path);
            }

            memoryEpisode = next;
            writeJson(episodeKey, next);
            return;
        }

        if (episode) {
            memoryEpisode = null;
            writeJson(episodeKey, null);
            enqueue({
                kind: 'network',
                startedAt: episode.startedAt,
                lastFailureAt: episode.lastFailureAt,
                recoveredAt: now,
                failedCalls: episode.failedCalls,
                paths: episode.paths,
                browserOnline: episode.browserOnline
            });
        }
    }

    // ---- 送出 ----

    function scheduleFlush(delay) {
        if (!sender) {
            return;
        }

        clearTimeout(retryTimer);
        retryTimer = setTimeout(flush, delay);
    }

    // 同一個瀏覽器開了好幾個分頁時只讓一個送（Web Locks；http 非 localhost 時沒有，就各自送，伺服器可能收到重複的）。
    function flush() {
        if (!sender || flushing) {
            return;
        }

        if (navigator.locks && navigator.locks.request) {
            navigator.locks.request('app-client-reports', { ifAvailable: true }, lock => lock ? send() : null)
                .catch(() => scheduleFlush(retryDelayMs));
        } else {
            send();
        }
    }

    async function send() {
        flushing = true;
        try {
            for (;;) {
                const state = updateState(() => { });
                if (state.items.length === 0 && state.dropped === 0) {
                    return;
                }

                const items = state.items.slice(0, maxBatch);
                const dropped = state.dropped;
                const result = await sender.invokeMethodAsync('SendAsync', { items: items, dropped: dropped });
                if (!result) {
                    // 沒送出（連不上、還沒登入、伺服器拒絕）：留著，稍後再試。
                    scheduleFlush(retryDelayMs);
                    return;
                }

                const done = new Set(result.doneIds || []);
                updateState(current => {
                    current.items = current.items.filter(item => !done.has(item.id));
                    current.dropped = Math.max(0, current.dropped - dropped);
                });

                if (result.retryAfterSeconds) {
                    scheduleFlush(result.retryAfterSeconds * 1000);
                    return;
                }

                if (done.size === 0) {
                    return;
                }
            }
        } catch {
            // 送件者已釋放（換版面、登出）或 .NET 端出錯：留在暫存區，稍後再試；不可再丟出錯誤，否則會自己觸發自己。
            scheduleFlush(retryDelayMs);
        } finally {
            flushing = false;
        }
    }

    // ---- 監聽 ----

    // capture 階段：資源載入失敗的 error 事件不會冒泡到 window，只有 capture 階段收得到。
    window.addEventListener('error', event => {
        const target = event.target;
        if (target && target !== window && target.nodeType === 1) {
            resourceFailed(target);
            return;
        }

        const location = event.filename ? `${event.filename}:${event.lineno}:${event.colno}` : '';
        capture('error', event.message, event.error && event.error.stack, location);
    }, true);

    window.addEventListener('unhandledrejection', event => {
        const reason = event.reason;
        const message = reason && reason.message ? reason.message : String(reason);
        capture('unhandledrejection', message, reason && reason.stack, '');
    });

    document.addEventListener('securitypolicyviolation', cspViolated);

    window.addEventListener('online', () => scheduleFlush(0));

    // 刻意不提供 unregister：版面切換時舊參考的釋放與新參考的註冊順序不固定，
    // 由「後註冊者取代前者」即可；舊參考已釋放時 send 的失敗會被吞掉，稍後由新的送。
    window.appClientErrors = {
        // 登入後由 ClientMainLayout 呼叫：之前暫存的（含登入前發生的）這時才送出。
        register: (dotNetReference, userId) => {
            sender = dotNetReference;
            signedInUserId = typeof userId === 'number' ? userId : null;
            scheduleFlush(0);
        },

        // 主動回報一個錯誤，回傳追蹤碼（.NET 端的 ClientErrorCapture、pwa.js、app-start.js）。
        report: (kind, message, stack, source, traceCode) => capture(kind, message, stack, source, traceCode),

        // ApiClient 每次呼叫完成（status 0＝連不上伺服器）。
        api: (method, url, status, elapsedMs, traceCode) => {
            const path = pathOf(url);
            addBreadcrumb({
                kind: 'api',
                method: clip(method, 10),
                path: path,
                status: status,
                elapsedMs: elapsedMs,
                traceCode: isTraceCode(traceCode) ? traceCode : null
            });
            trackConnectivity(path, status);
        },

        // 換頁（ClientRoutes）。
        nav: (path) => addBreadcrumb({ kind: 'nav', path: pathOf(path) }),

        // Blazor 啟動完成（app-start.js）：之後的錯誤不再當成啟動失敗。
        started: () => {
            blazorStarted = true;
        },

        // 前端程式的指紋（Program.cs）。
        setContext: (context) => {
            appBuild = clip(context && context.appBuild, 50);
        }
    };
})();
