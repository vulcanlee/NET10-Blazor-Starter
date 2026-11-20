using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Backup;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Health;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 收集系統巡檢報告的事實（0.10.39 起）。每個區塊各自 try/catch：一個來源讀不到只會變成一則「無法查詢」的黃燈，
/// 不讓整份報告失敗。不花錢、不對外連線（健康檢查用不花錢模式），沒有登入使用者也能跑（排程作業）。
///
/// ⚠️ 不寫稽核：這是系統自己的巡檢，不是有人在瀏覽資料。不要改呼叫會寫瀏覽稽核的查詢方法（例如系統管理助理的工具）。
/// </summary>
public sealed class SystemReportCollector
{
    private readonly ISystemHealthService healthService;
    private readonly ScheduledJobOverviewService jobOverview;
    private readonly BackupStore backupStore;
    private readonly SystemReportMetricsService metricsService;
    private readonly ILogQueryService logQueryService;
    private readonly CapacityProbe capacityProbe;
    private readonly CapacitySnapshotService snapshotService;
    private readonly IAiQuotaService quotaService;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly IOptionsMonitor<AiSettings> aiOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemReportCollector> logger;

    public SystemReportCollector(
        ISystemHealthService healthService,
        ScheduledJobOverviewService jobOverview,
        BackupStore backupStore,
        SystemReportMetricsService metricsService,
        ILogQueryService logQueryService,
        CapacityProbe capacityProbe,
        CapacitySnapshotService snapshotService,
        IAiQuotaService quotaService,
        IOptionsMonitor<EmailSettings> emailOptions,
        IOptionsMonitor<AiSettings> aiOptions,
        TimeProvider timeProvider,
        ILogger<SystemReportCollector> logger)
    {
        this.healthService = healthService;
        this.jobOverview = jobOverview;
        this.backupStore = backupStore;
        this.metricsService = metricsService;
        this.logQueryService = logQueryService;
        this.capacityProbe = capacityProbe;
        this.snapshotService = snapshotService;
        this.quotaService = quotaService;
        this.emailOptions = emailOptions;
        this.aiOptions = aiOptions;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<SystemReportFacts> CollectAsync(SystemReportPeriod period, CancellationToken cancellationToken)
    {
        var nowLocal = timeProvider.GetLocalNow().DateTime;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var zone = timeProvider.LocalTimeZone;
        var failures = new List<SystemReportSourceFailure>();
        var limitations = new List<string>();

        var health = await TryAsync(SystemReportSections.Health, "健康檢查", failures,
            () => healthService.GetPassiveItemsAsync(cancellationToken));
        var jobs = await TryAsync(SystemReportSections.Jobs, "排程作業狀態", failures, jobOverview.GetOverviewAsync);
        var backups = await TryAsync(SystemReportSections.Backup, "備份清單", failures,
            () => Task.FromResult(backupStore.List()));

        TimeSpan? backupInterval = null;
        DateTime? nextBackup = null;
        if (jobs?.Items.FirstOrDefault(x => x.Name == SystemBackupJob.JobName) is { } backupJob && backupJob.IsEnabled
            && ScheduleCalculator.TryParse(backupJob.Cron, out var backupCron))
        {
            var first = ScheduleCalculator.NextOccurrenceUtc(backupCron!, nowUtc, zone);
            var second = first is { } f ? ScheduleCalculator.NextOccurrenceUtc(backupCron!, f, zone) : null;
            if (first is { } a && second is { } b)
            {
                backupInterval = b - a;
            }

            nextBackup = backupJob.NextRunLocal;
        }

        var range = new SystemReportMetricsRange(
            period.Start, period.End,
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(period.Start, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(period.End, DateTimeKind.Unspecified), zone),
            nowUtc);
        var metrics = await TryAsync(SystemReportSections.Errors, "例外、排程、寄信、AI 與安全事件的統計", failures,
            () => metricsService.GetAsync(range, cancellationToken));

        var (errorCount, errorCapped) = await CountErrorLogsAsync(period, failures, cancellationToken);
        if (errorCapped)
        {
            limitations.Add($"Error 以上的日誌每天最多數 {LogQueryRequest.MaxTake:N0} 筆，報告上的筆數是「至少」。");
        }

        var capacity = await TryAsync(SystemReportSections.Capacity, "磁碟與資料目錄", failures,
            () => capacityProbe.MeasureAsync(cancellationToken));
        var volumes = new List<SystemReportVolumeFact>();
        if (capacity is not null)
        {
            limitations.AddRange(capacity.Notes);
            var history = await TryAsync(SystemReportSections.Capacity, "容量快照", failures,
                () => snapshotService.GetHistoryAsync(nowLocal.Date.AddDays(-CapacityForecast.WindowDays)));
            foreach (var volume in capacity.Volumes)
            {
                var samples = (history ?? [])
                    .Where(x => x.Category == CapacitySnapshotKinds.Volume && string.Equals(x.Key, volume.Root, StringComparison.OrdinalIgnoreCase) && x.UsedBytes is not null)
                    .Select(x => (x.SnapshotDate, x.UsedBytes!.Value))
                    .ToList();
                // 今天的量測也算一筆（快照作業還沒跑時也能用到最新的數字）。
                samples.RemoveAll(x => x.SnapshotDate.Date == nowLocal.Date);
                samples.Add((nowLocal.Date, volume.TotalBytes - volume.FreeBytes));
                volumes.Add(new SystemReportVolumeFact(volume, CapacityForecast.Predict(samples, volume.FreeBytes)));
            }

            if (volumes.Count > 0 && volumes.All(x => !x.Forecast.HasEnoughData))
            {
                var days = volumes.Max(x => x.Forecast.SampleDays);
                limitations.Add($"容量快照目前只有 {days} 天（至少要 {CapacityForecast.MinimumSampleDays} 天），暫時無法預估幾天後用完，只看剩餘空間。");
            }
        }

        var aiConfigured = AiChatEndpoint.Validate(aiOptions.CurrentValue) is null;
        var quota = aiConfigured
            ? await TryAsync(SystemReportSections.Ai, "AI 用量上限", failures, () => quotaService.GetStatusAsync(null, cancellationToken))
            : null;

        limitations.Add("日誌管線與告警的計數從網站上次啟動起算，重新啟動後歸零。");

        return new SystemReportFacts
        {
            Period = period,
            NowLocal = nowLocal,
            Health = health,
            Jobs = jobs,
            Backups = backups,
            BackupInterval = backupInterval,
            NextBackupLocal = nextBackup,
            Metrics = metrics,
            ErrorLogCount = errorCount,
            ErrorLogCapped = errorCapped,
            Capacity = capacity,
            Volumes = volumes,
            EmailProvider = emailOptions.CurrentValue.Provider,
            AiConfigured = aiConfigured,
            Quota = quota,
            Failures = failures,
            Limitations = limitations,
        };
    }

    /// <summary>區間內 Error 以上的日誌筆數：一天查一次（日誌查詢一次最多 3 天、最多 <see cref="LogQueryRequest.MaxTake"/> 筆）。</summary>
    private async Task<(int? Count, bool Capped)> CountErrorLogsAsync(SystemReportPeriod period, List<SystemReportSourceFailure> failures, CancellationToken cancellationToken)
    {
        try
        {
            var total = 0;
            var capped = false;
            for (var day = period.Start; day < period.End; day = day.AddDays(1))
            {
                var end = day.AddDays(1) < period.End ? day.AddDays(1) : period.End;
                var result = await logQueryService.QueryAsync(new LogQueryRequest
                {
                    StartTime = day,
                    EndTime = end.AddTicks(-1),
                    MinimumLevel = LogLevelRank.Error,
                    Take = LogQueryRequest.MaxTake,
                }, cancellationToken);
                var count = result.Entries.Count(x => x.Rank is LogLevelRank.Error or LogLevelRank.Fatal);
                total += count;
                capped |= result.Entries.Count >= LogQueryRequest.MaxTake;
            }

            return (total, capped);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "System report source failed. Source={Source}", "ErrorLogs");
            failures.Add(new SystemReportSourceFailure(SystemReportSections.Errors, "日誌檔"));
            return (null, false);
        }
    }

    private async Task<T?> TryAsync<T>(string section, string source, List<SystemReportSourceFailure> failures, Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "System report source failed. Section={Section}", section);
            failures.Add(new SystemReportSourceFailure(section, source));
            return null;
        }
    }
}
