using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
/// 0.9.141 起串流（<see cref="GetStreamingResponseAsync"/>）也走同一套規則。
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
    private readonly AiReasoningEffortSource reasoning;

    public AiAccountingChatClient(
        IChatClient innerClient,
        AiCallContext context,
        AiCallState state,
        AiSettings settings,
        string requestedModel,
        AiAgentServices services,
        AiReasoningEffortSource reasoning)
        : base(innerClient)
    {
        this.context = context;
        this.state = state;
        this.settings = settings;
        this.requestedModel = requestedModel;
        this.services = services;
        this.reasoning = reasoning;
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

        var capture = CreateCapture();

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
            var payload = capture.ResponseBody ?? string.Empty;
            var parsed = await CompleteSuccessAsync(
                capture,
                AiChatResponseParser.Parse(payload),
                AiChatResponseParser.ExtractUsageJson(payload),
                RequestsTools(response?.Messages.SelectMany(message => message.Contents)),
                messageList.Count,
                characters,
                stopwatch.Elapsed);

            return response ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, parsed.Content))
            {
                ModelId = string.IsNullOrEmpty(parsed.ModelName) ? requestedModel : parsed.ModelName,
                FinishReason = string.IsNullOrEmpty(parsed.FinishReason) ? null : new ChatFinishReason(parsed.FinishReason),
            };
        }
        catch (Exception ex) when (ex is not AiCallException)
        {
            stopwatch.Stop();
            var failure = await HandleFailureAsync(ex, capture, string.Empty, characters, stopwatch.Elapsed, cancellationToken);
            if (failure is null)
            {
                throw;
            }

            throw failure;
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
    /// 串流呼叫（0.9.141 起）。記帳規則與 <see cref="GetResponseAsync"/> 相同，差別只在：
    /// <list type="bullet">
    /// <item>用量、模型、finish_reason 在串流<b>結束後</b>才從擷取到的 SSE 原文解析（usage 在最後一個 chunk）。</item>
    /// <item>⚠️ 使用者中途取消時，<b>已經收到文字</b>就記一筆 <c>Canceled</c>、用量空白（有次數無金額）——
    /// 已產生的字上游照樣收費，而 usage 要等串流完整結束才拿得到。還沒收到任何文字就取消則照舊不記用量。
    /// AI 對話紀錄兩種都記，回應文字是已收到的部分。</item>
    /// <item>逾時涵蓋整段串流（<see cref="AiCallContext.Timeout"/>）。</item>
    /// </list>
    /// </summary>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var characters = messageList.Sum(message => message.Text?.Length ?? 0);

        await EnsureQuotaAsync(cancellationToken);

        var capture = CreateCapture();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(context.Timeout);

        state.Current = capture;
        var stopwatch = Stopwatch.StartNew();
        var received = new StringBuilder();
        var requestsTools = false;
        var finished = false;
        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            enumerator = base.GetStreamingResponseAsync(messageList, options, timeoutSource.Token)
                .GetAsyncEnumerator(timeoutSource.Token);

            while (true)
            {
                // ⚠️ C# 不允許在有 catch 的 try 裡 yield，所以只把「取下一筆」包起來，yield 放在外面。
                try
                {
                    if (await enumerator.MoveNextAsync() == false)
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is not AiCallException)
                {
                    stopwatch.Stop();
                    finished = true;
                    capture.CompleteStreamCapture();
                    var failure = await HandleFailureAsync(
                        ex, capture, received.ToString(), characters, stopwatch.Elapsed, cancellationToken);
                    if (failure is null)
                    {
                        throw;
                    }

                    throw failure;
                }

                var update = enumerator.Current;
                received.Append(update.Text);
                requestsTools |= RequestsTools(update.Contents);
                yield return update;
            }

            stopwatch.Stop();
            finished = true;
            capture.CompleteStreamCapture();
            var payload = capture.ResponseBody ?? string.Empty;
            await CompleteSuccessAsync(
                capture,
                AiChatResponseParser.ParseStream(payload),
                AiChatResponseParser.ExtractStreamUsageJson(payload),
                requestsTools,
                messageList.Count,
                characters,
                stopwatch.Elapsed);
        }
        finally
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }

            // 呼叫端沒取消、也沒讀到結尾就停止列舉（例如中途 break）：等同放棄，比照取消記錄。
            if (finished == false)
            {
                stopwatch.Stop();
                capture.CompleteStreamCapture();
                await RecordCanceledAsync(capture, received.ToString(), stopwatch.Elapsed);
            }

            state.Current = null;
            await AiCallCapture.RecordSafelyAsync(services.AiCallLogRecorder, capture, stopwatch.Elapsed, Logger);
        }
    }

    private AiCallCapture CreateCapture()
    {
        var user = services.CurrentUserService.CurrentUser;
        return new AiCallCapture(
            context.Operation,
            settings.GetProvider().ToString(),
            requestedModel,
            user.Account,
            user.Id,
            context.RelatedInfo,
            context.ConversationId,
            state.RunId ?? context.RunId);
    }

    private static bool RequestsTools(IEnumerable<AIContent>? contents)
        => contents?.OfType<FunctionCallContent>().Any() == true;

    /// <summary>
    /// 串流與非串流共用的失敗處理：記帳、寫日誌，回傳要往外丟的 <see cref="AiCallException"/>；
    /// 回傳 null 代表是呼叫端取消，原例外照原樣往外丟。
    /// </summary>
    /// <param name="receivedText">串流已收到的文字（非串流為空字串）。</param>
    private async Task<AiCallException?> HandleFailureAsync(
        Exception ex,
        AiCallCapture capture,
        string receivedText,
        int characters,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        capture.ResponseText = receivedText;

        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動放棄。這是使用者的決定，不是故障 —— 記 Information 而非 Error。
            await RecordCanceledAsync(capture, receivedText, elapsed);
            return null;
        }

        if (ex is OperationCanceledException)
        {
            // 呼叫端沒有取消卻收到取消 = 逾時（我們的 CancelAfter，或傳輸層自己的逾時）。
            capture.ExceptionType = ex.GetType().Name;
            Logger.LogError(
                ex,
                "AI chat completion timed out. Operation={Operation}, TimeoutSeconds={TimeoutSeconds}",
                context.Operation,
                (int)context.Timeout.TotalSeconds);
            await RecordUsageAsync(capture, null, null, null, elapsed, false, AiAnalysisFailureReason.Timeout);
            return new AiCallException(
                AiAnalysisFailureReason.Timeout,
                $"AI 分析逾時（超過 {(int)context.Timeout.TotalSeconds} 秒），{context.TimeoutHint}",
                elapsed,
                detail: ex.ToString(),
                exceptionType: ex.GetType().Name,
                innerException: ex);
        }

        if (ex is ClientResultException clientResult && clientResult.GetRawResponse() is { } raw)
        {
            var payload = capture.ResponseBody ?? raw.Content.ToString();
            capture.ResponseBody ??= payload;
            var failure = MapHttpFailure((HttpStatusCode)raw.Status, payload, characters, elapsed, ex);

            // 沒有用量可記，但呼叫確實發生過、上游也可能已經計費，至少要留下次數。
            await RecordUsageAsync(capture, null, null, null, elapsed, false, failure.Reason);
            return failure;
        }

        if (IsTransportFailure(ex))
        {
            var transport = UnwrapTransportException(ex);
            capture.ExceptionType = transport.GetType().Name;
            Logger.LogError(ex, "AI chat completion request failed at transport level. Operation={Operation}", context.Operation);
            await RecordUsageAsync(capture, null, null, null, elapsed, false, AiAnalysisFailureReason.UpstreamError);
            return new AiCallException(
                AiAnalysisFailureReason.UpstreamError,
                "無法連線到 AI 服務，請確認網路連線與 AiSettings:Endpoint 設定。",
                elapsed,
                detail: transport.ToString(),
                exceptionType: transport.GetType().Name,
                innerException: ex);
        }

        capture.ExceptionType = ex.GetType().Name;
        capture.SetOutcome(false, AiAnalysisFailureReason.Unexpected.ToString());
        Logger.LogError(ex, "AI chat completion failed unexpectedly. Operation={Operation}", context.Operation);
        return new AiCallException(
            AiAnalysisFailureReason.Unexpected,
            $"AI 分析失敗：{ex.GetType().Name}。",
            elapsed,
            detail: ex.ToString(),
            exceptionType: ex.GetType().Name,
            innerException: ex);
    }

    /// <summary>
    /// 呼叫端取消。Token 用量只在「已經收到文字」時記一筆 <c>Canceled</c>、用量空白（0.9.141 起，串流中途停止）：
    /// 已產生的字上游照樣收費，但 usage 要等串流完整結束才拿得到。還沒收到任何文字（含所有非串流呼叫）就照舊不記用量。
    /// AI 對話紀錄兩種都記（請求確實送出過）。
    /// </summary>
    private async Task RecordCanceledAsync(AiCallCapture capture, string receivedText, TimeSpan elapsed)
    {
        capture.ResponseText = receivedText;
        if (receivedText.Length > 0)
        {
            await RecordUsageAsync(capture, null, null, null, elapsed, false, AiAnalysisFailureReason.Canceled);
        }
        else
        {
            capture.SetOutcome(false, AiAnalysisFailureReason.Canceled.ToString());
        }

        Logger.LogInformation(
            "AI chat completion canceled by the caller. Operation={Operation}, ReceivedCharacters={ReceivedCharacters}, ElapsedMs={ElapsedMs}",
            context.Operation,
            receivedText.Length,
            (long)elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 成功收到回應之後：判斷空回應、寫日誌、記帳、記下這次的結果。串流與非串流共用，兩邊的規則才不會漂移。
    /// 空回應在記帳之後以 <see cref="AiCallException"/> 丟出。
    /// </summary>
    private async Task<AiChatParseResult> CompleteSuccessAsync(
        AiCallCapture capture,
        AiChatParseResult parsed,
        string? usageJson,
        bool requestsTools,
        int messageCount,
        int characters,
        TimeSpan elapsed)
    {
        capture.ResponseText = parsed.Content ?? string.Empty;
        capture.FinishReason = parsed.FinishReason;
        capture.ResponseModel = parsed.ModelName;

        // 要求呼叫工具的回應本來就沒有文字內容，不是「付了錢卻沒拿到東西」。
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
        return parsed;
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
            RunId = capture.RunId,
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

        // 推論等級只有 Agent 功能（Responses API）會送（0.9.149 起），所以被拒就是推論等級填了模型不接受的值
        // （例如 Azure gpt-6.1-sol 不接受 none）。Responses 的 param 是 reasoning.effort，Chat Completions 是 reasoning_effort。
        // 0.9.168 起各功能可在系統參數覆寫，訊息指名實際用到的那個設定（見 AiAgentFactory）。
        if (string.Equals(upstream.Param, "reasoning.effort", StringComparison.OrdinalIgnoreCase)
            || string.Equals(upstream.Param, "reasoning_effort", StringComparison.OrdinalIgnoreCase))
        {
            if (reasoning.Origin == AiReasoningEffortOrigin.Conversation)
            {
                return $"此模型不接受推論等級「{reasoning.Effort}」（{reasoning.Source}），請在對話上方改選其他等級或「系統預設」。";
            }

            if (reasoning.Origin == AiReasoningEffortOrigin.Feature)
            {
                return $"此模型不接受推論等級「{reasoning.Effort}」（{reasoning.Source}），請改為空白或此模型接受的值。";
            }

            return string.IsNullOrEmpty(reasoning.Effort)
                ? $"此模型不接受 {reasoning.Source} 的設定值，請改為 null 或此模型接受的值。"
                : $"此模型不接受推論等級「{reasoning.Effort}」（{reasoning.Source} 的設定值），請改為 null 或此模型接受的值。";
        }

        if (string.IsNullOrEmpty(upstream.Param) == false)
        {
            return string.IsNullOrEmpty(upstream.Code)
                ? $"AI 服務不接受參數 {upstream.Param} 的設定值，請調整 AiSettings 後再試。"
                : $"AI 服務不接受參數 {upstream.Param} 的設定值（代碼 {upstream.Code}），請調整 AiSettings 後再試。";
        }

        return string.IsNullOrEmpty(upstream.Code)
            ? $"AI 服務拒絕本次請求，請確認模型設定與{context.SubmittedContentLabel}。"
            : $"AI 服務拒絕本次請求（代碼 {upstream.Code}），請確認模型設定與{context.SubmittedContentLabel}。";
    }
}
