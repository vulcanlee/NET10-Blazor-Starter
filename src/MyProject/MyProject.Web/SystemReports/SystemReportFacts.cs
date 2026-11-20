using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Backup;
using MyProject.Web.Health;
using MyProject.Web.Scheduling;

namespace MyProject.Web.SystemReports;

/// <summary>一個區間（本地時間，結束不含）。</summary>
public sealed record SystemReportPeriod(string Frequency, DateTime Start, DateTime End)
{
    public int Days => Math.Max(1, (int)Math.Round((End - Start).TotalDays));

    /// <summary>給人看的區間，例如「2026/11/19」或「2026/11/09～2026/11/15」。</summary>
    public string Display => Days == 1 ? $"{Start:yyyy/MM/dd}" : $"{Start:yyyy/MM/dd}～{End.AddDays(-1):yyyy/MM/dd}";
}

/// <summary>一個資料來源讀取失敗（變成黃燈「無法查詢」，不讓整份報告失敗）。</summary>
public sealed record SystemReportSourceFailure(string Section, string Source);

/// <summary>一顆磁碟加上預估。</summary>
public sealed record SystemReportVolumeFact(CapacityVolumeUsage Usage, CapacityForecastResult Forecast)
{
    public double FreePercent => Usage.TotalBytes <= 0 ? 0 : Usage.FreeBytes * 100.0 / Usage.TotalBytes;
}

/// <summary>
/// 產生報告前收集到的全部事實（0.10.39 起）。規則只讀這裡，不自己查資料，所以規則是純函式、好測試。
/// 每個欄位讀取失敗時是 null，並在 <see cref="Failures"/> 記一筆。
/// </summary>
public sealed class SystemReportFacts
{
    public required SystemReportPeriod Period { get; init; }

    public required DateTime NowLocal { get; init; }

    /// <summary>健康檢查（不花錢模式，見 <see cref="ISystemHealthService.GetPassiveItemsAsync"/>）。</summary>
    public IReadOnlyList<SystemHealthItem>? Health { get; init; }

    public ScheduledJobOverview? Jobs { get; init; }

    public IReadOnlyList<BackupFileInfo>? Backups { get; init; }

    /// <summary>系統備份兩次排程之間的間隔；作業停用或 cron 不合法時為 null。</summary>
    public TimeSpan? BackupInterval { get; init; }

    /// <summary>系統備份的下一次執行時間（本地時間）；停用時為 null。</summary>
    public DateTime? NextBackupLocal { get; init; }

    public SystemReportMetrics? Metrics { get; init; }

    /// <summary>區間內 Error 以上的日誌筆數；每天最多數到上限，達上限時 <see cref="ErrorLogCapped"/> 為 true。</summary>
    public int? ErrorLogCount { get; init; }

    public bool ErrorLogCapped { get; init; }

    public CapacityProbeResult? Capacity { get; init; }

    public IReadOnlyList<SystemReportVolumeFact> Volumes { get; init; } = [];

    /// <summary>寄信的方式：None、Pickup、Smtp。</summary>
    public string? EmailProvider { get; init; }

    /// <summary>AI 設定完整（AI 規則只在設定完整時判斷）。</summary>
    public bool AiConfigured { get; init; }

    /// <summary>全系統的 AI 用量上限狀態。</summary>
    public IReadOnlyList<AiQuotaLimitStatus>? Quota { get; init; }

    public List<SystemReportSourceFailure> Failures { get; init; } = [];

    public List<string> Limitations { get; init; } = [];
}
