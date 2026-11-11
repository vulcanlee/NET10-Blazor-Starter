// WASM 前端的 Token 存放、跨分頁刷新鎖與分頁間溝通（0.10.1 起，WASM 遷移 M2；0.10.8 起純 Token 登入，M9）。
// 「記住我」的登入存 localStorage（關掉瀏覽器仍維持登入），否則存 sessionStorage（關掉分頁即登出）；兩邊同一個鍵，一次只存一邊。
// ⚠️ 0.10.8 起登入頁不再清 storage（以前的 auth-reset.js）：開一個登入分頁不可把其他分頁登出；換人登入時寫入新的 Token，
// 其他分頁經 storage 事件發現換人就整頁重新載入。
const storageKey = 'myproject.auth';
const channelName = 'myproject-auth';

export function read() {
  try {
    return localStorage.getItem(storageKey) ?? sessionStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

export function write(json, persistent) {
  try {
    // 只清另一邊：同一邊先刪再寫會讓其他分頁先收到一次「被清掉」的 storage 事件而誤以為登出。
    (persistent ? sessionStorage : localStorage).removeItem(storageKey);
    (persistent ? localStorage : sessionStorage).setItem(storageKey, json);
  } catch {
    // 瀏覽器禁止存放（例如隱私模式的限制）：這次載入仍可用，只是重新整理後要重新登入。
  }
}

export function clear() {
  try {
    localStorage.removeItem(storageKey);
    sessionStorage.removeItem(storageKey);
  } catch {
    // 同上。
  }
}

// 多個分頁同時到期時只讓一個去刷新（Web Locks）；其餘分頁拿到鎖時先重讀 storage，多半已經有新的 Token。
// 不支援 Web Locks 的瀏覽器直接執行（伺服器端另有 30 秒寬限期，不會因此被登出）。
export async function runLocked(name, dotnetRef, method) {
  if (navigator.locks && navigator.locks.request) {
    return await navigator.locks.request(name, () => dotnetRef.invokeMethodAsync(method));
  }

  return await dotnetRef.invokeMethodAsync(method);
}

// ---------- 一般的 storage 小工具（登入頁的待驗票證、記住裝置 Token、Google 驗證值） ----------

function storageOf(kind) {
  return kind === 'local' ? localStorage : sessionStorage;
}

export function getItem(kind, key) {
  try {
    return storageOf(kind).getItem(key);
  } catch {
    return null;
  }
}

export function setItem(kind, key, value) {
  try {
    storageOf(kind).setItem(key, value);
  } catch {
    // 不能存就算了：最多是下次要重新輸入驗證碼。
  }
}

export function removeItem(kind, key) {
  try {
    storageOf(kind).removeItem(key);
  } catch {
    // 同上。
  }
}

// ---------- 分頁間溝通（BroadcastChannel） ----------
// 沒勾「記住我」時 Token 在 sessionStorage，新開的分頁讀不到：向已開的分頁要。已開的分頁在刷新鎖內以自己的刷新 Token
// 呼叫 session/fork 開一串新的交給它（每個分頁各自一串，不會互相觸發重用偵測）。

function newChannel() {
  return typeof BroadcastChannel === 'function' ? new BroadcastChannel(channelName) : null;
}

/** 向其他分頁要一組 Token；timeoutMs 內沒人回應回 null。 */
export function requestTokens(timeoutMs) {
  const channel = newChannel();
  if (!channel) {
    return Promise.resolve(null);
  }

  const id = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  return new Promise(resolve => {
    const done = value => {
      clearTimeout(timer);
      channel.close();
      resolve(value);
    };
    const timer = setTimeout(() => done(null), timeoutMs);
    channel.onmessage = event => {
      const message = event.data;
      if (message && message.type === 'tokens' && message.id === id && message.json) {
        done(message.json);
      }
    };
    channel.postMessage({ type: 'request', id });
  });
}

/**
 * 通知其他分頁「有人在這個瀏覽器登入了」（0.10.8 獨立審查）：顯示的是另一個人的分頁丟掉自己的 Token 重新載入，
 * 改用這次的登入 —— 同一個瀏覽器只會有一個身分（與 Server 版的 Cookie 相同），新分頁才不會拿到另一個人的 Token。
 */
export function broadcastSignedIn(userId) {
  const channel = newChannel();
  if (channel) {
    channel.postMessage({ type: 'signed-in', userId });
    channel.close();
  }
}

/** 只清這個分頁的 Token（sessionStorage）；localStorage 可能是剛登入那一位的，不能動。 */
export function clearSession() {
  try {
    sessionStorage.removeItem(storageKey);
  } catch {
    // 同 clear。
  }
}

/** 通知其他分頁「這次登入已登出」，它們會重新確認自己的登入。 */
export function broadcastLogout() {
  const channel = newChannel();
  if (channel) {
    channel.postMessage({ type: 'logout' });
    channel.close();
  }
}

/**
 * 監聽其他分頁：storage 事件（localStorage 的 Token 換了或被清掉）與 BroadcastChannel（要 Token、登出）。
 * 回傳的物件用來取消監聽。
 */
export function listen(dotnetRef) {
  const onStorage = event => {
    if (event.storageArea === localStorage && (event.key === storageKey || event.key === null)) {
      dotnetRef.invokeMethodAsync('OnStorageChangedAsync', event.newValue ?? null).catch(() => { /* 頁面已關閉 */ });
    }
  };
  window.addEventListener('storage', onStorage);

  const channel = newChannel();
  if (channel) {
    channel.onmessage = async event => {
      const message = event.data;
      if (!message) {
        return;
      }

      if (message.type === 'logout') {
        dotnetRef.invokeMethodAsync('OnLogoutBroadcastAsync').catch(() => { /* 頁面已關閉 */ });
        return;
      }

      if (message.type === 'signed-in') {
        dotnetRef.invokeMethodAsync('OnSignedInBroadcastAsync', message.userId).catch(() => { /* 頁面已關閉 */ });
        return;
      }

      if (message.type === 'request') {
        try {
          const json = await runLocked('myproject-token-refresh', dotnetRef, 'ForkForAnotherTabAsync');
          if (json) {
            channel.postMessage({ type: 'tokens', id: message.id, json });
          }
        } catch {
          // 這個分頁給不出來（例如自己的 Token 也失效了）：讓別的分頁回應，或讓對方逾時回登入頁。
        }
      }
    };
  }

  return {
    dispose: () => {
      window.removeEventListener('storage', onStorage);
      if (channel) {
        channel.close();
      }
    },
  };
}
