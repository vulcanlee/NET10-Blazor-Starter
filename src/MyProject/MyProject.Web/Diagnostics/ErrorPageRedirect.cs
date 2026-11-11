using System.Text.Json;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 正式環境未處理例外的回應（0.10.8 起，WASM 遷移 M9；取代重跑 Server 版 <c>/Error</c> 頁）。
/// <c>/Error</c> 改在 WebAssembly 執行，拿不到這次請求的 <c>HttpContext.TraceIdentifier</c>，所以把錯誤追蹤碼放進網址導過去：
/// <c>302 /Error?trace={追蹤碼}</c>。API 請求不導頁，回 500 <see cref="ApiResult"/>（帶同一個追蹤碼）。
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
        context.Response.Redirect($"{ErrorPath}?trace={Uri.EscapeDataString(traceCode)}");
    }
}
