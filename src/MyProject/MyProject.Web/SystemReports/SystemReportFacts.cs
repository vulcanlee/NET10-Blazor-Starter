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

    /// <summary>週報、月報（有趨勢比較、容量成長、備份可還原驗證）。</summary>
    public bool IsPeriodic => Frequency is SystemReportFrequencies.Weekly or SystemReportFrequencies.Monthly;

    /// <summary>月報才有 AI 費用。</summary>
    public bool IncludesAiCost => Frequency == SystemReportFrequencies.Monthly;

    /// <summary>
    /// 季末那個月的月報（在 1／4／7／10 月產生）附帳號與權限清查，涵蓋剛結束的那一季（使用者 2026/11/20 決定）。
    /// </summary>
    public bool IncludesAccountReview => Frequency == SystemReportFrequencies.Monthly && Start.Month % 3 == 0;

    /// <summary>帳號清查涵蓋的季度：這個月往前共三個月。</summary>
    public SystemReportPeriod Quarter => new(Frequency, Start.AddMonths(-2), End);

    /// <summary>上一期（趨勢比較用）。</summary>
    public SystemReportPeriod Previous() => Containing(Frequency, Start.AddDays(-1));

    /// <summary>
    /// 某一天所在的區間：日報＝那一天；週報＝那一天所在的週一～週日；月報＝那一天所在的月份。指定區間不適用（丟例外）。
    /// </summary>
    public static SystemReportPeriod Containing(string frequency, DateTime date)
    {
        var day = date.Date;
        return frequency switch
        {
            SystemReportFrequencies.Daily => new(frequency, day, day.AddDays(1)),
            SystemReportFrequencies.Weekly => new(frequency, day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), day.AddDays(-(((int)day.DayOfWeek + 6) % 7) + 7)),
            SystemReportFrequencies.Monthly => new(frequency, new DateTime(day.Year, day.Month, 1), new DateTime(day.Year, day.Month, 1).AddMonths(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "只有日報、週報、月報有固定的區間。"),
        };
    }

    /// <summary>指定區間（0.10.42 起，系統管理助理）：起日 00:00～迄日隔天 00:00（迄日是今天時，數字只到現在）。</summary>
    public static SystemReportPeriod Custom(DateTime start, DateTime endInclusive)
        => new(SystemReportFrequencies.Custom, start.Date, endInclusive.Date.AddDays(1));

    /// <summary>今天以前最近一個完整的區間：日報＝昨天；週報＝上週一～週日；月報＝上個月。</summary>
    public static SystemReportPeriod LastComplete(string frequency, DateTime today)
        => Containing(frequency, frequency switch
        {
            SystemReportFrequencies.Weekly => today.Date.AddDays(-7),
            SystemReportFrequencies.Monthly => new DateTime(today.Year, today.Month, 1).AddDays(-1),
            _ => today.Date.AddDays(-1),
        });

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

    // ───────────── 週報、月報（0.10.41 起）─────────────

    /// <summary>伺服器的時區（稽核時間是 UTC，顯示時換成本地）。</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>上一期的統計（週報、月報的趨勢比較）。</summary>
    public SystemReportMetrics? PreviousMetrics { get; init; }

    /// <summary>區間內（與預估用）的容量快照；容量成長用。</summary>
    public IReadOnlyList<CapacitySnapshotRow>? CapacityHistory { get; init; }

    /// <summary>最新一份備份的可還原驗證（週報、月報）；沒有備份或沒有驗證時為 null。</summary>
    public BackupVerificationResult? BackupVerification { get; init; }

    /// <summary>AI 費用（月報）。</summary>
    public SystemReportAiCost? AiCost { get; init; }

    /// <summary>帳號與權限清查（1／4／7／10 月產生的月報）。</summary>
    public SystemReportAccountReview? AccountReview { get; init; }

    public List<SystemReportSourceFailure> Failures { get; init; } = [];

    public List<string> Limitations { get; init; } = [];
}
