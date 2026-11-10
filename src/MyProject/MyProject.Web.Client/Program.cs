using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// WASM 前端的進入點（0.10.0 起）。M1 只是空殼：還沒有任何頁面在 WASM 執行，
// 服務註冊（API 用戶端、Token 存放、驗證狀態）從 M2 開始加入。
var builder = WebAssemblyHostBuilder.CreateDefault(args);

await builder.Build().RunAsync();
