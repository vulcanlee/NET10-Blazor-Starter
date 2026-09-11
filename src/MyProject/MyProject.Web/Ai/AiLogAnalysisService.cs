using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Ai;

/// <inheritdoc cref="IAiLogAnalysisService" />
public sealed class AiLogAnalysisService : IAiLogAnalysisService
{
    /// <summary>
    /// named client 的名稱。為什麼用 named client 而非 typed client，
    /// 見 ServiceCollectionExtensions.AddApplicationServices 的註解。
    /// </summary>
    public const string HttpClientName = "AiChatCompletions";

    private readonly ILogger<AiLogAnalysisService> logger;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;

    public AiLogAnalysisService(
        ILogger<AiLogAnalysisService> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor)
    {
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
        this.optionsMonitor = optionsMonitor;
    }

    public bool IsAvailable => AiChatEndpoint.Validate(optionsMonitor.CurrentValue) is null;

    public string UnavailableReason => AiChatEndpoint.Validate(optionsMonitor.CurrentValue) ?? string.Empty;

    public async Task<AiAnalysisResult> AnalyzeAsync(
        IReadOnlyList<LogEntry> entriesAscending,
        CancellationToken cancellationToken = default)
    {
        // ⚠️ 在呼叫時才讀設定（CurrentValue），不在建構時快取：Blazor Server 的 DI scope
        // 等於 SignalR circuit，可存活數小時，IOptions 的一次性快照會讓設定變更永遠吃不到。
        var settings = optionsMonitor.CurrentValue;

        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            logger.LogWarning("AI log analysis skipped because settings are incomplete.");
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NotConfigured, invalid);
        }

        var prompt = AiLogPromptBuilder.Build(
            entriesAscending,
            settings.MaxEntries,
            settings.MaxCharactersPerEntry,
            settings.MaxTotalCharacters);

        if (prompt.IsEmpty)
        {
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NoData, "目前沒有可分析的日誌。", prompt);
        }

        var descriptor = AiChatEndpoint.Create(settings);
        var body = AiChatRequestFactory.CreateRequestJson(
            settings,
            AiLogPromptBuilder.ResolveSystemPrompt(settings.SystemPrompt),
            prompt.UserMessage);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, descriptor.RequestUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            // ⚠️ 金鑰只掛在「這一次」的請求上，絕不設到 client.DefaultRequestHeaders：
            // named client 是從池子拿的，設在上面會把金鑰留給後續所有請求，
            // 而且設定重載換了金鑰之後用的還是舊的。
            request.Headers.TryAddWithoutValidation(descriptor.AuthHeaderName, descriptor.AuthHeaderValue);

            using var response = await client.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (response.IsSuccessStatusCode == false)
            {
                return MapHttpFailure(response.StatusCode, payload, prompt, stopwatch.Elapsed);
            }

            var parsed = AiChatResponseParser.Parse(payload);
            if (string.IsNullOrWhiteSpace(parsed.Content))
            {
                var message = string.IsNullOrEmpty(parsed.FilterCategory)
                    ? "AI 沒有回傳任何內容，請稍後再試。"
                    : $"AI 回應被內容過濾攔下（類別：{parsed.FilterCategory}）。";

                logger.LogWarning(
                    "AI log analysis returned empty content. FinishReason={FinishReason}, Entries={Entries}",
                    parsed.FinishReason,
                    prompt.IncludedEntryCount);

                return AiAnalysisResult.Failure(
                    AiAnalysisFailureReason.EmptyResponse,
                    message,
                    prompt,
                    stopwatch.Elapsed,
                    parsed.Usage);
            }

            logger.LogInformation(
                "AI log analysis completed. Entries={Entries}, Characters={Characters}, "
                + "UsageInput={UsageInput}, UsageOutput={UsageOutput}, ElapsedMs={ElapsedMs}",
                prompt.IncludedEntryCount,
                prompt.CharacterCount,
                parsed.Usage?.InputCount,
                parsed.Usage?.OutputCount,
                stopwatch.ElapsedMilliseconds);

            return new AiAnalysisResult
            {
                Success = true,
                Markdown = parsed.Content,
                Usage = parsed.Usage,
                Prompt = prompt,
                ModelName = string.IsNullOrEmpty(parsed.ModelName)
                    ? AiChatEndpoint.ResolveModelField(settings)
                    : parsed.ModelName,
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested == false)
        {
            // HttpClient 的逾時在 .NET 上表現為 TaskCanceledException（內含 TimeoutException），
            // 所以要用 cancellationToken 是否真的被取消來區分「使用者取消」與「逾時」。
            stopwatch.Stop();
            logger.LogError(ex, "AI log analysis timed out. TimeoutSeconds={TimeoutSeconds}", settings.TimeoutSeconds);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Timeout,
                $"AI 分析逾時（超過 {settings.TimeoutSeconds} 秒），請縮小查詢範圍後再試。",
                prompt,
                stopwatch.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "AI log analysis request failed at transport level.");
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.UpstreamError,
                "無法連線到 AI 服務，請確認網路連線與 AiSettings:Endpoint 設定。",
                prompt,
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogError(ex, "AI log analysis failed unexpectedly.");
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Unexpected,
                $"AI 分析失敗：{ex.GetType().Name}。",
                prompt,
                stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// 把上游的 HTTP 失敗對應成使用者看得懂的中文訊息。
    /// ⚠️ 只回吐 <c>error.code</c>，絕不回吐上游 body：body 可能夾帶整份 prompt，
    /// 也就是日誌內容。
    /// </summary>
    private AiAnalysisResult MapHttpFailure(
        HttpStatusCode status,
        string payload,
        AiPromptBuildResult prompt,
        TimeSpan elapsed)
    {
        AiChatResponseParser.TryGetErrorCode(payload, out var code);

        var (reason, message) = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (
                AiAnalysisFailureReason.Unauthorized,
                "AI 服務拒絕存取，請確認 AiSettings:ApiKey 是否正確或已過期。"),
            HttpStatusCode.NotFound => (
                AiAnalysisFailureReason.NotConfigured,
                "找不到指定的 AI 部署或模型，請確認 AiSettings 的 Endpoint、Deployment 與 ApiVersion。"),
            HttpStatusCode.TooManyRequests => (
                AiAnalysisFailureReason.RateLimited,
                "AI 服務目前限流，請稍後再試。"),
            HttpStatusCode.BadRequest => (
                AiAnalysisFailureReason.UpstreamError,
                string.IsNullOrEmpty(code)
                    ? "AI 服務拒絕本次請求，請確認模型設定與送出的日誌量。"
                    : $"AI 服務拒絕本次請求（代碼 {code}），請確認模型設定與送出的日誌量。"),
            _ => (
                AiAnalysisFailureReason.UpstreamError,
                $"AI 服務回應失敗（HTTP {(int)status}），請稍後再試。"),
        };

        logger.LogError(
            "AI log analysis upstream failure. StatusCode={StatusCode}, ErrorCode={ErrorCode}, Entries={Entries}",
            (int)status,
            code,
            prompt.IncludedEntryCount);

        return AiAnalysisResult.Failure(reason, message, prompt, elapsed);
    }
}
