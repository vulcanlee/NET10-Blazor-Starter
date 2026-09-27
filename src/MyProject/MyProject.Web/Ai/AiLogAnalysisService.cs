using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Ai;

/// <inheritdoc cref="IAiLogAnalysisService" />
/// <remarks>
/// 0.9.68 起 HTTP、錯誤對應與 Token 用量記錄都移到 <see cref="AiChatCompletionClient"/>
/// （與 AI 例外分析共用），本類別只負責組日誌提示詞與日誌分析專屬的提示文字。
/// </remarks>
public sealed class AiLogAnalysisService : IAiLogAnalysisService
{
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly IAiChatCompletionClient chatCompletionClient;

    public AiLogAnalysisService(
        IOptionsMonitor<AiSettings> optionsMonitor,
        IAiChatCompletionClient chatCompletionClient)
    {
        this.optionsMonitor = optionsMonitor;
        this.chatCompletionClient = chatCompletionClient;
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

        // 設定不完整優先於「沒有資料」：兩者都不發請求，但前者才是使用者該先處理的事。
        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NotConfigured, invalid);
        }

        var prompt = AiLogPromptBuilder.Build(entriesAscending, settings.MaxEntries);

        if (prompt.IsEmpty)
        {
            return AiAnalysisResult.Failure(AiAnalysisFailureReason.NoData, "目前沒有可分析的日誌。", prompt);
        }

        var result = await chatCompletionClient.CompleteAsync(
            new AiChatCompletionRequest
            {
                Operation = TokenUsageOperations.AiLogAnalysis,
                Messages =
                [
                    new AiChatMessage(AiChatRoles.System, AiLogPromptBuilder.ResolveSystemPrompt(settings.SystemPrompt)),
                    new AiChatMessage(AiChatRoles.User, prompt.UserMessage),
                ],
                // 0.9.7 起送出的日誌不再有字元上限，所以「內容太長」變成主要的失敗模式。
                ContextLengthExceededMessage =
                    "送出的日誌量超過模型的內容視窗上限。請在日誌檢視頁縮小時間區間或減少查詢筆數後再試。",
                TimeoutHint = "請縮小查詢範圍後再試。",
                SubmittedContentLabel = "送出的日誌量",
            },
            cancellationToken);

        return result with { Prompt = prompt };
    }
}
