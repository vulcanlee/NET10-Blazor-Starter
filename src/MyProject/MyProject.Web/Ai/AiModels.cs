namespace MyProject.Web.Ai;

/// <summary>
/// Prompt 組裝結果。完整表達「送了幾筆」「哪裡被截斷」，
/// 讓畫面提示、稽核明細與 PDF 報告共用同一份事實。
/// </summary>
public sealed record AiPromptBuildResult
{
    /// <summary>要送給模型的使用者訊息（含開頭的說明行與各筆日誌）。</summary>
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>查詢結果的總筆數。</summary>
    public int TotalEntryCount { get; init; }

    /// <summary>實際送出的筆數。</summary>
    public int IncludedEntryCount { get; init; }

    /// <summary>因單筆過長而被截斷的筆數。</summary>
    public int TruncatedEntryCount { get; init; }

    /// <summary>是否因筆數上限而只取最新的 N 筆。</summary>
    public bool DroppedByEntryLimit { get; init; }

    /// <summary>是否因總字元上限而再丟掉較舊的資料。</summary>
    public bool DroppedByTotalLimit { get; init; }

    /// <summary>日誌本體的字元數（不含開頭說明行），必定不超過設定的總字元上限。</summary>
    public int BodyCharacterCount { get; init; }

    /// <summary><see cref="UserMessage"/> 的總字元數。</summary>
    public int CharacterCount { get; init; }

    /// <summary>是否因上限或總量而捨棄了部分資料。</summary>
    public bool IsTruncated => DroppedByEntryLimit || DroppedByTotalLimit || TruncatedEntryCount > 0;

    public bool IsEmpty => IncludedEntryCount == 0;
}

/// <summary>
/// API 回傳的用量。每個欄位都是 nullable，代表「這次沒回就不顯示」。
///
/// ⚠️ 欄名刻意用 Count 結尾而非 Tokens：MyProject.Tests 的 LoggingConventionTests
/// 禁止日誌佔位符含 "token"，用 Count 結尾才能安全地寫進 ILogger 訊息。
/// </summary>
public sealed record AiTokenUsage
{
    /// <summary><c>usage.prompt_tokens</c></summary>
    public int? InputCount { get; init; }

    /// <summary><c>usage.completion_tokens</c></summary>
    public int? OutputCount { get; init; }

    /// <summary><c>usage.total_tokens</c></summary>
    public int? TotalCount { get; init; }

    /// <summary><c>usage.prompt_tokens_details.cached_tokens</c></summary>
    public int? CachedInputCount { get; init; }

    /// <summary><c>usage.completion_tokens_details.reasoning_tokens</c></summary>
    public int? ReasoningCount { get; init; }

    public bool HasAny
        => InputCount.HasValue || OutputCount.HasValue || TotalCount.HasValue
        || CachedInputCount.HasValue || ReasoningCount.HasValue;
}

public enum AiAnalysisFailureReason
{
    None = 0,
    NotConfigured,
    NoData,
    Unauthorized,
    RateLimited,
    Timeout,
    UpstreamError,
    EmptyResponse,
    Unexpected,
}

/// <summary>一次 AI 分析的完整結果。失敗也用這個型別表達，服務層不丟例外。</summary>
public sealed record AiAnalysisResult
{
    public bool Success { get; init; }

    public AiAnalysisFailureReason Reason { get; init; } = AiAnalysisFailureReason.None;

    /// <summary>模型回傳的原始 Markdown。複製按鈕與 PDF 都以這份為來源。</summary>
    public string Markdown { get; init; } = string.Empty;

    /// <summary>
    /// 給使用者看的中文訊息。
    /// ⚠️ 只能由固定字串（必要時加上上游的 error code）組成，
    /// 絕不可夾帶上游回應 body —— body 可能回吐整份 prompt，也就是日誌內容。
    /// </summary>
    public string ErrorMessage { get; init; } = string.Empty;

    public AiTokenUsage? Usage { get; init; }

    public AiPromptBuildResult Prompt { get; init; } = new();

    /// <summary>實際使用的模型名稱（優先取回應的 model，空的才退回設定值）。</summary>
    public string ModelName { get; init; } = string.Empty;

    public TimeSpan Elapsed { get; init; }

    public static AiAnalysisResult Failure(
        AiAnalysisFailureReason reason,
        string message,
        AiPromptBuildResult? prompt = null,
        TimeSpan elapsed = default,
        AiTokenUsage? usage = null)
        => new()
        {
            Success = false,
            Reason = reason,
            ErrorMessage = message,
            Prompt = prompt ?? new AiPromptBuildResult(),
            Elapsed = elapsed,
            Usage = usage,
        };
}

/// <summary>Chat Completions 回應的解析結果（純資料，不含任何 I/O）。</summary>
public sealed record AiChatParseResult
{
    public string Content { get; init; } = string.Empty;

    public string FinishReason { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public AiTokenUsage? Usage { get; init; }

    /// <summary>Azure 內容過濾攔下時的類別名稱；一般情況為空字串。</summary>
    public string FilterCategory { get; init; } = string.Empty;
}

/// <summary>
/// 請求位址與認證標頭。Azure OpenAI 與 OpenAI 的所有差異都收斂在這個型別裡。
/// </summary>
public sealed record AiChatEndpointDescriptor(Uri RequestUri, string AuthHeaderName, string AuthHeaderValue);
