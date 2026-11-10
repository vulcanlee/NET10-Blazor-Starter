// 登入頁載入時清掉 WASM 頁面存在瀏覽器的 Token（0.10.1 起，WASM 遷移 M2）。
// 換人登入（或同一人重新登入）之後，WASM 頁面一律以新的網頁登入重新換一組 Token，不會沿用上一個人的。
// 鍵名與 MyProject.Web.Client 的 js/client-auth.js 相同。
(function () {
  try {
    localStorage.removeItem('myproject.auth');
    sessionStorage.removeItem('myproject.auth');
  } catch {
    // 瀏覽器禁止存取時本來就沒有存 Token。
  }
})();
