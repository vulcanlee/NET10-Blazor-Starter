using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai;

/// <summary>
/// 送出一次對話並取回結果（AI 日誌分析與 AI 例外分析共用，0.9.68 起）。
///
/// 0.9.140 起底層改走 Microsoft Agent Framework：由 <see cref="IAiAgentFactory"/> 建 agent 後執行，
/// 記帳、AI 對話紀錄、用量上限與錯誤對應都在 agent 管線上的 <see cref="AiAccountingChatClient"/>。
/// 這一層只負責「設定檢查」與「把 agent 的結果轉回 <see cref="AiAnalysisResult"/>」。
/// </summary>
public interface IAiChatCompletionClient
{
    /// <summary>
    /// 送出並解析回應。
    /// ⚠️ 這個方法<b>不丟例外</b>，所有失敗都以 <see cref="AiAnalysisResult"/> 回報
    /// （<see cref="AiAnalysisResult.Prompt"/> 由呼叫端自行補上）。
    /// </summary>
    Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 串流版（0.9.141 起）：逐段回傳文字，最後一定有一筆帶 <see cref="AiStreamUpdate.Result"/>。
    /// ⚠️ 同樣<b>不丟例外</b>，取消也不丟：取消時最後一筆是 <c>Canceled</c>，已收到文字的話
    /// <see cref="AiAnalysisResult.IsInterrupted"/> 為 true、<see cref="AiAnalysisResult.Markdown"/> 是已收到的部分。
    /// </summary>
    IAsyncEnumerable<AiStreamUpdate> StreamAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiChatCompletionClient" />
public sealed class AiChatCompletionClient : IAiChatCompletionClient
{
    private readonly ILogger<AiChatCompletionClient> logger;
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly IAiAgentFactory agentFactory;

    public AiChatCompletionClient(
        ILogger<AiChatCompletionClient> logger,
        IOptionsMonitor<AiSettings> optionsMonitor,
        IAiAgentFactory agentFactory)
    {
        this.logger = logger;
        this.optionsMonitor = optionsMonitor;
        this.agentFactory = agentFactory;
    }

    public async Task<AiAnalysisResult> CompleteAsync(
        AiChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } notConfigured)
        {
            return notConfigured;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var handle = CreateAgent(request);
            await handle.Agent.RunAsync(ToChatMessages(request), cancellationToken: cancellationToken);
            return ToSuccess(handle);
        }
        catch (AiCallException ex)
        {
            // 已在記帳中介層記過帳、寫過日誌，這裡只轉型。
            return ex.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled(stopwatch.Elapsed, string.Empty);
        }
        catch (Exception ex)
        {
            return Unexpected(ex, request, stopwatch.Elapsed);
        }
    }

    public async IAsyncEnumerable<AiStreamUpdate> StreamAsync(
        AiChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } notConfigured)
        {
            yield return new AiStreamUpdate { Result = notConfigured };
            yield break;
        }

        var stopwatch = Stopwatch.StartNew();
        var received = new StringBuilder();
        AiAnalysisResult? result = null;
        AiAgentHandle? handle = null;
        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        try
        {
            while (result is null)
            {
                // ⚠️ C# 不允許在有 catch 的 try 裡 yield，所以只把「取下一筆」包起來，yield 放在外面。
                string? delta;
                try
                {
                    if (enumerator is null)
                    {
                        handle = CreateAgent(request);
                        enumerator = handle.Agent
                            .RunStreamingAsync(ToChatMessages(request), cancellationToken: cancellationToken)
                            .GetAsyncEnumerator(cancellationToken);
                    }

                    if (await enumerator.MoveNextAsync() == false)
                    {
                        result = ToSuccess(handle!);
                        break;
                    }

                    delta = enumerator.Current.Text;
                }
                catch (AiCallException ex)
                {
                    result = ex.ToResult();
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = Canceled(stopwatch.Elapsed, received.ToString());
                    break;
                }
                catch (Exception ex)
                {
                    result = Unexpected(ex, request, stopwatch.Elapsed);
                    break;
                }

                if (string.IsNullOrEmpty(delta) == false)
                {
                    received.Append(delta);
                    yield return new AiStreamUpdate { TextDelta = delta };
                }
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }
        }

        yield return new AiStreamUpdate { Result = result };
    }

    /// <summary>設定不完整就不建 agent、不發請求。</summary>
    private AiAnalysisResult? Validate(AiChatCompletionRequest request)
    {
        // ⚠️ 在呼叫時才讀設定（CurrentValue），理由見 AiAgentFactory.CreateAgent。
        var invalid = AiChatEndpoint.Validate(optionsMonitor.CurrentValue);
        if (invalid is null)
        {
            return null;
        }

        logger.LogWarning("AI chat completion skipped because settings are incomplete. Operation={Operation}", request.Operation);
        return AiAnalysisResult.Failure(AiAnalysisFailureReason.NotConfigured, invalid);
    }

    private AiAgentHandle CreateAgent(AiChatCompletionRequest request)
    {
        var settings = optionsMonitor.CurrentValue;
        return agentFactory.CreateAgent(new AiCallContext
        {
            Operation = request.Operation,
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120),
            RelatedInfo = request.RelatedInfo,
            ConversationId = request.ConversationId,
            ContextLengthExceededMessage = request.ContextLengthExceededMessage,
            TimeoutHint = request.TimeoutHint,
            SubmittedContentLabel = request.SubmittedContentLabel,
        });
    }

    /// <summary>system 訊息照原樣放在訊息序列裡（不用 agent 的 instructions），送出的順序與 0.9.139 以前一致。</summary>
    private static List<ChatMessage> ToChatMessages(AiChatCompletionRequest request)
        => request.Messages
            .Select(message => new ChatMessage(new ChatRole(message.Role), message.Content))
            .ToList();

    /// <summary>記帳中介層在每次成功時記下解析結果；單次對話只有一次模型呼叫。</summary>
    private static AiAnalysisResult ToSuccess(AiAgentHandle handle)
    {
        var outcome = handle.State.LastOutcome
            ?? throw new InvalidOperationException("AI agent finished without a recorded model call.");

        return new AiAnalysisResult
        {
            Success = true,
            Markdown = outcome.Content,
            Usage = outcome.Usage,
            ModelName = outcome.ModelName,
            IsTruncatedByLength = string.Equals(outcome.FinishReason, "length", StringComparison.OrdinalIgnoreCase),
            Elapsed = outcome.Elapsed,
        };
    }

    /// <summary>使用者取消。串流已收到文字時保留下來並標示已中止（0.9.141 起）。</summary>
    private static AiAnalysisResult Canceled(TimeSpan elapsed, string receivedText)
        => AiAnalysisResult.Failure(AiAnalysisFailureReason.Canceled, "已取消本次 AI 分析。", elapsed: elapsed) with
        {
            Markdown = receivedText,
            IsInterrupted = receivedText.Length > 0,
        };

    /// <summary>送出之前就失敗（建立 agent、組訊息）。沒有發出請求，所以沒有帳可記。</summary>
    private AiAnalysisResult Unexpected(Exception ex, AiChatCompletionRequest request, TimeSpan elapsed)
    {
        logger.LogError(ex, "AI chat completion failed unexpectedly. Operation={Operation}", request.Operation);
        return AiAnalysisResult.Failure(
            AiAnalysisFailureReason.Unexpected,
            $"AI 分析失敗：{ex.GetType().Name}。",
            elapsed: elapsed,
            detail: ex.ToString());
    }
}
