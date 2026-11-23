namespace MyProject.Models.Systems;

/// <summary>
/// 系統巡檢報告的燈號（0.10.39 起）。由程式規則判定，不是模型。存進資料表的是英文代碼，畫面與 PDF 顯示中文。
/// ⚠️ 畫面不可以用 emoji 表示燈號（速查表 §6.1），一律用文字＋色票。
/// </summary>
public static class SystemReportLevels
{
    /// <summary>已發生問題，要馬上處理。</summary>
    public const string Red = "Red";

    /// <summary>即將發生，有期限要處理。</summary>
    public const string Orange = "Orange";

    /// <summary>注意即可。</summary>
    public const string Yellow = "Yellow";

    /// <summary>正常。</summary>
    public const string Green = "Green";

    public static IReadOnlyList<string> All { get; } = [Red, Orange, Yellow, Green];

    /// <summary>嚴重程度：紅 3、橙 2、黃 1、綠 0。</summary>
    public static int Rank(string level) => level switch
    {
        Red => 3,
        Orange => 2,
        Yellow => 1,
        _ => 0,
    };

    public static string Max(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>短名稱：紅燈、橙燈、黃燈、綠燈。</summary>
    public static string ShortName(string level) => level switch
    {
        Red => "紅燈",
        Orange => "橙燈",
        Yellow => "黃燈",
        Green => "綠燈",
        _ => level,
    };

    /// <summary>意義：已發生問題、即將發生、注意、正常。</summary>
    public static string Meaning(string level) => level switch
    {
        Red => "已發生問題",
        Orange => "即將發生",
        Yellow => "注意",
        Green => "正常",
        _ => level,
    };
}

/// <summary>報告頻率（0.10.39 起）。</summary>
public static class SystemReportFrequencies
{
    public const string Daily = "Daily";

    public const string Weekly = "Weekly";

    public const string Monthly = "Monthly";

    /// <summary>手動指定區間（系統管理助理對話）。</summary>
    public const string Custom = "Custom";

    public static IReadOnlyList<string> All { get; } = [Daily, Weekly, Monthly, Custom];

    public static string DisplayName(string frequency) => frequency switch
    {
        Daily => "日報",
        Weekly => "週報",
        Monthly => "月報",
        Custom => "指定區間",
        _ => frequency,
    };
}

/// <summary>報告的觸發方式（0.10.39 起）。前三個與 <see cref="JobRunTriggers"/> 相同。</summary>
public static class SystemReportTriggers
{
    public const string Schedule = JobRunTriggers.Schedule;

    public const string CatchUp = JobRunTriggers.CatchUp;

    /// <summary>「排程作業」頁的立即執行，或報告頁的「立即產生」。</summary>
    public const string Manual = JobRunTriggers.Manual;

    /// <summary>系統管理助理對話裡要求的巡檢。</summary>
    public const string Assistant = "Assistant";
}

/// <summary>AI 說明的狀態（0.10.39 起）。</summary>
public static class SystemReportNarrativeStatuses
{
    /// <summary>AI 寫好了。</summary>
    public const string Written = "Written";

    /// <summary>AI 呼叫失敗或回應不合格式，改用固定句型。</summary>
    public const string Fallback = "Fallback";

    /// <summary>沒有呼叫 AI（未設定、參數關閉、日報全部綠燈）。</summary>
    public const string Skipped = "Skipped";
}

/// <summary>通知狀態（0.10.39 起）。</summary>
public static class SystemReportDeliveryStatuses
{
    /// <summary>不需要通知（例如日報沒有橙燈以上、手動產生沒勾寄送）。</summary>
    public const string NotRequired = "NotRequired";

    public const string Sent = "Sent";

    public const string Failed = "Failed";
}

/// <summary>
/// 報告區塊（發現所屬的章節）。固定名稱，畫面、PDF 與 Email 用同一組。
/// </summary>
public static class SystemReportSections
{
    public const string Health = "系統健康";

    public const string Errors = "錯誤與例外";

    public const string Jobs = "排程作業";

    public const string Backup = "備份";

    public const string Capacity = "容量";

    public const string Email = "寄信";

    public const string Ai = "AI";

    public const string Security = "帳號與安全";

    public static IReadOnlyList<string> All { get; } = [Health, Errors, Jobs, Backup, Capacity, Email, Ai, Security];
}

/// <summary>週報、月報章節的代碼（0.10.41 起，<c>SystemReportSectionDto.Key</c>）。</summary>
public static class SystemReportSectionKeys
{
    /// <summary>和上一期比較（週報、月報）。</summary>
    public const string Trend = "Trend";

    /// <summary>容量成長與一年後的需求（週報、月報）。</summary>
    public const string Growth = "Growth";

    /// <summary>備份可還原驗證（週報、月報）。</summary>
    public const string BackupVerify = "BackupVerify";

    /// <summary>AI 費用（月報）。</summary>
    public const string AiCost = "AiCost";

    /// <summary>帳號與權限清查（1／4／7／10 月產生的月報，涵蓋剛結束的那一季）。</summary>
    public const string AccountReview = "AccountReview";
}

/// <summary>要存的一份報告（中繼資料＋內容 JSON）。</summary>
public sealed record SystemReportDraft(
    Guid ReportId,
    string Frequency,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime CreatedAt,
    string Level,
    int RedCount,
    int OrangeCount,
    int YellowCount,
    string Trigger,
    string? TriggeredByAccount,
    int? JobRunId,
    string NarrativeStatus,
    string? ModelName,
    string DeliveryStatus,
    string ContentJson);

/// <summary>報告清單的一列。</summary>
public sealed record SystemReportSummary(
    Guid ReportId,
    string Frequency,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime CreatedAt,
    string Level,
    int RedCount,
    int OrangeCount,
    int YellowCount,
    string Trigger,
    string? TriggeredByAccount,
    string NarrativeStatus,
    string? ModelName,
    string DeliveryStatus);

/// <summary>一份報告：清單資料＋內容 JSON（內容檔不見時為 null）。</summary>
public sealed record SystemReportDetail(SystemReportSummary Summary, string? ContentJson);

/// <summary>清單的篩選條件。日期比對 <see cref="SystemReportSummary.CreatedAt"/>（本地時間，結束不含）。</summary>
public sealed record SystemReportFilter(string? Frequency, string? Level, DateTime? From, DateTime? To, int PageIndex, int PageSize);

/// <summary>容量快照的一列（服務層進出用，對應資料表 <c>CapacitySnapshot</c>）。</summary>
public sealed record CapacitySnapshotRow(
    DateTime SnapshotDate,
    string Category,
    string Key,
    long? UsedBytes,
    long? TotalBytes,
    long? FreeBytes,
    long? ItemCount,
    string Status);

/// <summary>容量快照的種類與狀態。</summary>
public static class CapacitySnapshotKinds
{
    public const string Volume = "Volume";

    public const string Folder = "Folder";

    public const string Database = "Database";

    public const string StatusOk = "Ok";

    /// <summary>量測逾時，數字是下限。</summary>
    public const string StatusPartial = "Partial";

    /// <summary>目錄不存在或沒有設定。</summary>
    public const string StatusMissing = "Missing";

    /// <summary>讀不到（權限、磁碟離線）。</summary>
    public const string StatusUnavailable = "Unavailable";
}
