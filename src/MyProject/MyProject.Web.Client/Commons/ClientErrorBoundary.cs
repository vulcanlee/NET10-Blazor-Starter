using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// WASM 頁面的最後一道網（0.10.1 起；對應 Server 版的 <c>LoggingErrorBoundary</c>）：接住元件內未處理的例外，
/// 送到伺服器的系統例外紀錄，畫面顯示錯誤提示而不是空白。
/// 0.10.36 起經 <see cref="ClientErrorCapture"/> 回報（先存在瀏覽器、登入後批次送出），並在<b>第一個 await 之前</b>同步取得追蹤碼，
/// 錯誤畫面（ErrorContent）渲染時就顯示得出來（<see cref="ClientErrorCapture.TraceCodeOf"/>）。
/// </summary>
public sealed class ClientErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<ClientErrorBoundary> Logger { get; set; } = default!;

    [Inject]
    private ClientErrorCapture ErrorCapture { get; set; } = default!;

    protected override Task OnErrorAsync(Exception exception)
    {
        var traceCode = ErrorCapture.Capture("boundary", exception);
        Logger.LogError(exception, "Unhandled exception in a WebAssembly component. TraceId={TraceId}", traceCode);
        return Task.CompletedTask;
    }
}
