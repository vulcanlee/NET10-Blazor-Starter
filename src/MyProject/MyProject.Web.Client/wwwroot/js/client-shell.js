// WASM 外框的小工具（0.10.1 起，WASM 遷移 M2）。
// 切回這個分頁時通知 .NET 立即更新（通知鈴鐺、公告）；回傳的物件用來取消。
export function onVisible(dotnetRef, method) {
  const handler = () => {
    if (document.visibilityState === 'visible') {
      dotnetRef.invokeMethodAsync(method).catch(() => { /* 元件已關閉 */ });
    }
  };
  document.addEventListener('visibilitychange', handler);
  return { dispose: () => document.removeEventListener('visibilitychange', handler) };
}
