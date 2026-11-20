using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Admins;

/// <summary>
/// 系統巡檢報告的查詢條件（0.10.39 起，<c>POST api/v1/system-reports/search</c>，只限管理員）。
/// 日期比對產生時間（伺服器本地時間；To 為當天，含整天）。
/// </summary>
public class SystemReportSearchRequestDto
{
    /// <summary>Daily、Weekly、Monthly、Custom；空白不限。</summary>
    public string? Frequency { get; set; }

    /// <summary>Red、Orange、Yellow、Green；空白不限。</summary>
    public string? Level { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "頁碼必須大於或等於 1。")]
    public int PageIndex { get; set; } = 1;

    /// <summary>每頁筆數，1～200。</summary>
    [Range(1, 200, ErrorMessage = "每頁筆數必須介於 1 到 200 之間。")]
    public int PageSize { get; set; } = 20;
}

/// <summary>清單的一列。時間是伺服器本地時間；PeriodEnd 不含。</summary>
public class SystemReportSummaryDto
{
    public Guid ReportId { get; set; }

    public string Frequency { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Red、Orange、Yellow、Green。</summary>
    public string Level { get; set; } = string.Empty;

    public int RedCount { get; set; }

    public int OrangeCount { get; set; }

    public int YellowCount { get; set; }

    /// <summary>Schedule、CatchUp、Manual、Assistant。</summary>
    public string Trigger { get; set; } = string.Empty;

    public string? TriggeredByAccount { get; set; }

    /// <summary>Written、Fallback、Skipped。</summary>
    public string NarrativeStatus { get; set; } = string.Empty;

    public string? ModelName { get; set; }

    /// <summary>NotRequired、Sent、Failed。</summary>
    public string DeliveryStatus { get; set; } = string.Empty;
}

/// <summary>清單的回應：分頁資料＋各頻率的保留天數（畫面顯示「保存 N 天」）。</summary>
public class SystemReportListDto
{
    public List<SystemReportSummaryDto> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int PageIndex { get; set; }

    public int PageSize { get; set; }

    public int DailyRetentionDays { get; set; }
}

/// <summary>一份報告：清單資料＋內容（內容檔不見時為 null）。</summary>
public class SystemReportDetailDto
{
    public SystemReportSummaryDto Summary { get; set; } = new();

    public SystemReportContentDto? Content { get; set; }
}

/// <summary>
/// 「立即產生」的請求（<c>POST api/v1/system-reports/generate</c>）。報告在背景產生（可能超過一分鐘），完成時以站內通知告訴產生者。
/// </summary>
public class SystemReportGenerateRequestDto
{
    /// <summary>Daily（0.10.39 起只開放日報；週報、月報在之後的版本）。</summary>
    [Required(ErrorMessage = "請選擇報告種類。")]
    public string Frequency { get; set; } = string.Empty;

    /// <summary>區間內的任一天（日報＝那一天）；必須早於今天。</summary>
    [Required(ErrorMessage = "請選擇日期。")]
    public DateTime? Date { get; set; }

    /// <summary>產生後寄給所有啟用中的管理員（不論燈號）。</summary>
    public bool SendToAdmins { get; set; }
}

/// <summary>
/// 報告內容（0.10.39 起）：也是內容檔（JSON）的格式。燈號、數字與三段說明都由程式規則產生；
/// <see cref="Narrative"/> 是 AI 寫的說明（Markdown，畫面自己消毒成 HTML），沒有時為 null。
/// ⚠️ 內容不放堆疊、設定值、檔案路徑；帳號只放數量（速查表 §6.34）。
/// </summary>
public class SystemReportContentDto
{
    /// <summary>格式版本；欄位有不相容的變動時加 1。</summary>
    public int SchemaVersion { get; set; } = 1;

    public Guid ReportId { get; set; }

    public string Frequency { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    public DateTime CreatedAt { get; set; }

    public string SystemName { get; set; } = string.Empty;

    public string SystemVersion { get; set; } = string.Empty;

    /// <summary>整體燈號。</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>整體一句話（程式產生），例如「有 1 個已發生的問題需要馬上處理」。</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>所有發現，依燈號由重到輕、同燈號依區塊排序。綠燈不列。</summary>
    public List<SystemReportFindingDto> Findings { get; set; } = [];

    /// <summary>需要準備擴充的磁碟（預估一年內用完）。</summary>
    public List<SystemReportExpansionDto> Expansions { get; set; } = [];

    /// <summary>下期預測（程式依成長速度、配額與排程推算的固定句型）。</summary>
    public List<string> Predictions { get; set; } = [];

    public List<SystemReportVolumeDto> Volumes { get; set; } = [];

    public List<SystemReportFolderDto> Folders { get; set; } = [];

    public SystemReportDatabaseDto? Database { get; set; }

    /// <summary>各區塊的統計數字（例如「錯誤與例外／Error 以上日誌／12 筆」）。</summary>
    public List<SystemReportStatDto> Stats { get; set; } = [];

    /// <summary>量測與資料的限制（例如「重新啟動後日誌管線計數歸零」「容量快照只有 3 天，暫時無法預估」）。</summary>
    public List<string> Limitations { get; set; } = [];

    /// <summary>AI 寫的說明（Markdown）；沒有時為 null。</summary>
    public string? Narrative { get; set; }

    /// <summary>Written、Fallback、Skipped。</summary>
    public string NarrativeStatus { get; set; } = string.Empty;

    /// <summary>沒有 AI 說明的原因（固定用語）。</summary>
    public string? NarrativeNote { get; set; }
}

/// <summary>一項發現：哪裡有問題、不處理會怎樣、怎麼處理、處理完會怎樣。</summary>
public class SystemReportFindingDto
{
    /// <summary>規則代碼，例如 CAP-FREE。</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>所屬區塊（固定名稱）。</summary>
    public string Section { get; set; } = string.Empty;

    /// <summary>Red、Orange、Yellow。</summary>
    public string Level { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>現況（含數字）。</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>即將發生的期限（本地日期）；沒有時為 null。</summary>
    public DateTime? Deadline { get; set; }

    public string IfIgnored { get; set; } = string.Empty;

    public string HowToFix { get; set; } = string.Empty;

    public string AfterFix { get; set; } = string.Empty;

    /// <summary>處理的頁面（站內相對路徑）；沒有對應頁面時為 null。</summary>
    public string? ActionRoute { get; set; }
}

/// <summary>需要準備擴充的磁碟。</summary>
public class SystemReportExpansionDto
{
    public string Volume { get; set; } = string.Empty;

    public DateTime? FullDate { get; set; }

    /// <summary>建議擴充的容量（位元組）：一年的成長量再加 15%。</summary>
    public long SuggestedBytes { get; set; }
}

/// <summary>一顆磁碟。名稱只有磁碟代號或掛載點根目錄，加上用途（不放完整路徑）。</summary>
public class SystemReportVolumeDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>用途，例如「程式、資料庫、日誌」。</summary>
    public string Purposes { get; set; } = string.Empty;

    public long TotalBytes { get; set; }

    public long FreeBytes { get; set; }

    public double FreePercent { get; set; }

    /// <summary>每天平均成長（位元組）；快照不夠時為 null。</summary>
    public long? DailyGrowthBytes { get; set; }

    /// <summary>預估還剩幾天用完；不會用完或快照不夠時為 null。</summary>
    public int? DaysToFull { get; set; }

    public DateTime? FullDate { get; set; }

    /// <summary>用來預估的快照天數。</summary>
    public int SampleDays { get; set; }

    public string Level { get; set; } = string.Empty;
}

/// <summary>一個資料目錄（用途名稱，不放路徑）。</summary>
public class SystemReportFolderDto
{
    public string Name { get; set; } = string.Empty;

    public string Volume { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    public long? ItemCount { get; set; }

    /// <summary>Ok、Partial、Missing、Unavailable。</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>資料庫檔案。</summary>
public class SystemReportDatabaseDto
{
    public long MainBytes { get; set; }

    public long WalBytes { get; set; }

    /// <summary>可回收的空頁（位元組）。</summary>
    public long FreePageBytes { get; set; }
}

/// <summary>一個統計數字。</summary>
public class SystemReportStatDto
{
    public string Section { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
