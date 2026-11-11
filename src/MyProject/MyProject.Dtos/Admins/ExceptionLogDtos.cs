using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;

namespace MyProject.Dtos.Admins;

/// <summary>
/// 系統例外紀錄頁的查詢條件（0.10.5 起，WASM 遷移 M6；<c>POST api/v1/exception-logs/search</c>、<c>export</c>、<c>copy-text</c>）。
/// 時間比對「最後發生」，是伺服器的本地時間（不帶時區）；<see cref="SearchRequestBaseDto.Keyword"/> 比對類型、訊息、頁面、操作與追蹤碼。
/// 排序支援 <c>LastOccurredAt</c>、<c>OccurrenceCount</c>，未指定時依最後發生由新到舊。
/// </summary>
public class ExceptionLogSearchRequestDto : SearchRequestBaseDto
{
    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    /// <summary>來源（完全相同）；空白不限。</summary>
    public string? Source { get; set; }

    /// <summary>使用者帳號（部分符合）。</summary>
    public string? Account { get; set; }

    /// <summary>true：使用者按下「查詢」，伺服器寫一筆搜尋稽核；換頁、排序、重新整理不帶。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>系統例外紀錄的一列（0.10.5 起）。時間是伺服器的本地時間（不帶時區）。不含簽章與堆疊檔名（伺服器內部鍵值）。</summary>
public class ExceptionLogItemDto
{
    public int Id { get; set; }

    public string ExceptionType { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? Page { get; set; }

    public string? Operation { get; set; }

    public string? LoggerName { get; set; }

    public string? Account { get; set; }

    public int? UserId { get; set; }

    public long OccurrenceCount { get; set; }

    public DateTime FirstOccurredAt { get; set; }

    public DateTime LastOccurredAt { get; set; }

    public string? LastTraceId { get; set; }
}

/// <summary>明細窗（<c>GET api/v1/exception-logs/{id}</c>）：列的內容加上完整堆疊。</summary>
public class ExceptionLogDetailDto : ExceptionLogItemDto
{
    /// <summary>原始堆疊；堆疊檔不存在時為 null（畫面另外顯示說明文字）。</summary>
    public string? StackTrace { get; set; }
}

/// <summary>「點列複製」用的堆疊（<c>GET api/v1/exception-logs/{id}/stack-trace</c>，不寫稽核，與 Server 版相同）。</summary>
public class ExceptionLogStackTraceDto
{
    /// <summary>原始堆疊；堆疊檔不存在時為 null。</summary>
    public string? StackTrace { get; set; }
}

/// <summary>「複製目前查詢結果（含完整堆疊）」的文字（<c>POST api/v1/exception-logs/copy-text</c>）。</summary>
public class ExceptionLogCopyTextDto
{
    /// <summary>實際複製的筆數。</summary>
    public int Count { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>系統例外紀錄頁的選項（<c>GET api/v1/exception-logs/options</c>）。</summary>
public class ExceptionLogOptionsDto
{
    /// <summary>「清除 N 天未再發生的紀錄」的天數（伺服器的 <c>LogRetentionSettings:ManualExceptionLogDays</c>）。</summary>
    public int PurgeDays { get; set; }

    /// <summary>匯出與「複製目前查詢結果」最多幾筆。</summary>
    public int MaxRows { get; set; }

    /// <summary>AI 例外分析的設定是否完整可用。</summary>
    public bool AiAvailable { get; set; }

    /// <summary>不可用時的中文原因（直接當「AI 分析」按鈕的 Tooltip）；可用時為空字串。</summary>
    public string AiUnavailableReason { get; set; } = string.Empty;

    /// <summary>追問輪數上限（不含第一次分析），0 代表不開放追問。</summary>
    public int MaxFollowUpRounds { get; set; }
}

/// <summary>
/// AI 例外分析（<c>POST api/v1/exception-logs/{id}/ai</c>，回 NDJSON 串流）。
/// 第一則「例外明細」與 system 提示由伺服器自己組（畫面改不到），這裡只帶之後的對話。
/// </summary>
public class ExceptionAiRequestDto
{
    /// <summary>單則追問的字數上限（畫面的輸入框與伺服器的檢查共用）。追問是給人打的問題，不是貼整份檔案的地方。</summary>
    public const int MaxQuestionLength = 2000;

    /// <summary>
    /// 單則 AI 回覆的字數上限：畫面本來就不顯示轉成 HTML 後超過 512KB 的回覆（同 Server 版），能送回來的回覆不會比這更大；
    /// 伺服器擋下異常龐大的內容，避免被拿來塞爆上游的內容視窗。
    /// </summary>
    public const int MaxReplyLength = 512 * 1024;

    /// <summary>同一段對話的識別碼（開窗時產生，第一次分析與每輪追問共用；AI 對話紀錄靠它串成同一段）。</summary>
    public Guid ConversationId { get; set; }

    /// <summary>例外明細之後的對話：assistant、user 交替；第一次分析為空，追問時以 user（這次的問題）結尾。</summary>
    public List<AiChatMessageDto> Messages { get; set; } = [];
}

/// <summary>AI 例外分析的 PDF 報告（<c>POST api/v1/exception-logs/{id}/ai/pdf</c>）：例外明細與堆疊由伺服器補上。</summary>
public class ExceptionAiPdfRequestDto
{
    /// <summary>例外明細之後的對話：assistant、user 交替，以 assistant 結尾；被停止的回覆標 <see cref="AiChatMessageDto.Interrupted"/>。</summary>
    public List<AiChatMessageDto> Messages { get; set; } = [];

    public string ModelName { get; set; } = string.Empty;

    /// <summary>整段對話的累計用量。</summary>
    public AiUsageDto? Usage { get; set; }
}
