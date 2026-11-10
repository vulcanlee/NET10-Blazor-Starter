using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MyProject.Web.Client.Layout;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// WASM 頁面的最後一道網（0.10.1 起；對應 Server 版的 <c>LoggingErrorBoundary</c>）：接住元件內未處理的例外，
/// 送到伺服器的系統例外紀錄（經 <see cref="ClientErrorReporter"/>），畫面顯示錯誤提示而不是空白。
/// </summary>
public sealed class ClientErrorBoundary : ErrorBoundary
{
    [Inject]
    private ILogger<ClientErrorBoundary> Logger { get; set; } = default!;

    [Inject]
    private ClientErrorReporter Reporter { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "Unhandled exception in a WebAssembly component.");
        try
        {
            await Reporter.Report("error", exception.Message, exception.ToString(), exception.GetType().FullName, NavigationManager.ToBaseRelativePath(NavigationManager.Uri));
        }
        catch (Exception reportException)
        {
            // 回報失敗只記在瀏覽器主控台；不可以讓錯誤處理本身再丟出例外。
            Logger.LogWarning(reportException, "Failed to report the unhandled exception.");
        }
    }
}
