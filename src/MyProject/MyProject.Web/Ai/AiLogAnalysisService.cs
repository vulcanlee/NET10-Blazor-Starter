using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
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
    private readonly IAiSystemPromptProvider systemPromptProvider;

    public AiLogAnalysisService(
        IOptionsMonitor<AiSettings> optionsMonitor,
        IAiChatCompletionClient chatCompletionClient,
        IAiSystemPromptProvider systemPromptProvider)
    {
        this.optionsMonitor = optionsMonitor;
        this.chatCompletionClient = chatCompletionClient;
        this.systemPromptProvider = systemPromptProvider;
    }

    public bool IsAvailable => AiChatEndpoint.Validate(optionsMonitor.CurrentValue) is null;

    public string UnavailableReason => AiChatEndpoint.Validate(optionsMonitor.CurrentValue) ?? string.Empty;

    public async Task<AiAnalysisResult> AnalyzeAsync(
        IReadOnlyList<LogEntry> entriesAscending,
        CancellationToken cancellationToken = default)
    {
        var (request, failure, prompt) = await PrepareAsync(entriesAscending, cancellationToken);
        if (request is null)
        {
            return failure!;
        }

        var result = await chatCompletionClient.CompleteAsync(request, cancellationToken);
        return result with { Prompt = prompt };
    }

    public async IAsyncEnumerable<AiStreamUpdate> AnalyzeStreamingAsync(
        IReadOnlyList<LogEntry> entriesAscending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (request, failure, prompt) = await PrepareAsync(entriesAscending, cancellationToken);
        if (request is null)
        {
            yield return new AiStreamUpdate { Result = failure };
            yield break;
        }

        await foreach (var update in chatCompletionClient.StreamAsync(request, cancellationToken))
        {
            yield return update.Result is null ? update : update with { Result = update.Result with { Prompt = prompt } };
        }
    }

    /// <summary>
    /// 設定檢查、組提示詞、組請求。非串流與串流共用。
    /// 不能送出時 <c>Request</c> 為 null，<c>Failure</c> 是要回給呼叫端的結果。
    /// </summary>
    private async Task<(AiChatCompletionRequest? Request, AiAnalysisResult? Failure, AiPromptBuildResult Prompt)> PrepareAsync(
        IReadOnlyList<LogEntry> entriesAscending,
        CancellationToken cancellationToken)
    {
        // ⚠️ 在呼叫時才讀設定（CurrentValue），不在建構時快取：Blazor Server 的 DI scope
        // 等於 SignalR circuit，可存活數小時，IOptions 的一次性快照會讓設定變更永遠吃不到。
        var settings = optionsMonitor.CurrentValue;

        // 設定不完整優先於「沒有資料」：兩者都不發請求，但前者才是使用者該先處理的事。
        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            return (null, AiAnalysisResult.Failure(AiAnalysisFailureReason.NotConfigured, invalid), new AiPromptBuildResult());
        }

        var prompt = AiLogPromptBuilder.Build(entriesAscending, settings.MaxEntries);

        if (prompt.IsEmpty)
        {
            return (null, AiAnalysisResult.Failure(AiAnalysisFailureReason.NoData, "目前沒有可分析的日誌。", prompt), prompt);
        }

        // 0.9.108 起提示詞在「AI 提示詞」頁管理（作用中的版本或內建預設＋固定規則）。
        var systemPrompt = await systemPromptProvider.GetAsync(PromptTemplateKeys.LogAnalysis, cancellationToken);

        var request = new AiChatCompletionRequest
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            ModelKey = nameof(AiModelSettings.LogAnalysis),
            Messages =
                [
                    new AiChatMessage(AiChatRoles.System, systemPrompt),
                    new AiChatMessage(AiChatRoles.User, prompt.UserMessage),
                ],
            // 0.9.7 起送出的日誌不再有字元上限，所以「內容太長」變成主要的失敗模式。
            ContextLengthExceededMessage =
                    "送出的日誌量超過模型的內容視窗上限。請在日誌檢視頁縮小時間區間或減少查詢筆數後再試。",
            TimeoutHint = "請縮小查詢範圍後再試。",
            SubmittedContentLabel = "送出的日誌量",
            RelatedInfo = BuildRelatedInfo(entriesAscending, prompt),
        };

        return (request, null, prompt);
    }

    /// <summary>
    /// AI 對話紀錄的關聯說明：實際送出的日誌時間區間與筆數（0.9.72 起）。
    /// 提示詞只保留最新的 N 筆，所以第一筆是倒數第 <c>IncludedEntryCount</c> 筆。
    /// </summary>
    internal static string BuildRelatedInfo(IReadOnlyList<LogEntry> entriesAscending, AiPromptBuildResult prompt)
    {
        var first = entriesAscending[entriesAscending.Count - prompt.IncludedEntryCount];
        var last = entriesAscending[^1];
        return $"日誌 {first.Timestamp:yyyy-MM-dd HH:mm:ss}～{last.Timestamp:yyyy-MM-dd HH:mm:ss}，"
            + $"送出 {prompt.IncludedEntryCount}／{prompt.TotalEntryCount} 筆";
    }
}
