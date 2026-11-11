using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace MyProject.Web.Streaming;

/// <summary>
/// NDJSON 串流回應（0.10.5 起，WASM 遷移 M6；AI 分析逐字顯示用，不用 SignalR，理由見遷移分析報告 §5.4）。
/// 每個項目序列化成一行 JSON，寫完立刻送出（關閉回應緩衝），瀏覽器邊收邊顯示。
///
/// <para>
/// ⚠️ 使用者關閉對話窗或按「停止產生」時，瀏覽器會中斷請求：伺服器的 <c>RequestAborted</c> 觸發，
/// 產生項目的一方（例如 AI 服務）自己收到取消並送出最後一筆「已取消」的結果。
/// 這裡<b>不</b>拿 <c>RequestAborted</c> 去中斷列舉，寫不出去之後也繼續把序列讀完，只是不再寫出 ——
/// 呼叫端在序列最後做的收尾（稽核、記帳）才一定會執行。
/// </para>
/// <para>
/// IIS：<c>application/x-ndjson</c> 不在 IIS 動態壓縮的預設類型內，不會被壓縮；另外送 <c>Cache-Control: no-cache, no-transform</c>
/// 與 <c>X-Accel-Buffering: no</c>，避免中間的代理伺服器把整段收齊才轉送（見 web.config 註解與部署文件）。
/// </para>
/// </summary>
public sealed class NdjsonResult<T> : IActionResult
{
    public const string ContentType = "application/x-ndjson; charset=utf-8";

    private readonly IAsyncEnumerable<T> items;

    public NdjsonResult(IAsyncEnumerable<T> items)
    {
        this.items = items;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var clientGone = false;
        try
        {
            // 先把標頭送出去：AI 第一個字可能要等很久，瀏覽器不必一直等到那時候才知道請求成功。
            await response.StartAsync(http.RequestAborted);
        }
        catch (Exception ex) when (IsClientGone(ex, http))
        {
            clientGone = true;
        }

        await foreach (var item in items)
        {
            if (clientGone)
            {
                continue;
            }

            try
            {
                await JsonSerializer.SerializeAsync(response.Body, item, JsonSerializerOptions.Web, http.RequestAborted);
                await response.Body.WriteAsync("\n"u8.ToArray(), http.RequestAborted);
                await response.Body.FlushAsync(http.RequestAborted);
            }
            catch (Exception ex) when (IsClientGone(ex, http))
            {
                clientGone = true;
            }
        }
    }

    private static bool IsClientGone(Exception ex, HttpContext http)
        => ex is OperationCanceledException or IOException || http.RequestAborted.IsCancellationRequested;
}
