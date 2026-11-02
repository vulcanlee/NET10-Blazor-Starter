using System.ClientModel;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai;

/// <summary>
/// 記帳中介層（0.9.140 起）：每一次送給模型的呼叫都經過這裡。
///
/// <para>
/// 由 <see cref="AiAgentFactory"/> 以 <c>AsAIAgent(..., clientFactory:)</c> 掛上，位置在 MAF 自動加上的
/// <c>FunctionInvokingChatClient</c> <b>下方</b> —— agent 一次執行裡的工具呼叫迴圈會呼叫模型很多次，
/// 每一次都會經過這一層，所以不會漏帳（速查表 §6.7）。
/// </para>
/// <para>
/// 這一層做的事就是 0.9.139 以前 <c>AiChatCompletionClient.CompleteAsync</c> 做的事，原樣搬過來：
/// 用量上限檢查 → 建 <see cref="AiCallCapture"/> → 逾時 → 成功／失敗／空回應／取消分支記 Token 用量 →
/// 單一 <c>finally</c> 記 AI 對話紀錄。失敗在記帳之後以 <see cref="AiCallException"/> 往外丟。
/// </para>
/// <para>
/// ⚠️ 用量、實際模型名稱、finish_reason、內容過濾類別一律從 <b>HTTP 回應原文</b>以
/// <see cref="AiChatResponseParser"/> 解析，不用 SDK 轉換後的物件：這樣記帳結果與 0.9.139 以前逐位元一致，
/// <c>RawUsageJson</c> 也仍然只是 usage 子物件。
/// </para>
/// </summary>
internal sealed class AiAccountingChatClient : DelegatingChatClient
{
    private readonly AiCallContext context;
    private readonly AiCallState state;
    private readonly AiSettings settings;
    private readonly string requestedModel;
    private readonly AiAgentServices services;

    public AiAccountingChatClient(
        IChatClient innerClient,
        AiCallContext context,
        AiCallState state,
        AiSettings settings,
        string requestedModel,
        AiAgentServices services)
        : base(innerClient)
    {
        this.context = context;
        this.state = state;
        this.settings = settings;
        this.requestedModel = requestedModel;
        this.services = services;
    }

    private ILogger Logger => services.Logger;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var characters = messageList.Sum(message => message.Text?.Length ?? 0);

        await EnsureQuotaAsync(cancellationToken);

        var user = services.CurrentUserService.CurrentUser;
        var capture = new AiCallCapture(
            context.Operation,
            settings.GetProvider().ToString(),
            requestedModel,
            user.Account,
            user.Id,
            context.RelatedInfo,
            context.ConversationId);

        // 自己控管逾時：具名 HttpClient 與 SDK 的逾時都已關掉（見 AiAgentFactory）。
        // linked source 讓呼叫端的取消仍然有效，而且事後分得出「使用者取消」與「逾時」。
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(context.Timeout);

        state.Current = capture;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ChatResponse? response = null;
            try
            {
                response = await base.GetResponseAsync(messageList, options, timeoutSource.Token);
            }
            catch (Exception ex) when (IsSdkReadFailureAfterSuccess(ex, capture))
            {
                // HTTP 已成功、上游已計費，只是 SDK 讀不懂回應（例如 Azure 的 choices: []）。
                // 不能當成非預期錯誤丟掉這筆帳：改用我們自己的解析器照常記帳與判斷空回應。
                Logger.LogWarning(
                    ex,
                    "AI SDK could not read a successful response; falling back to the raw payload. Operation={Operation}",
                    context.Operation);
            }

            stopwatch.Stop();
            return await CompleteSuccessAsync(capture, response, messageList.Count, characters, stopwatch.Elapsed);
        }
        catch (AiCallException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動放棄。這是使用者的決定，不是故障 —— 記 Information 而非 Error。
            // Token 用量不記取消，但請求確實送出過，AI 對話紀錄照記。
            stopwatch.Stop();
            capture.SetOutcome(false, AiAnalysisFailureReason.Canceled.ToString());
            Logger.LogInformation(
                "AI chat completion canceled by the caller. Operation={Operation}, ElapsedMs={ElapsedMs}",
                context.Operation,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // 呼叫端沒有取消卻收到取消 = 逾時（我們的 CancelAfter，或傳輸層自己的逾時）。
            stopwatch.Stop();
            capture.ExceptionType = ex.GetType().Name;
            Logger.LogError(
                ex,
                "AI chat completion timed out. Operation={Operation}, TimeoutSeconds={TimeoutSeconds}",
                context.Operation,
                (int)context.Timeout.TotalSeconds);
            await RecordUsageAsync(capture, null, null, null, stopwatch.Elapsed, false, AiAnalysisFailureReason.Timeout);
            throw new AiCallException(
                AiAnalysisFailureReason.Timeout,
                $"AI 分析逾時（超過 {(int)context.Timeout.TotalSeconds} 秒），{context.TimeoutHint}",
                stopwatch.Elapsed,
                detail: ex.ToString(),
                exceptionType: ex.GetType().Name,
                innerException: ex);
        }
        catch (ClientResultException ex) when (ex.GetRawResponse() is not null)
        {
            stopwatch.Stop();
            var status = ex.GetRawResponse()!.Status;
            var payload = capture.ResponseBody ?? ex.GetRawResponse()!.Content.ToString();
            var failure = MapHttpFailure((HttpStatusCode)status, payload, characters, stopwatch.Elapsed, ex);

            // 沒有用量可記，但呼叫確實發生過、上游也可能已經計費，至少要留下次數。
            await RecordUsageAsync(capture, null, null, null, stopwatch.Elapsed, false, failure.Reason);
            throw failure;
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            stopwatch.Stop();
            var transport = UnwrapTransportException(ex);
            capture.ExceptionType = transport.GetType().Name;
            Logger.LogError(ex, "AI chat completion request failed at transport level. Operation={Operation}", context.Operation);
            await RecordUsageAsync(capture, null, null, null, stopwatch.Elapsed, false, AiAnalysisFailureReason.UpstreamError);
            throw new AiCallException(
                AiAnalysisFailureReason.UpstreamError,
                "無法連線到 AI 服務，請確認網路連線與 AiSettings:Endpoint 設定。",
                stopwatch.Elapsed,
                detail: transport.ToString(),
                exceptionType: transport.GetType().Name,
                innerException: ex);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            capture.ExceptionType = ex.GetType().Name;
            capture.SetOutcome(false, AiAnalysisFailureReason.Unexpected.ToString());
            Logger.LogError(ex, "AI chat completion failed unexpectedly. Operation={Operation}", context.Operation);
            throw new AiCallException(
                AiAnalysisFailureReason.Unexpected,
                $"AI 分析失敗：{ex.GetType().Name}。",
                stopwatch.Elapsed,
                detail: ex.ToString(),
                exceptionType: ex.GetType().Name,
                innerException: ex);
        }
        finally
        {
            state.Current = null;

            // 單一記錄點：每個分支都已填好 capture。沒送出（MarkSent 之前就失敗）不記。
            // ⚠️ 不傳 cancellationToken：取消那一支的 token 已經取消了，照傳就記不進去。
            await AiCallCapture.RecordSafelyAsync(services.AiCallLogRecorder, capture, stopwatch.Elapsed, Logger);
        }
    }

    /// <summary>
    /// 串流在第二階段才接上記帳。在那之前<b>明確拒絕</b>，絕不讓任何呼叫繞過記帳走出去。
    /// </summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("AI 串流呼叫尚未接上用量記錄，暫不開放。");

    private async Task<ChatResponse> CompleteSuccessAsync(
        AiCallCapture capture,
        ChatResponse? response,
        int messageCount,
        int characters,
        TimeSpan elapsed)
    {
        var payload = capture.ResponseBody ?? string.Empty;
        var parsed = AiChatResponseParser.Parse(payload);
        capture.ResponseText = parsed.Content ?? string.Empty;
        capture.FinishReason = parsed.FinishReason;
        capture.ResponseModel = parsed.ModelName;
        var usageJson = AiChatResponseParser.ExtractUsageJson(payload);

        // 要求呼叫工具的回應本來就沒有文字內容，不是「付了錢卻沒拿到東西」。
        var requestsTools = response?.Messages.Any(message => message.Contents.OfType<FunctionCallContent>().Any()) == true;

        if (string.IsNullOrWhiteSpace(parsed.Content) && requestsTools == false)
        {
            Logger.LogWarning(
                "AI chat completion returned empty content. Operation={Operation}, FinishReason={FinishReason}, Characters={Characters}",
                context.Operation,
                parsed.FinishReason,
                characters);

            // ⚠️ 這一支最值得記：回應成功、上游照常計費，但使用者什麼都沒拿到。
            await RecordUsageAsync(
                capture, parsed.ModelName, parsed.Usage, usageJson, elapsed, false, AiAnalysisFailureReason.EmptyResponse);

            throw new AiCallException(
                AiAnalysisFailureReason.EmptyResponse,
                DescribeEmptyResponse(parsed),
                elapsed,
                usage: parsed.Usage,
                httpStatus: capture.HttpStatus);
        }

        Logger.LogInformation(
            "AI chat completion completed. Operation={Operation}, Messages={Messages}, Characters={Characters}, "
            + "UsageInput={UsageInput}, UsageOutput={UsageOutput}, ElapsedMs={ElapsedMs}",
            context.Operation,
            messageCount,
            characters,
            parsed.Usage?.InputCount,
            parsed.Usage?.OutputCount,
            (long)elapsed.TotalMilliseconds);

        // 慢 AI 呼叫（LOG-21）。
        var slowThreshold = services.SlowOptions?.CurrentValue.AiCallMs ?? 0;
        if (SlowOperationSettings.IsSlow(elapsed, slowThreshold))
        {
            Logger.LogWarning(
                "Slow AI chat completion. Operation={Operation}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                context.Operation, (long)elapsed.TotalMilliseconds, slowThreshold);
        }

        await RecordUsageAsync(capture, parsed.ModelName, parsed.Usage, usageJson, elapsed, true, AiAnalysisFailureReason.None);

        var modelName = string.IsNullOrEmpty(parsed.ModelName) ? requestedModel : parsed.ModelName;
        state.Add(new AiCallOutcome(parsed.Content ?? string.Empty, modelName, parsed.FinishReason, parsed.Usage, elapsed));

        return response ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, parsed.Content))
        {
            ModelId = modelName,
            FinishReason = string.IsNullOrEmpty(parsed.FinishReason) ? null : new ChatFinishReason(parsed.FinishReason),
        };
    }

    /// <summary>
    /// AI 用量上限（0.9.109 起）：每一次模型呼叫送出之前檢查。並行的呼叫可能讓已用金額小幅超過上限（已接受）。
    /// 擋下時不發請求、不記用量，只寫稽核。
    /// </summary>
    private async Task EnsureQuotaAsync(CancellationToken cancellationToken)
    {
        if (context.EnforceQuota == false)
        {
            return;
        }

        var caller = services.CurrentUserService.CurrentUser;
        var callerId = caller.Id == 0 ? (int?)null : caller.Id;
        var reached = await services.QuotaService.CheckAsync(callerId, cancellationToken);
        if (reached is null)
        {
            return;
        }

        Logger.LogWarning(
            "AI chat completion blocked by usage quota. Operation={Operation}, Scope={Scope}, Period={Period}",
            context.Operation, reached.Scope, reached.Period);
        await services.AuditLogService.WriteAsync(
            AuditActions.Ai.QuotaBlocked,
            success: false,
            actorUserId: callerId,
            actorAccount: string.IsNullOrWhiteSpace(caller.Account) ? null : caller.Account,
            targetType: "AiQuota",
            targetId: $"{reached.Scope}:{reached.Period}",
            detail: $"operation={context.Operation}; limit={reached.LimitTwd}; used={AiQuotaService.Money(reached.UsedTwd)}");
        throw new AiCallException(AiAnalysisFailureReason.QuotaExceeded, AiQuotaService.BlockedMessage(reached), TimeSpan.Zero);
    }

    /// <summary>
    /// 把這次呼叫記進「Token 用量」。
    ///
    /// ⚠️ 記錄點刻意放在這一層而不是畫面層：原始 usage、實際模型名稱、耗時，
    /// 回到畫面之後就已經丟失了。成功與失敗都記 ——
    /// 「回應成功但內容為空」那種情況付了錢卻沒拿到東西，最值得被看見。
    /// 記錄失敗絕不影響結果（Service 內部已全程吞例外）。同時把結果寫到 capture，兩邊以同一個 CallId 關聯。
    /// </summary>
    private async Task RecordUsageAsync(
        AiCallCapture capture,
        string? modelName,
        AiTokenUsage? usage,
        string? rawUsageJson,
        TimeSpan elapsed,
        bool success,
        AiAnalysisFailureReason reason)
    {
        var user = services.CurrentUserService.CurrentUser;

        capture.SetOutcome(success, reason.ToString());

        await services.TokenUsageRecorder.RecordAsync(new TokenUsageEntry
        {
            Operation = context.Operation,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = settings.GetProvider().ToString(),
            Model = string.IsNullOrWhiteSpace(modelName) ? requestedModel : modelName,
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
            CallId = capture.CallId,
        });
    }

    private static bool IsSdkReadFailureAfterSuccess(Exception ex, AiCallCapture capture)
        => ex is not OperationCanceledException
            && ex is not ClientResultException
            && ex is not AiCallException
            && capture.HttpStatus is >= 200 and < 300
            && capture.ResponseBody is not null;

    /// <summary>沒有收到任何 HTTP 回應的失敗（DNS、連線被拒、TLS…）。</summary>
    private static bool IsTransportFailure(Exception ex)
        => ex is HttpRequestException
            || (ex is ClientResultException clientResult && clientResult.GetRawResponse() is null);

    /// <summary>SDK 會把 HttpRequestException 包進 ClientResultException；記錄與訊息用原本那一個。</summary>
    private static Exception UnwrapTransportException(Exception ex)
        => ex is ClientResultException { InnerException: HttpRequestException inner } ? inner : ex;

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
    /// （後者是參數名稱，結構上不可能夾帶送出的內容）。上游的 <c>error.message</c> 只寫進
    /// 日誌，且已在解析時截斷 —— 它可能夾帶提示詞片段，而提示詞裡是日誌或例外內容。
    /// </summary>
    private AiCallException MapHttpFailure(
        HttpStatusCode status,
        string payload,
        int characters,
        TimeSpan elapsed,
        Exception innerException)
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
        Logger.LogError(
            "AI chat completion upstream failure. Operation={Operation}, StatusCode={StatusCode}, ErrorCode={ErrorCode}, "
            + "ErrorParam={ErrorParam}, ErrorType={ErrorType}, ErrorDetail={ErrorDetail}, Characters={Characters}",
            context.Operation,
            (int)status,
            upstream.Code,
            upstream.Param,
            upstream.Type,
            upstream.Message,
            characters);

        return new AiCallException(
            reason,
            message,
            elapsed,
            detail: DescribeHttpFailure(status, upstream),
            httpStatus: (int)status,
            upstream: upstream,
            innerException: innerException);
    }

    /// <summary>給管理員複製用的 HTTP 失敗摘要：只有狀態碼與上游代碼，刻意不含 error.message（理由同上）。</summary>
    internal static string DescribeHttpFailure(HttpStatusCode status, AiUpstreamError upstream)
    {
        var parts = new List<string> { $"HTTP {(int)status}" };
        AddIfPresent("code", upstream.Code);
        AddIfPresent("type", upstream.Type);
        AddIfPresent("param", upstream.Param);
        return string.Join("; ", parts);

        void AddIfPresent(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={value}");
            }
        }
    }

    /// <summary>
    /// 把 400 的錯誤翻成「使用者知道要改哪裡」的訊息。
    ///
    /// 只回代碼是不夠的：實務上最常見的 400 是送了模型不接受的參數，而代碼
    /// （<c>unsupported_value</c>）本身看不出是哪一個。上游的 <c>param</c> 就是答案。
    /// </summary>
    private string DescribeBadRequest(AiUpstreamError upstream)
    {
        // 送出的內容沒有字元上限，所以「內容太長」是主要的失敗模式。
        // 這個錯誤沒有 param，落到通用分支的話訊息會講不出解法 —— 解法由呼叫端提供。
        if (string.Equals(upstream.Code, "context_length_exceeded", StringComparison.OrdinalIgnoreCase))
        {
            return context.ContextLengthExceededMessage;
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
            ? $"AI 服務拒絕本次請求，請確認模型設定與{context.SubmittedContentLabel}。"
            : $"AI 服務拒絕本次請求（代碼 {upstream.Code}），請確認模型設定與{context.SubmittedContentLabel}。";
    }
}
