using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Web.SystemReports;

/// <summary>AI 說明的結果。</summary>
public sealed record SystemReportNarration(string Status, string? Markdown, string? ModelName, string? Note);

/// <summary>
/// 系統巡檢報告的 AI 說明（0.10.39 起）：把程式判定好的燈號與數字交給模型，寫「整體狀況、建議處理順序、下期預測」。
///
/// <list type="bullet">
/// <item>只送去識別的發現（規則代碼、燈號、標題、數字、期限、處理頁面），不送帳號、路徑、例外訊息；所以也沒有使用者輸入的文字。</item>
/// <item>模型不能改燈號與數字：指示裡要求，畫面與 PDF 也以程式的結果為準，AI 說明只是附加的文字。</item>
/// <item>指示不放現在時間（模型不知道今天幾號，放了會讓提示詞快取失效）；區間與產生時間放在使用者訊息。</item>
/// <item>失敗、逾時、格式不對都不讓報告失敗：狀態記 Fallback，報告照常產生與寄出（只是少了 AI 說明）。</item>
/// </list>
/// </summary>
public sealed class SystemReportNarrator
{
    /// <summary>送給模型的發現最多幾個字（JSON）；超過時先捨棄黃燈。</summary>
    public const int MaxInputChars = 12_000;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>回應必須包含的段落標題。</summary>
    public const string RequiredHeading = "## 整體狀況";

    public const string DefaultInstructions = """
        你是系統維運顧問，要替系統管理者寫巡檢報告最前面的說明。使用者訊息是一份 JSON：報告區間、整體燈號，
        以及程式依固定規則判定好的每一項發現（燈號、標題、現況、期限、處理頁面）、需要擴充的磁碟與下期預測。

        燈號的意思：紅燈＝已發生問題，要馬上處理；橙燈＝即將發生，有期限；黃燈＝注意即可；綠燈＝正常。

        請輸出三段，標題一字不改：
        ## 整體狀況
        3～5 句話：系統現在是否健康、最需要管理者注意的是什麼。
        ## 建議處理順序
        依急迫程度排序的編號清單，最多 7 項；每項說明要做什麼、到哪個頁面（用 JSON 裡的頁面路徑），有期限的寫出期限日期。
        ## 下期預測
        2～4 句話：照目前趨勢，下一期可能發生什麼、哪些要提早準備（例如擴充磁碟）。

        規則：
        - 燈號、數字、日期以 JSON 為準，不可以改、不可以自己推算新的數字。
        - 不可以提到 JSON 裡沒有的問題。
        - 全部綠燈時就說系統正常，不要硬找問題。
        - 寫給管理者看，用平實的中文，不用技術縮寫；提到燈號一律寫「紅燈」「橙燈」「黃燈」「綠燈」。
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAiChatCompletionClient chatClient;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly IOptionsMonitor<AiSettings> aiOptions;
    private readonly IOptionsMonitor<SystemReportSettings> settings;
    private readonly ILogger<SystemReportNarrator> logger;

    public SystemReportNarrator(
        IAiChatCompletionClient chatClient,
        IAiSystemPromptProvider promptProvider,
        IOptionsMonitor<AiSettings> aiOptions,
        IOptionsMonitor<SystemReportSettings> settings,
        ILogger<SystemReportNarrator> logger)
    {
        this.chatClient = chatClient;
        this.promptProvider = promptProvider;
        this.aiOptions = aiOptions;
        this.settings = settings;
        this.logger = logger;
    }

    public async Task<SystemReportNarration> NarrateAsync(SystemReportContentDto content, CancellationToken cancellationToken)
    {
        if (!settings.CurrentValue.AiNarrativeEnabled)
        {
            return new SystemReportNarration(SystemReportNarrativeStatuses.Skipped, null, null, "系統參數「AI 寫報告說明」已關閉。");
        }

        if (AiChatEndpoint.Validate(aiOptions.CurrentValue) is not null)
        {
            return new SystemReportNarration(SystemReportNarrativeStatuses.Skipped, null, null, "沒有設定 AI。");
        }

        if (content.Frequency == SystemReportFrequencies.Daily && content.Level == SystemReportLevels.Green)
        {
            return new SystemReportNarration(SystemReportNarrativeStatuses.Skipped, null, null, "日報全部正常，不需要 AI 說明。");
        }

        var systemPrompt = await promptProvider.GetAsync(PromptTemplateKeys.SystemReport, cancellationToken);
        var result = await chatClient.CompleteAsync(new AiChatCompletionRequest
        {
            Operation = TokenUsageOperations.SystemReportNarrative,
            Messages =
            [
                new AiChatMessage(AiChatRoles.System, systemPrompt),
                new AiChatMessage(AiChatRoles.User, BuildUserMessage(content)),
            ],
            ContextLengthExceededMessage = "報告內容超過模型的內容視窗上限。",
            TimeoutHint = "下一份報告會再試。",
            SubmittedContentLabel = "報告內容",
            RelatedInfo = $"系統巡檢報告 {SystemReportFrequencies.DisplayName(content.Frequency)} {content.PeriodStart:yyyy/MM/dd}～{content.PeriodEnd.AddDays(-1):yyyy/MM/dd}",
            ConversationId = content.ReportId,
            Timeout = Timeout,
        }, cancellationToken);

        if (!result.Success)
        {
            logger.LogWarning("System report narration failed. ReportId={ReportId}, Reason={Reason}", content.ReportId, result.Reason);
            return new SystemReportNarration(SystemReportNarrativeStatuses.Fallback, null, result.ModelName, "AI 說明產生失敗，以程式判定的內容為準。");
        }

        if (string.IsNullOrWhiteSpace(result.Markdown) || !result.Markdown.Contains(RequiredHeading, StringComparison.Ordinal))
        {
            logger.LogWarning("System report narration had an unexpected format. ReportId={ReportId}", content.ReportId);
            return new SystemReportNarration(SystemReportNarrativeStatuses.Fallback, null, result.ModelName, "AI 說明的格式不符，以程式判定的內容為準。");
        }

        return new SystemReportNarration(SystemReportNarrativeStatuses.Written, result.Markdown.Trim(), result.ModelName, null);
    }

    /// <summary>送給模型的 JSON（去識別、限長度：超過時先捨棄黃燈）。</summary>
    public static string BuildUserMessage(SystemReportContentDto content)
    {
        var findings = content.Findings.ToList();
        string Serialize() => JsonSerializer.Serialize(new
        {
            period = $"{content.PeriodStart:yyyy/MM/dd}～{content.PeriodEnd.AddDays(-1):yyyy/MM/dd}",
            frequency = SystemReportFrequencies.DisplayName(content.Frequency),
            generatedAt = $"{content.CreatedAt:yyyy/MM/dd HH:mm}",
            level = SystemReportLevels.ShortName(content.Level),
            verdict = content.Verdict,
            findings = findings.Select(x => new
            {
                level = SystemReportLevels.ShortName(x.Level),
                section = x.Section,
                title = x.Title,
                detail = x.Detail,
                deadline = x.Deadline?.ToString("yyyy/MM/dd"),
                page = x.ActionRoute,
            }),
            expansions = content.Expansions.Select(x => new { volume = x.Volume, fullDate = x.FullDate?.ToString("yyyy/MM/dd"), suggestedGb = Math.Round(x.SuggestedBytes / 1024d / 1024 / 1024, 1) }),
            predictions = content.Predictions,
        }, JsonOptions);

        var json = Serialize();
        while (json.Length > MaxInputChars && findings.Count > 0)
        {
            findings.RemoveAt(findings.Count - 1);
            json = Serialize();
        }

        return json;
    }
}
