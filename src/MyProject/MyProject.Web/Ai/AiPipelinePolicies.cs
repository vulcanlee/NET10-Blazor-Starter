using System.ClientModel.Primitives;
using System.Text;

namespace MyProject.Web.Ai;

/// <summary>
/// 把每一次 HTTP 的請求 body、狀態碼與回應原文寫進「AI 對話紀錄」的擷取物件（0.9.140 起）。
///
/// <para>
/// 為什麼放在 SDK 管線而不是 <c>HttpClient</c> 的 DelegatingHandler：管線是我們在
/// <see cref="AiAgentFactory"/> 裡自己組的，正式環境與測試（假的 HttpMessageHandler）走的是同一條路，
/// 不會出現「測試沒經過擷取層」的落差。
/// </para>
/// <para>
/// ⚠️ 只讀 body，<b>絕不碰標頭</b> —— API 金鑰在標頭上（規則同 <see cref="AiCallCapture"/>）。
/// 放在 <see cref="PipelinePosition.BeforeTransport"/>：SDK 的重試已關掉，每次送出就是一次 HTTP。
/// </para>
/// </summary>
internal sealed class AiHttpCapturePolicy : PipelinePolicy
{
    private readonly AiCallState state;

    public AiHttpCapturePolicy(AiCallState state)
    {
        this.state = state;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var capture = BeforeSend(message, ReadRequestBody(message));
        ProcessNext(message, pipeline, currentIndex);
        AfterSend(message, capture);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var capture = BeforeSend(message, await ReadRequestBodyAsync(message));
        await ProcessNextAsync(message, pipeline, currentIndex);
        AfterSend(message, capture);
    }

    private AiCallCapture? BeforeSend(PipelineMessage message, string body)
    {
        var capture = state.Current;
        if (capture is not null && message.Request.Uri is not null)
        {
            capture.SetRequest(message.Request.Uri, body);
            capture.MarkSent();
        }

        return capture;
    }

    private static void AfterSend(PipelineMessage message, AiCallCapture? capture)
    {
        if (capture is null || message.Response is null)
        {
            return;
        }

        capture.HttpStatus = message.Response.Status;

        // 非串流的回應（含錯誤回應）SDK 會先整段緩衝，這裡讀到的就是完整原文。
        // 串流回應不緩衝，由記帳中介層自己拼（第二階段）。
        if (message.BufferResponse)
        {
            capture.ResponseBody = message.Response.Content.ToString();
        }
    }

    private static string ReadRequestBody(PipelineMessage message)
    {
        if (message.Request.Content is null)
        {
            return string.Empty;
        }

        using var buffer = new MemoryStream();
        message.Request.Content.WriteTo(buffer, message.CancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<string> ReadRequestBodyAsync(PipelineMessage message)
    {
        if (message.Request.Content is null)
        {
            return string.Empty;
        }

        using var buffer = new MemoryStream();
        await message.Request.Content.WriteToAsync(buffer, message.CancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// Azure OpenAI 改用 <c>api-key</c> 標頭認證（0.9.140 起）。
///
/// OpenAI SDK 預設把金鑰放在 <c>Authorization: Bearer</c>。微軟的 C# 範例暗示 Azure v1 端點也接受，
/// 但官方文件沒有明文保證；Azure 的 REST 文件寫的是 <c>api-key</c>。這裡直接換成 <c>api-key</c>，
/// 和 0.9.140 以前手刻 HTTP 送出的標頭完全一致，不賭未明文的行為。
///
/// 放在 <see cref="PipelinePosition.BeforeTransport"/>，確保在 SDK 加上 Authorization 之後才執行。
/// </summary>
internal sealed class AzureApiKeyPolicy : PipelinePolicy
{
    public const string HeaderName = "api-key";

    private readonly string apiKey;

    public AzureApiKeyPolicy(string apiKey)
    {
        this.apiKey = apiKey;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Apply(PipelineMessage message)
    {
        message.Request.Headers.Remove("Authorization");
        message.Request.Headers.Set(HeaderName, apiKey);
    }
}
