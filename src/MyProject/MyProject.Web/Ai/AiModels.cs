namespace MyProject.Web.Ai;

/// <summary>
/// Prompt 組裝結果。表達「送了幾筆、多少字」，
/// 讓畫面提示、稽核明細與 PDF 報告共用同一份事實。
///
/// 0.9.7 起不再有任何字元層級的截斷，所以「哪些內容被切掉」這組概念整個消失了 ——
/// 唯一會捨棄資料的原因是 <see cref="DroppedByEntryLimit"/>（筆數上限）。
/// </summary>
public sealed record AiPromptBuildResult
{
    /// <summary>要送給模型的使用者訊息（含開頭的說明行與各筆日誌）。</summary>
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>查詢結果的總筆數。</summary>
    public int TotalEntryCount { get; init; }

    /// <summary>實際送出的筆數。</summary>
    public int IncludedEntryCount { get; init; }

    /// <summary>是否因筆數上限而只取最新的 N 筆。這是唯一會少送資料的原因。</summary>
    public bool DroppedByEntryLimit { get; init; }

    /// <summary><see cref="UserMessage"/> 的總字元數。</summary>
    public int CharacterCount { get; init; }

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

    /// <summary>
    /// 呼叫端主動取消（0.9.8 起：使用者在等待中關閉對話窗）。
    ///
    /// ⚠️ 與 <see cref="Timeout"/> 刻意分開：兩者在 .NET 上都表現為
    /// <c>TaskCanceledException</c>，但一個是使用者的決定、一個是系統的失敗。
    /// 混在一起會讓「使用者放棄」被記成 ERROR，也會讓真正的逾時被稀釋掉。
    /// </summary>
    Canceled,
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

    /// <summary>
    /// 回應是否因為達到長度上限而被切斷（<c>finish_reason</c> 為 <c>length</c>）。
    /// 內容非空時仍會照常顯示，但結尾可能不完整，畫面要另外提醒 ——
    /// 一份看起來完整、實際被切掉結論的分析報告比明講更危險。
    /// </summary>
    public bool IsTruncatedByLength { get; init; }

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

/// <summary>
/// 上游回傳的錯誤細節（<c>{"error":{...}}</c>）。
///
/// <para>
/// <see cref="Param"/> 是出問題的參數名稱（例如 <c>temperature</c>），結構上不可能夾帶
/// 日誌內容，是排查時最有用也最安全的一欄。
/// </para>
/// <para>
/// ⚠️ <see cref="Message"/> 已截斷至 <see cref="MaxMessageLength"/> 字元。上游對某些錯誤
/// （例如內容過濾）的說明可能夾帶提示詞片段，而提示詞裡是上百筆日誌 —— 不截斷會把日誌檔
/// 撐爆，也可能讓同一段內容在日誌裡反覆堆疊。
/// </para>
/// </summary>
public sealed record AiUpstreamError
{
    /// <summary>截斷上限。夠一句完整的錯誤說明，又短到不可能塞進有意義的日誌內容。</summary>
    public const int MaxMessageLength = 300;

    /// <summary><c>error.code</c>，例如 <c>unsupported_value</c>。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary><c>error.param</c>，出問題的參數名稱。</summary>
    public string Param { get; init; } = string.Empty;

    /// <summary><c>error.type</c>，例如 <c>invalid_request_error</c>。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary><c>error.message</c>，已截斷。</summary>
    public string Message { get; init; } = string.Empty;

    public bool HasAny
        => string.IsNullOrEmpty(Code) == false
        || string.IsNullOrEmpty(Param) == false
        || string.IsNullOrEmpty(Type) == false
        || string.IsNullOrEmpty(Message) == false;
}
