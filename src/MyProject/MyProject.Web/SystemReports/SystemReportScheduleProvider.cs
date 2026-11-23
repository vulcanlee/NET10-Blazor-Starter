using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 系統巡檢報告的執行時間（日報 0.10.39 起，週報與月報 0.10.41 起）：把系統參數的時間、星期幾、幾號換成 cron，
/// 排程器約一分鐘內跟上。設定讀不到（執行中被改壞）時回 false，排程器改用預設時間。
/// </summary>
public sealed class SystemReportScheduleProvider : IScheduledJobCronOverride
{
    private readonly IOptionsMonitor<SystemReportSettings> settings;

    public SystemReportScheduleProvider(IOptionsMonitor<SystemReportSettings> settings)
    {
        this.settings = settings;
    }

    public IReadOnlyCollection<string> JobNames { get; } =
        [SystemReportDailyJob.JobName, SystemReportWeeklyJob.JobName, SystemReportMonthlyJob.JobName];

    public bool TryGetCron(string jobName, out string cron)
    {
        cron = string.Empty;
        try
        {
            var current = settings.CurrentValue;
            return jobName switch
            {
                SystemReportDailyJob.JobName => TryBuildDaily(current.DailyTime, out cron),
                SystemReportWeeklyJob.JobName => TryBuildWeekly(current.WeeklyDayOfWeek, current.WeeklyTime, out cron),
                SystemReportMonthlyJob.JobName => TryBuildMonthly(current.MonthlyDay, current.MonthlyTime, out cron),
                _ => false,
            };
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

    /// <summary>星期一（1）「08:00」→「0 8 * * 1」；星期日（7）→ cron 的 0。</summary>
    public static bool TryBuildWeekly(int dayOfWeek, string? time, out string cron)
    {
        cron = string.Empty;
        if (dayOfWeek is < 1 or > 7 || !SystemReportSettings.TryParseTime(time, out var hour, out var minute))
        {
            return false;
        }

        cron = $"{minute} {hour} * * {dayOfWeek % 7}";
        return true;
    }

    /// <summary>1 號「08:30」→「30 8 1 * *」（只開放 1～28 號，每個月都有）。</summary>
    public static bool TryBuildMonthly(int day, string? time, out string cron)
    {
        cron = string.Empty;
        if (day is < 1 or > 28 || !SystemReportSettings.TryParseTime(time, out var hour, out var minute))
        {
            return false;
        }

        cron = $"{minute} {hour} {day} * *";
        return true;
    }
}
