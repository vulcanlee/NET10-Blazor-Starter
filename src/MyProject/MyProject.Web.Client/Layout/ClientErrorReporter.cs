using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 頁面的瀏覽器錯誤回報（0.10.1 起）：接住 <c>js/client-error-reporter.js</c> 收集的錯誤，送到 <c>/api/v1/shell/client-errors</c>，
/// 由伺服器端的 <c>BrowserErrorReporter</c> 以同一套規則（截斷、開關、系統例外紀錄）處理。版面完成登入後才註冊。
/// </summary>
public sealed class ClientErrorReporter
{
    private readonly ApiClient api;
    private readonly ClientSession session;
    private readonly ILogger<ClientErrorReporter> logger;

    public ClientErrorReporter(ApiClient api, ClientSession session, ILogger<ClientErrorReporter> logger)
    {
        this.api = api;
        this.session = session;
        this.logger = logger;
    }

    [JSInvokable]
    public async Task<bool> Report(string? kind, string? message, string? stack, string? source, string? path)
    {
        // 還沒登入（登入、重設密碼這類匿名頁）：回報端點要登入，呼叫的話 401 → 刷新失敗 → 被導回登入頁（0.10.8 獨立審查）。
        // 只留在瀏覽器主控台。
        if (session.Info is null)
        {
            logger.LogWarning("Browser error on a page without a signed-in user (not reported to the server). Kind={Kind}, Path={Path}", kind, path);
            return false;
        }

        var result = await api.PostAsync<object>("api/v1/shell/client-errors", new ClientErrorReportDto
        {
            Kind = kind,
            Message = message,
            Stack = stack,
            Source = source,
            Path = path,
        });
        if (!result.Success)
        {
            logger.LogDebug("Client error report was not delivered. Status={Status}", result.StatusCode);
        }

        return result.Success;
    }
}
