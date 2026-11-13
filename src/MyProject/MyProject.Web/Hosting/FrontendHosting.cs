using System.Text.Json;
using Microsoft.Extensions.Options;
using MyProject.Dtos.Commons;
using MyProject.Web.Configuration;

namespace MyProject.Web.Hosting;

/// <summary>
/// 前端的入口頁 <c>index.html</c>（同站台模式，0.10.10 起）：算一次 CSP（要帶 import map 的雜湊）就快取起來。
/// 檔案來自 Web 根目錄：發佈後是 <c>wwwroot/index.html</c>（MyProject.Web.Client 發佈時處理過指紋與 import map 的那一份），
/// 開發與測試時是 Client 專案建置產生的靜態資產。
/// </summary>
public sealed class FrontendIndexPage
{
    public const string FileName = "index.html";

    private readonly Lazy<string> contentSecurityPolicy;

    public FrontendIndexPage(IWebHostEnvironment environment)
    {
        contentSecurityPolicy = new Lazy<string>(() =>
        {
            var file = environment.WebRootFileProvider.GetFileInfo(FileName);
            string? hash = null;
            if (file.Exists)
            {
                using var reader = new StreamReader(file.CreateReadStream());
                hash = FrontendContentSecurityPolicy.ImportMapHash(reader.ReadToEnd());
            }

            return FrontendContentSecurityPolicy.Build(hash, apiOrigin: null);
        });
    }

    /// <summary>同站台時前端回應的 CSP（connect-src 只有 'self'）。</summary>
    public string ContentSecurityPolicy => contentSecurityPolicy.Value;
}

/// <summary>
/// 前端與 API 路由的收尾（0.10.10 起，WASM 遷移 M11）：
/// <list type="bullet">
/// <item><c>/api/</c> 底下沒有本文的錯誤狀態碼（找不到路由、方法不符…）：回 <see cref="ApiResult"/>（兩種模式都一樣）。</item>
/// <item>同站台（<c>ClientSettings:BaseUrl</c> 留空）：提供前端的靜態檔；不是 API、也沒有對應端點（實體檔案）的 GET 一律回 <c>index.html</c>
/// （前端路由決定顯示哪一頁，找不到時顯示找不到頁面）；<c>_framework</c> 底下找不到的檔案回 404，不回 index.html。</item>
/// <item>雙站台：不提供前端檔案，非 API 路徑一律 404。</item>
/// </list>
/// 0.10.9 以前找不到的網址回 404 後重跑成 <c>/not-found</c>（主機頁渲染），已不需要。
/// </summary>
public static class FrontendHostingExtensions
{
    /// <summary>API 路徑上沒有本文的錯誤狀態碼（找不到路由、方法不符…）補上 <see cref="ApiResult"/>；其他路徑維持空的本文。</summary>
    public static WebApplication UseApiStatusCodePages(this WebApplication app)
    {
        app.UseStatusCodePages(async context =>
        {
            var httpContext = context.HttpContext;
            if (!httpContext.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var statusCode = httpContext.Response.StatusCode;
            var message = statusCode == StatusCodes.Status404NotFound ? "找不到這個 API。" : "API 請求失敗。";
            var result = ApiResult.FailureResult(message, statusCode);
            result.TraceId = httpContext.TraceIdentifier;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(JsonSerializer.Serialize(result));
        });

        return app;
    }

    /// <summary>
    /// 同站台：提供前端的靜態檔（MapStaticAssets：Web 專案參考 MyProject.Web.Client 取得它的靜態資產，含 _framework），
    /// 沒有任何端點對應的 GET／HEAD（不是 <c>/api/</c>、不是 <c>/_framework/</c>）回 <c>index.html</c>。雙站台：什麼都不做，非 API 路徑自然 404。
    /// <para>⚠️ SPA 回退刻意做成「沒有端點時」的中介軟體，不用 <c>MapFallback</c>／<c>MapFallbackToFile</c> 端點：路由的 HTTP 方法與 Content-Type 比對
    /// 先於路由約束，萬用的回退端點會變成那些請求唯一可選的端點 —— <c>/api/</c> 的 POST 變成 405、沒帶 Content-Type 的 API 請求從 401／415 變成 404。</para>
    /// <para>必須排在 <c>UseAuthentication</c>（Google 登入的 <c>/signin-google</c> 在那裡處理）與 Swagger 之後；放在管線最後。</para>
    /// </summary>
    public static WebApplication UseFrontend(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<ClientSettings>>().Value.IsSeparateSite)
        {
            return app;
        }

        app.MapStaticAssets();

        var webRoot = app.Environment.WebRootFileProvider;
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (context.GetEndpoint() is null
                && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && !request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !request.Path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase)
                && webRoot.GetFileInfo(FrontendIndexPage.FileName) is { Exists: true } index)
            {
                var response = context.Response;
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength = index.Length;

                // 入口頁不可快取：部署新版後要立刻拿到新的檔名（指紋）。已經設定的（重設密碼頁的 no-store，見 UseSecurityHeaders）不覆蓋。
                if (string.IsNullOrEmpty(response.Headers.CacheControl))
                {
                    response.Headers.CacheControl = "no-cache";
                }

                if (HttpMethods.IsGet(request.Method))
                {
                    await response.SendFileAsync(index, context.RequestAborted);
                }

                return;
            }

            await next();
        });

        return app;
    }
}
