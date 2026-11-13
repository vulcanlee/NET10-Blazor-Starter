using System.Text.Json;
using Microsoft.Extensions.Options;
using MyProject.Dtos.Commons;
using MyProject.Web.Configuration;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 正式環境未處理例外的回應（0.10.8 起，WASM 遷移 M9；取代重跑 Server 版 <c>/Error</c> 頁）。
/// API 請求回 500 <see cref="ApiResult"/>（帶追蹤碼）。其他請求（0.10.10 起只剩 Google 登入的導向端點與同站台的前端靜態檔）
/// 是瀏覽器的整頁請求，把錯誤追蹤碼放進網址導到前端的錯誤頁：<c>302 /Error?trace={追蹤碼}</c>；
/// 雙站台時導到 <c>ClientSettings:BaseUrl</c> 的錯誤頁（只用設定值，不採用請求的 Host）。
/// 例外本身已由請求日誌中介軟體以 Error 記過；這裡只記「使用者被帶到錯誤頁」，靠追蹤碼與那筆對上。
/// </summary>
public sealed class ErrorPageRedirect
{
    public const string ErrorPath = "/Error";

    private ErrorPageRedirect()
    {
    }

    public static async Task HandleAsync(HttpContext context)
    {
        var traceCode = context.TraceIdentifier;
        var logger = context.RequestServices.GetRequiredService<ILogger<ErrorPageRedirect>>();

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";
            var result = ApiResult.FailureResult("系統發生未預期的錯誤，請稍後再試。", StatusCodes.Status500InternalServerError);
            result.TraceId = traceCode;
            await context.Response.WriteAsync(JsonSerializer.Serialize(result));
            return;
        }

        logger.LogWarning("Redirecting the user to the error page. TraceId={TraceId}", traceCode);
        var client = context.RequestServices.GetRequiredService<IOptions<ClientSettings>>().Value;
        context.Response.Redirect(client.FrontendUrl($"{ErrorPath}?trace={Uri.EscapeDataString(traceCode)}"));
    }
}
