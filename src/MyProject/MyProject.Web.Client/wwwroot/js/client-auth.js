// WASM 前端的 Token 存放與跨分頁刷新鎖（0.10.1 起，WASM 遷移 M2）。
// 「記住我」的登入存 localStorage（關掉瀏覽器仍維持登入），否則存 sessionStorage（關掉分頁即登出）；兩邊同一個鍵，一次只存一邊。
// ⚠️ 登入頁載入時會清掉這兩邊（js/auth-reset.js），換人登入時不會沿用上一個人的 Token。
const storageKey = 'myproject.auth';

export function read() {
  try {
    return localStorage.getItem(storageKey) ?? sessionStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

export function write(json, persistent) {
  try {
    clear();
    (persistent ? localStorage : sessionStorage).setItem(storageKey, json);
  } catch {
    // 瀏覽器禁止存放（例如隱私模式的限制）：這次載入仍可用，只是重新整理後要再換一次 Token。
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
