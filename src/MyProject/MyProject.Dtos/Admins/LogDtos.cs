using MyProject.Dtos.Ai;

namespace MyProject.Dtos.Admins;

/// <summary>
/// 日誌檢視的查詢條件（0.10.5 起，WASM 遷移 M6；<c>POST api/v1/logs/search</c>、<c>export</c>、<c>ai</c>，以及 <c>ai/pdf</c> 的 <see cref="LogAiReportPdfRequestDto.Query"/>）。
/// 時間是伺服器的本地時間（不帶時區）。區間超過 3 天、起訖顛倒、筆數超出範圍時伺服器自動調整並回傳警告（與 Server 版相同），不拒絕查詢。
/// </summary>
public class LogSearchRequestDto
{
    /// <summary>查詢筆數的預設值（與伺服器 <c>LogQueryRequest.DefaultTake</c> 同一個值）。</summary>
    public const int DefaultTake = 100;

    /// <summary>查詢筆數的上限（與伺服器 <c>LogQueryRequest.MaxTake</c> 同一個值）。</summary>
    public const int MaxTake = 10000;

    /// <summary>起始時間；沒帶時是伺服器的「現在」減 1 小時。帶了 <see cref="QuickRange"/> 時不看。</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>結束時間；沒帶時是伺服器的「現在」。帶了 <see cref="QuickRange"/> 時不看。</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>快速區間（<see cref="LogQuickRanges"/>），依伺服器的「現在」計算起訖；null 表示用 <see cref="StartTime"/>／<see cref="EndTime"/>。</summary>
    public string? QuickRange { get; set; }

    /// <summary>最低等級：<c>TRACE</c>、<c>DEBUG</c>、<c>INFO</c>、<c>WARN</c>、<c>ERROR</c>、<c>FATAL</c>；空字串或 null 表示不限。</summary>
    public string? MinimumLevel { get; set; }

    /// <summary>取最新 N 筆（套用其他篩選之後）。</summary>
    public int Take { get; set; } = DefaultTake;

    /// <summary>關鍵字，比對整筆原始內容（不分大小寫）。</summary>
    public string? Keyword { get; set; }

    /// <summary>錯誤追蹤碼，只留 TraceId 完全相同的紀錄（不分大小寫）。</summary>
    public string? TraceCode { get; set; }

    /// <summary>true：使用者按下「查詢」（含快速區間、查前後 1 分鐘），伺服器寫一筆搜尋稽核；進頁的自動查詢不帶。只有 <c>search</c> 看這個欄位。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>日誌檢視的快速區間代碼（<see cref="LogSearchRequestDto.QuickRange"/>）。</summary>
public static class LogQuickRanges
{
    public const string Last5Minutes = "last5m";
    public const string Last15Minutes = "last15m";
    public const string LastHour = "last1h";
    public const string Today = "today";
}

/// <summary>一筆日誌（一筆可能橫跨多個實體行，例如例外堆疊）。時間是伺服器的本地時間（不帶時區）。</summary>
public class LogEntryDto
{
    /// <summary>這次查詢結果裡的順序（1 起算、時間正序），畫面當表格的列識別。</summary>
    public int Sequence { get; set; }

    public DateTime Timestamp { get; set; }

    public string TraceId { get; set; } = string.Empty;

    /// <summary>原始等級字串，例如 <c>INFO</c>；無法解析時為空字串。</summary>
    public string Level { get; set; } = string.Empty;

    public string ThreadId { get; set; } = string.Empty;

    public string Logger { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>未經任何處理的原始 nlog 文字（含所有續行），展開、複製與匯出都用這份。</summary>
    public string Raw { get; set; } = string.Empty;

    public bool HasException { get; set; }
}

/// <summary>日誌查詢的結果（<c>POST api/v1/logs/search</c>）。</summary>
public class LogSearchResultDto
{
    /// <summary>查詢結果，時間正序（舊→新）。</summary>
    public List<LogEntryDto> Entries { get; set; } = [];

    public int TotalCount { get; set; }

    /// <summary>給使用者看的狀態訊息（查無資料、目錄不存在等）；沒有時為空字串。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>非致命問題：條件被調整、個別檔案讀取失敗等。</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>伺服器實際使用的起始時間（快速區間、預設區間或被調整後的值），畫面寫回日期欄位。</summary>
    public DateTime AppliedStartTime { get; set; }

    public DateTime AppliedEndTime { get; set; }
}

/// <summary>日誌檢視的選項（<c>GET api/v1/logs/options</c>）。</summary>
public class LogViewerOptionsDto
{
    /// <summary>AI 分析的設定是否完整。</summary>
    public bool AiAvailable { get; set; }

    /// <summary>設定不完整的原因（當 AI 按鈕的提示文字）；可用時為空字串。</summary>
    public string AiUnavailableReason { get; set; } = string.Empty;
}

/// <summary>
/// AI 日誌分析報告匯出 PDF（<c>POST api/v1/logs/ai/pdf</c>）。報告內文、模型、用量、耗時由畫面送回（伺服器不保存分析結果）；
/// 附錄的提示詞由伺服器依 <see cref="Query"/> 重新查詢後重建，產生時間用伺服器時間。
/// </summary>
public class LogAiReportPdfRequestDto
{
    /// <summary>分析時使用的查詢條件（畫面上那份結果的條件）。</summary>
    public LogSearchRequestDto Query { get; set; } = new();

    /// <summary>
    /// 畫面上的報告 Markdown（停止產生時已加上「已中止」註記）。上限同例外分析的單則回覆（0.10.5 獨立審查：
    /// 這份與下面的模型名稱來自畫面，會拿去排版 PDF 與寫稽核摘要，不能不設上限）。
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(ExceptionAiRequestDto.MaxReplyLength, ErrorMessage = "報告內容過長，無法匯出 PDF。")]
    public string Markdown { get; set; } = string.Empty;

    public bool Success { get; set; }

    /// <summary>失敗原因名稱（成功為 <c>None</c>、停止產生為 <c>Canceled</c>）。</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(50, ErrorMessage = "失敗原因過長。")]
    public string Reason { get; set; } = "None";

    [System.ComponentModel.DataAnnotations.MaxLength(200, ErrorMessage = "模型名稱過長。")]
    public string ModelName { get; set; } = string.Empty;

    public AiUsageDto? Usage { get; set; }

    public long ElapsedMilliseconds { get; set; }
}

/// <summary>日誌等級設定（<c>GET api/v1/log-level</c>，套用與還原也回這個）。等級是 NLog 的大寫字串，例如 <c>INFO</c>。</summary>
public class LogLevelStateDto
{
    /// <summary>是否成功掛上 NLog 設定；false 時無法在執行期調整。</summary>
    public bool IsAvailable { get; set; }

    public string EffectiveLevel { get; set; } = string.Empty;

    /// <summary>nlog.config 的 <c>&lt;logger name="*"&gt;</c> minlevel。</summary>
    public string SystemDefaultLevel { get; set; } = string.Empty;

    /// <summary>目前有沒有執行期覆寫（有才能還原）。</summary>
    public bool HasOverride { get; set; }
}

/// <summary>套用日誌等級（<c>POST api/v1/log-level/apply</c>）。</summary>
public class LogLevelApplyRequestDto
{
    /// <summary><c>TRACE</c>、<c>DEBUG</c>、<c>INFO</c>、<c>WARN</c>、<c>ERROR</c>、<c>FATAL</c>。</summary>
    public string Level { get; set; } = string.Empty;
}
