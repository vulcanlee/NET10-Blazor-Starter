using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
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
    private readonly ITokenUsageRecorder tokenUsageRecorder;
    private readonly CurrentUserService currentUserService;

    public AiLogAnalysisService(
        ILogger<AiLogAnalysisService> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService)
    {
        this.logger = logger;
        this.httpClientFactory = httpClientFactory;
        this.optionsMonitor = optionsMonitor;
        this.tokenUsageRecorder = tokenUsageRecorder;
        this.currentUserService = currentUserService;
    }

    /// <summary>
    /// 把這次呼叫記進「Token 用量」。
    ///
    /// ⚠️ 記錄點刻意放在這一層而不是畫面層：原始 usage、實際模型名稱、耗時，
    /// 回到 LogViewerView 之後就已經丟失了。成功與失敗都記 ——
    /// 「回應成功但內容為空」那種情況付了錢卻沒拿到東西，最值得被看見。
    ///
    /// 記錄失敗絕不影響分析結果（Service 內部已全程吞例外）。
    /// </summary>
    private async Task RecordUsageAsync(
        AiSettings settings,
        string? modelName,
        AiTokenUsage? usage,
        string? rawUsageJson,
        TimeSpan elapsed,
        bool success,
        AiAnalysisFailureReason reason)
    {
        var user = currentUserService.CurrentUser;

        await tokenUsageRecorder.RecordAsync(new TokenUsageEntry
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = settings.GetProvider().ToString(),
            Model = string.IsNullOrWhiteSpace(modelName) ? settings.Model : modelName,
            Account = string.IsNullOrWhiteSpace(user.Account) ? null : user.Account,
            UserId = user.Id == 0 ? null : user.Id,
            InputCount = usage?.InputCount,
            OutputCount = usage?.OutputCount,
            TotalCount = usage?.TotalCount,
            CachedInputCount = usage?.CachedInputCount,
            ReasoningCount = usage?.ReasoningCount,
            ElapsedMilliseconds = (long)elapsed.TotalMilliseconds,
            Success = success,
            FailureReason = reason == AiAnalysisFailureReason.None ? null : reason.ToString(),
            RawUsageJson = rawUsageJson,
        });
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

        var prompt = AiLogPromptBuilder.Build(entriesAscending, settings.MaxEntries);

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
                var failure = MapHttpFailure(response.StatusCode, payload, prompt, stopwatch.Elapsed);
                // 沒有用量可記，但呼叫確實發生過、上游也可能已經計費，至少要留下次數。
                await RecordUsageAsync(
                    settings, settings.Model, null, null, stopwatch.Elapsed, false, failure.Reason);
                return failure;
            }

            var parsed = AiChatResponseParser.Parse(payload);
            if (string.IsNullOrWhiteSpace(parsed.Content))
            {
                var message = DescribeEmptyResponse(parsed);

                logger.LogWarning(
                    "AI log analysis returned empty content. FinishReason={FinishReason}, Entries={Entries}",
                    parsed.FinishReason,
                    prompt.IncludedEntryCount);

                // ⚠️ 這一支最值得記：回應成功、上游照常計費，但使用者什麼都沒拿到。
                await RecordUsageAsync(
                    settings, parsed.ModelName, parsed.Usage, AiChatResponseParser.ExtractUsageJson(payload),
                    stopwatch.Elapsed, false, AiAnalysisFailureReason.EmptyResponse);

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

            await RecordUsageAsync(
                settings, parsed.ModelName, parsed.Usage, AiChatResponseParser.ExtractUsageJson(payload),
                stopwatch.Elapsed, true, AiAnalysisFailureReason.None);

            return new AiAnalysisResult
            {
                Success = true,
                Markdown = parsed.Content,
                Usage = parsed.Usage,
                Prompt = prompt,
                ModelName = string.IsNullOrEmpty(parsed.ModelName)
                    ? settings.Model
                    : parsed.ModelName,
                IsTruncatedByLength =
                    string.Equals(parsed.FinishReason, "length", StringComparison.OrdinalIgnoreCase),
                Elapsed = stopwatch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動放棄。這是使用者的決定，不是故障 —— 記 Information 而非 Error，
            // 也不要落到下面的泛型 catch 被寫成「failed unexpectedly」。
            stopwatch.Stop();
            logger.LogInformation(
                "AI log analysis canceled by the caller. Entries={Entries}, ElapsedMs={ElapsedMs}",
                prompt.IncludedEntryCount,
                stopwatch.ElapsedMilliseconds);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Canceled,
                "已取消本次 AI 分析。",
                prompt,
                stopwatch.Elapsed);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested == false)
        {
            // HttpClient 的逾時在 .NET 上表現為 TaskCanceledException（內含 TimeoutException），
            // 所以要用 cancellationToken 是否真的被取消來區分「使用者取消」與「逾時」。
            stopwatch.Stop();
            logger.LogError(ex, "AI log analysis timed out. TimeoutSeconds={TimeoutSeconds}", settings.TimeoutSeconds);
            await RecordUsageAsync(
                settings, settings.Model, null, null, stopwatch.Elapsed, false, AiAnalysisFailureReason.Timeout);
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
            await RecordUsageAsync(
                settings, settings.Model, null, null, stopwatch.Elapsed, false, AiAnalysisFailureReason.UpstreamError);
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
    /// 回應成功但沒有內容時，說明是為什麼。
    ///
    /// ⚠️ <c>finish_reason</c> 為 <c>length</c> 這一支特別重要：推論模型的思考 token 也算進
    /// <c>max_completion_tokens</c>，額度太小就會在產出任何可見文字之前耗盡。使用者拿到的是
    /// 空回應，卻仍要付輸入與思考的費用 —— 只說「請稍後再試」會讓人一再重試、一再付錢。
    /// </summary>
    private static string DescribeEmptyResponse(AiChatParseResult parsed)
    {
        if (string.IsNullOrEmpty(parsed.FilterCategory) == false)
        {
            return $"AI 回應被內容過濾攔下（類別：{parsed.FilterCategory}）。";
        }

        if (string.Equals(parsed.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            return "回應在產出任何內容之前就達到長度上限。請調高 AiSettings:MaxOutputTokens "
                + "或將它設為 null（推論模型的思考 token 也算進這個額度）。";
        }

        return "AI 沒有回傳任何內容，請稍後再試。";
    }

    /// <summary>
    /// 把上游的 HTTP 失敗對應成使用者看得懂的中文訊息。
    ///
    /// ⚠️ 給使用者看的訊息只由固定字串加上 <c>error.code</c> 與 <c>error.param</c> 組成
    /// （後者是參數名稱，結構上不可能夾帶日誌內容）。上游的 <c>error.message</c> 只寫進
    /// 日誌，且已在解析時截斷 —— 它可能夾帶提示詞片段，而提示詞裡是上百筆日誌。
    /// </summary>
    private AiAnalysisResult MapHttpFailure(
        HttpStatusCode status,
        string payload,
        AiPromptBuildResult prompt,
        TimeSpan elapsed)
    {
        AiChatResponseParser.TryGetError(payload, out var upstream);

        var (reason, message) = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (
                AiAnalysisFailureReason.Unauthorized,
                "AI 服務拒絕存取，請確認 AiSettings:ApiKey 是否正確或已過期。"),
            HttpStatusCode.NotFound => (
                AiAnalysisFailureReason.NotConfigured,
                "找不到指定的 AI 部署或模型，請確認 AiSettings 的 Endpoint 與 Model 設定。"),
            HttpStatusCode.TooManyRequests => (
                AiAnalysisFailureReason.RateLimited,
                "AI 服務目前限流，請稍後再試。"),
            HttpStatusCode.BadRequest => (
                AiAnalysisFailureReason.UpstreamError,
                DescribeBadRequest(upstream)),
            _ => (
                AiAnalysisFailureReason.UpstreamError,
                $"AI 服務回應失敗（HTTP {(int)status}），請稍後再試。"),
        };

        // ⚠️ ErrorDetail 是已截斷的上游說明。它是排查這類失敗的唯一線索 ——
        // 只記代碼的話，「哪個參數不被接受」完全看不出來。
        logger.LogError(
            "AI log analysis upstream failure. StatusCode={StatusCode}, ErrorCode={ErrorCode}, "
            + "ErrorParam={ErrorParam}, ErrorType={ErrorType}, ErrorDetail={ErrorDetail}, Entries={Entries}",
            (int)status,
            upstream.Code,
            upstream.Param,
            upstream.Type,
            upstream.Message,
            prompt.IncludedEntryCount);

        return AiAnalysisResult.Failure(reason, message, prompt, elapsed);
    }

    /// <summary>
    /// 把 400 的錯誤翻成「使用者知道要改哪裡」的訊息。
    ///
    /// 只回代碼是不夠的：實務上最常見的 400 是送了模型不接受的參數，而代碼
    /// （<c>unsupported_value</c>）本身看不出是哪一個。上游的 <c>param</c> 就是答案。
    /// </summary>
    private static string DescribeBadRequest(AiUpstreamError upstream)
    {
        // 0.9.7 起送出的日誌不再有字元上限，所以「內容太長」變成主要的失敗模式。
        // 這個錯誤沒有 param，落到通用分支的話訊息會講不出解法。
        if (string.Equals(upstream.Code, "context_length_exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return "送出的日誌量超過模型的內容視窗上限。請在日誌檢視頁縮小時間區間或減少查詢筆數後再試。";
        }

        // 推論模型（o 系列、gpt-5 家族）只接受 temperature 的預設值，
        // 這是切換模型時最常撞到的一個，直接告訴使用者怎麼改。
        if (string.Equals(upstream.Param, "temperature", StringComparison.OrdinalIgnoreCase))
        {
            return "此模型不接受 AiSettings:Temperature 的設定值，請將它設為 null（部分推論模型只接受預設值）。";
        }

        if (string.IsNullOrEmpty(upstream.Param) == false)
        {
            return $"AI 服務不接受參數 {upstream.Param} 的設定值（代碼 {upstream.Code}），請調整 AiSettings 後再試。";
        }

        return string.IsNullOrEmpty(upstream.Code)
            ? "AI 服務拒絕本次請求，請確認模型設定與送出的日誌量。"
            : $"AI 服務拒絕本次請求（代碼 {upstream.Code}），請確認模型設定與送出的日誌量。";
    }
}
