namespace MyProject.Web.Diagnostics;

/// <summary>
/// 日誌等級序位。數值越大越嚴重，用於「最低等級」篩選比較。
/// </summary>
public enum LogLevelRank
{
    /// <summary>不限，不套用等級篩選。</summary>
    Any = -1,
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5,
    /// <summary>無法解析的等級。一律保留，不因等級篩選而隱藏資料。</summary>
    Unknown = 99,
}

public sealed class LogQueryRequest
{
    /// <summary>起始時間（含），本地時間。</summary>
    public DateTime StartTime { get; set; }

    /// <summary>結束時間（含），本地時間。</summary>
    public DateTime EndTime { get; set; }

    /// <summary>取最新 N 筆（套用其他篩選之後）。</summary>
    public int Take { get; set; } = DefaultTake;

    public LogLevelRank MinimumLevel { get; set; } = LogLevelRank.Any;

    public string Keyword { get; set; } = string.Empty;

    public const int DefaultTake = 100;
    public const int MaxTake = 10000;

    /// <summary>查詢區間上限。超過時夾住起始時間，而非拒絕查詢。</summary>
    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(3);
}

/// <summary>
/// 一筆日誌紀錄。一筆可能橫跨多個實體行（例外堆疊追蹤）。
/// </summary>
public sealed class LogEntry
{
    /// <summary>讀取順序（時間正序）。作為表格 RowKey，且不依賴可能解析失敗的時間戳。</summary>
    public int Sequence { get; set; }

    public DateTime Timestamp { get; set; }

    public string TraceId { get; set; } = string.Empty;

    /// <summary>原始等級字串，例如 INFO。無法解析時為空字串。</summary>
    public string Level { get; set; } = string.Empty;

    public LogLevelRank Rank { get; set; } = LogLevelRank.Unknown;

    public string ThreadId { get; set; } = string.Empty;

    public string Logger { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>未經任何處理的原始文字，含所有續行。匯出與關鍵字比對都以此為準。</summary>
    public string Raw { get; set; } = string.Empty;

    public bool HasException { get; set; }
}

public sealed class LogQueryResult
{
    /// <summary>查詢結果，<b>時間正序（舊→新）</b>。匯出直接沿用此順序。</summary>
    public IReadOnlyList<LogEntry> Entries { get; set; } = [];

    public int ScannedFileCount { get; set; }

    public int ScannedLineCount { get; set; }

    /// <summary>給使用者看的狀態訊息（無資料、目錄不存在等）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>非致命問題：條件被夾住、個別檔案讀取失敗等。</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>實際套用的起始時間（可能因區間上限被夾過），供畫面回寫選擇器。</summary>
    public DateTime AppliedStartTime { get; set; }

    public DateTime AppliedEndTime { get; set; }
}
