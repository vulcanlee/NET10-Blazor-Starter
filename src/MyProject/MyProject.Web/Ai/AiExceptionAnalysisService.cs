using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai;

/// <summary>系統例外紀錄的 AI 例外分析（0.9.68 起，含多輪追問）。</summary>
public interface IAiExceptionAnalysisService
{
    /// <summary>設定是否完整可用。明細窗的「AI 分析」按鈕依此決定要不要停用。</summary>
    bool IsAvailable { get; }

    /// <summary>設定不完整時的中文原因（可直接當 Tooltip）；可用時為空字串。</summary>
    string UnavailableReason { get; }

    /// <summary>追問輪數上限（不含第一次分析）。0 代表不開放追問。</summary>
    int MaxFollowUpRounds { get; }

    /// <summary>
    /// 送出整段對話並取得 AI 的下一則回覆。
    /// <paramref name="conversation"/> 不含 system 訊息，第一則是 <see cref="AiExceptionPromptBuilder.Build"/>
    /// 組出的例外明細，最後一則必須是使用者訊息。
    /// ⚠️ 這個方法<b>不丟例外</b>，所有失敗都以 <see cref="AiAnalysisResult"/> 回報。
    /// </summary>
    Task<AiAnalysisResult> AskAsync(
        IReadOnlyList<AiChatMessage> conversation,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiExceptionAnalysisService" />
public sealed class AiExceptionAnalysisService : IAiExceptionAnalysisService
{
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly IAiChatCompletionClient chatCompletionClient;

    public AiExceptionAnalysisService(
        IOptionsMonitor<AiSettings> optionsMonitor,
        IAiChatCompletionClient chatCompletionClient)
    {
        this.optionsMonitor = optionsMonitor;
        this.chatCompletionClient = chatCompletionClient;
    }

    public bool IsAvailable => AiChatEndpoint.Validate(optionsMonitor.CurrentValue) is null;

    public string UnavailableReason => AiChatEndpoint.Validate(optionsMonitor.CurrentValue) ?? string.Empty;

    public int MaxFollowUpRounds => Math.Max(0, optionsMonitor.CurrentValue.MaxFollowUpRounds);

    public async Task<AiAnalysisResult> AskAsync(
        IReadOnlyList<AiChatMessage> conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (conversation.Count == 0)
        {
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NoData, "沒有可分析的例外內容。");
        }

        // ⚠️ 上限在服務層再擋一次，不只靠畫面停用輸入框：每一輪都帶整段前文，
        // 畫面的判斷一旦寫錯，漏掉的就是對話裡最貴的那幾筆呼叫。
        var followUps = conversation.Count(message => message.Role == AiChatRoles.User) - 1;
        if (followUps > MaxFollowUpRounds)
        {
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.FollowUpLimitReached,
                $"已達追問上限（{MaxFollowUpRounds} 輪）。請關閉視窗後重新分析，或調整 AiSettings:MaxFollowUpRounds。");
        }

        return await chatCompletionClient.CompleteAsync(
            new AiChatCompletionRequest
            {
                Operation = TokenUsageOperations.AiExceptionAnalysis,
                Messages = [new AiChatMessage(AiChatRoles.System, AiExceptionPromptDefaults.SystemPrompt), .. conversation],
                ContextLengthExceededMessage = followUps > 0
                    ? "對話內容已超過模型的內容視窗上限。請關閉視窗後重新分析，並減少追問輪數。"
                    : "這筆例外的內容（多半是堆疊）超過模型的內容視窗上限，無法送交 AI 分析。",
                TimeoutHint = "請稍後再試。",
                SubmittedContentLabel = "送出的例外內容",
            },
            cancellationToken);
    }
}
