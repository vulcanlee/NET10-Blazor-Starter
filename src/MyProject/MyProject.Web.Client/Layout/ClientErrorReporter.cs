using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 頁面的瀏覽器錯誤回報（0.10.1 起）：接住 <c>js/client-error-reporter.js</c> 收集的錯誤，送到 <c>/api/v1/shell/client-errors</c>，
/// 由伺服器端的 <c>BrowserErrorReporter</c> 以同一套規則（截斷、開關、系統例外紀錄）處理。版面完成登入後才註冊。
/// </summary>
public sealed class ClientErrorReporter
{
    private readonly ApiClient api;
    private readonly ILogger<ClientErrorReporter> logger;

    public ClientErrorReporter(ApiClient api, ILogger<ClientErrorReporter> logger)
    {
        this.api = api;
        this.logger = logger;
    }

    [JSInvokable]
    public async Task<bool> Report(string? kind, string? message, string? stack, string? source, string? path)
    {
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
