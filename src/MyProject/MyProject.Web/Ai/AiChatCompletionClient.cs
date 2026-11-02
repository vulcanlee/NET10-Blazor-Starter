using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai;

/// <summary>
/// 送出一次對話並取回結果（AI 日誌分析與 AI 例外分析共用，0.9.68 起）。
///
/// 0.9.140 起底層改走 Microsoft Agent Framework：由 <see cref="IAiAgentFactory"/> 建 agent 後執行，
/// 記帳、AI 對話紀錄、用量上限與錯誤對應都在 agent 管線上的 <see cref="AiAccountingChatClient"/>。
/// 這一層只負責「設定檢查」與「把 agent 的結果轉回 <see cref="AiAnalysisResult"/>」，介面與行為不變。
/// </summary>
public interface IAiChatCompletionClient
{
    /// <summary>
    /// 送出並解析回應。
    /// ⚠️ 這個方法<b>不丟例外</b>，所有失敗都以 <see cref="AiAnalysisResult"/> 回報
    /// （<see cref="AiAnalysisResult.Prompt"/> 由呼叫端自行補上）。
    /// </summary>
    Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default);
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

        // ⚠️ 在呼叫時才讀設定（CurrentValue），理由見 AiAgentFactory.CreateAgent。
        var settings = optionsMonitor.CurrentValue;

        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            logger.LogWarning("AI chat completion skipped because settings are incomplete. Operation={Operation}", request.Operation);
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NotConfigured, invalid);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var handle = agentFactory.CreateAgent(new AiCallContext
            {
                Operation = request.Operation,
                Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120),
                RelatedInfo = request.RelatedInfo,
                ConversationId = request.ConversationId,
                ContextLengthExceededMessage = request.ContextLengthExceededMessage,
                TimeoutHint = request.TimeoutHint,
                SubmittedContentLabel = request.SubmittedContentLabel,
            });

            // system 訊息照原樣放在訊息序列裡（不用 agent 的 instructions），送出的順序與 0.9.139 以前一致。
            var messages = request.Messages
                .Select(message => new ChatMessage(new ChatRole(message.Role), message.Content))
                .ToList();

            await handle.Agent.RunAsync(messages, cancellationToken: cancellationToken);

            // 記帳中介層在每次成功時記下解析結果；單次對話只有一次模型呼叫。
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
        catch (AiCallException ex)
        {
            // 已在記帳中介層記過帳、寫過日誌，這裡只轉型。
            return ex.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Canceled,
                "已取消本次 AI 分析。",
                elapsed: stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            // 送出之前就失敗（建立 agent、組訊息）。沒有發出請求，所以沒有帳可記。
            logger.LogError(ex, "AI chat completion failed unexpectedly. Operation={Operation}", request.Operation);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Unexpected,
                $"AI 分析失敗：{ex.GetType().Name}。",
                elapsed: stopwatch.Elapsed,
                detail: ex.ToString());
        }
    }
}
