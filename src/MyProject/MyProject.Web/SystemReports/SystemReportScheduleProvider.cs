using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 系統巡檢報告的執行時間（0.10.39 起）：把系統參數的「日報產生時間」換成 cron，排程器約一分鐘內跟上。
/// 設定讀不到（執行中被改壞）時回 false，排程器改用預設時間。
/// </summary>
public sealed class SystemReportScheduleProvider : IScheduledJobCronOverride
{
    private readonly IOptionsMonitor<SystemReportSettings> settings;

    public SystemReportScheduleProvider(IOptionsMonitor<SystemReportSettings> settings)
    {
        this.settings = settings;
    }

    public IReadOnlyCollection<string> JobNames { get; } = [SystemReportDailyJob.JobName];

    public bool TryGetCron(string jobName, out string cron)
    {
        cron = string.Empty;
        if (jobName != SystemReportDailyJob.JobName)
        {
            return false;
        }

        try
        {
            return TryBuildDaily(settings.CurrentValue.DailyTime, out cron);
        }
        catch (OptionsValidationException)
        {
            return false;
        }
    }

    /// <summary>「07:30」→「30 7 * * *」。</summary>
    public static bool TryBuildDaily(string? time, out string cron)
    {
        cron = string.Empty;
        if (!SystemReportSettings.TryParseTime(time, out var hour, out var minute))
        {
            return false;
        }

        cron = $"{minute} {hour} * * *";
        return true;
    }
}
