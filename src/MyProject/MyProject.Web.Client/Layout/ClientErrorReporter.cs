using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// 瀏覽器端回報的送件者（0.10.1 起；0.10.36 起改成批次）：<c>js/client-error-reporter.js</c> 把暫存在瀏覽器的錯誤與事件
/// 一批批交給這裡，送到 <c>/api/v1/shell/client-reports</c>，回傳伺服器處理完的項目，JS 再從暫存區移除。
/// 版面完成登入後才註冊（<c>ClientMainLayout</c>）；還沒登入時一律不送，留在瀏覽器等登入。
/// </summary>
public sealed class ClientErrorReporter
{
    internal const string ReportsUrl = "api/v1/shell/client-reports";

    private readonly ApiClient api;
    private readonly ClientSession session;
    private readonly ILogger<ClientErrorReporter> logger;

    public ClientErrorReporter(ApiClient api, ClientSession session, ILogger<ClientErrorReporter> logger)
    {
        this.api = api;
        this.session = session;
        this.logger = logger;
    }

    /// <returns>伺服器的處理結果；沒送出（還沒登入、連不上、伺服器拒絕）回 null，JS 留著下次再送。</returns>
    [JSInvokable]
    public async Task<ClientReportBatchResultDto?> SendAsync(ClientReportBatchDto batch)
    {
        // 還沒登入（或已登出）：回報端點要登入，呼叫的話 401 → 刷新失敗 → 被導回登入頁（0.10.8 獨立審查）。
        if (session.Info is null)
        {
            return null;
        }

        var result = await api.PostAsync<ClientReportBatchResultDto>(ReportsUrl, batch);
        if (!result.Success)
        {
            logger.LogDebug("Browser reports were not delivered; they stay in the browser. Status={Status}, Count={Count}", result.StatusCode, batch.Items.Count);
            return null;
        }

        return result.Data;
    }
}
