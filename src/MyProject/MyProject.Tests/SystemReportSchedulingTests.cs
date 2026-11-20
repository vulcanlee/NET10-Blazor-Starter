using System.ComponentModel.DataAnnotations;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Configuration.Validation;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;
using MyProject.Web.SystemReports;

namespace MyProject.Tests;

/// <summary>
/// 系統巡檢報告的時間與設定（0.10.39 起）：容量預估、日報時間換成 cron、程式提供的 cron 優先於設定檔、
/// 舊作業名稱不讓網站啟動失敗、設定的跨欄位驗證、日報的區間。
/// </summary>
public sealed class SystemReportSchedulingTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Today = new(2026, 11, 20);

    // ───────────── 容量預估 ─────────────

    [Fact]
    public void Forecast_NeedsSevenDays()
    {
        var result = CapacityForecast.Predict(Days(6, day => day * Gb), freeBytes: 100 * Gb);

        Assert.Equal(6, result.SampleDays);
        Assert.False(result.HasEnoughData);
        Assert.Null(result.DaysToFull);
    }

    [Fact]
    public void Forecast_LinearGrowth_DaysToFull()
    {
        var result = CapacityForecast.Predict(Days(10, day => 50 * Gb + day * Gb), freeBytes: 30 * Gb);

        Assert.Equal(Gb, result.DailyGrowthBytes);
        Assert.Equal(30, result.DaysToFull);
    }

    [Fact]
    public void Forecast_ShrinkingOrFlat_NeverFills()
    {
        var shrinking = CapacityForecast.Predict(Days(10, day => 50 * Gb - day * Gb), freeBytes: 30 * Gb);
        var flat = CapacityForecast.Predict(Days(10, _ => 50 * Gb), freeBytes: 30 * Gb);

        Assert.True(shrinking.DailyGrowthBytes < 0);
        Assert.Null(shrinking.DaysToFull);
        Assert.Equal(0, flat.DailyGrowthBytes);
        Assert.Null(flat.DaysToFull);
    }

    [Fact]
    public void Forecast_UsesUsedBytes_SoEnlargingTheDiskDoesNotBreakTheTrend()
    {
        // 已用空間照常每天 +1 GB；磁碟在第 5 天擴充（可用空間跳上去），預估只看已用空間的斜率與現在的可用空間。
        var result = CapacityForecast.Predict(Days(10, day => 50 * Gb + day * Gb), freeBytes: 500 * Gb);

        Assert.Equal(Gb, result.DailyGrowthBytes);
        Assert.Equal(500, result.DaysToFull);
    }

    [Fact]
    public void Forecast_OnlyUsesTheRecentWindow()
    {
        var old = Days(200, day => day < 100 ? day * 10 * Gb : 1000 * Gb + day * Gb).ToList();

        var result = CapacityForecast.Predict(old, freeBytes: 100 * Gb);

        Assert.Equal(CapacityForecast.WindowDays, result.SampleDays);
        Assert.Equal(Gb, result.DailyGrowthBytes);
    }

    // ───────────── 時間與 cron ─────────────

    [Theory]
    [InlineData("07:30", "30 7 * * *")]
    [InlineData("00:00", "0 0 * * *")]
    [InlineData("23:59", "59 23 * * *")]
    public void DailyTime_BecomesCron(string time, string expected)
    {
        Assert.True(SystemReportScheduleProvider.TryBuildDaily(time, out var cron));
        Assert.Equal(expected, cron);
    }

    [Theory]
    [InlineData("7:30")]
    [InlineData("24:00")]
    [InlineData("07:60")]
    [InlineData("")]
    [InlineData(null)]
    public void DailyTime_Invalid_IsRejected(string? time)
    {
        Assert.False(SystemReportScheduleProvider.TryBuildDaily(time, out _));
        Assert.Contains(Validate(new SystemReportSettings { DailyTime = time! }), x => x.MemberNames.Contains(nameof(SystemReportSettings.DailyTime)));
    }

    [Fact]
    public void ProviderCron_WinsOverTheSettingsFile_AndOtherJobsAreUntouched()
    {
        var provider = new SystemReportScheduleProvider(new StaticOptionsMonitor<SystemReportSettings>(new SystemReportSettings { DailyTime = "06:15" }));
        var settings = new ScheduledJobSettings();
        settings.Jobs[SystemReportDailyJob.JobName] = new ScheduledJobOverride { Cron = "0 1 * * *" };
        var daily = new ScheduledJobDescriptor(SystemReportDailyJob.JobName, "日報", string.Empty, "30 7 * * *", typeof(SystemReportDailyJob));
        var other = new ScheduledJobDescriptor("Other", "其他", string.Empty, "0 3 * * *", typeof(SystemReportDailyJob));

        Assert.Equal("15 6 * * *", ScheduleCalculator.GetEffectiveCron(daily, settings, [provider]));
        Assert.Equal("0 3 * * *", ScheduleCalculator.GetEffectiveCron(other, settings, [provider]));
    }

    [Fact]
    public void Validator_RejectsOverridingAProviderJob_AndIgnoresRetiredJobs()
    {
        var provider = new SystemReportScheduleProvider(new StaticOptionsMonitor<SystemReportSettings>(new SystemReportSettings()));
        var descriptors = new[] { new ScheduledJobDescriptor(SystemReportDailyJob.JobName, "日報", string.Empty, "30 7 * * *", typeof(SystemReportDailyJob)) };
        var validator = new ScheduledJobSettingsValidator(descriptors, [provider]);

        var overridden = new ScheduledJobSettings();
        overridden.Jobs[SystemReportDailyJob.JobName] = new ScheduledJobOverride { Cron = "0 1 * * *" };
        var retired = new ScheduledJobSettings();
        retired.Jobs["AdminPatrol"] = new ScheduledJobOverride { Cron = "30 7 * * *" };

        var result = validator.Validate(null, overridden);
        Assert.True(result.Failed);
        Assert.Contains("系統參數", result.FailureMessage, StringComparison.Ordinal);
        Assert.True(validator.Validate(null, retired).Succeeded);
    }

    [Fact]
    public void Settings_LooserThresholdsNeverUndercutStricterOnes()
    {
        var settings = new SystemReportSettings { CapacityRedFreePercent = 20, CapacityOrangeFreePercent = 10, CapacityRedForecastDays = 40, CapacityOrangeForecastDays = 30, CapacityYellowForecastDays = 20 };

        Assert.Empty(Validate(settings));
        Assert.Equal((20, 40, 40), (settings.EffectiveOrangeFreePercent, settings.EffectiveOrangeForecastDays, settings.EffectiveYellowForecastDays));
        Assert.Equal((15, 30, 90), (new SystemReportSettings().EffectiveOrangeFreePercent, new SystemReportSettings().EffectiveOrangeForecastDays, new SystemReportSettings().EffectiveYellowForecastDays));
    }

    [Fact]
    public void DailyPeriod_IsTheWholeDay()
    {
        var period = SystemReportDailyJob.DailyPeriod(new DateTime(2026, 11, 19, 15, 0, 0));

        Assert.Equal((new DateTime(2026, 11, 19), new DateTime(2026, 11, 20)), (period.Start, period.End));
        Assert.Equal("2026/11/19", period.Display);
        Assert.Equal(1, period.Days);
    }

    [Fact]
    public void JobArguments_RoundTrip_AndGarbageIsIgnored()
    {
        var arguments = new SystemReportJobArguments(new DateTime(2026, 11, 18), SendToAdmins: true);

        Assert.Equal(arguments, SystemReportJobArguments.TryParse(arguments.ToJson()));
        Assert.Null(SystemReportJobArguments.TryParse("not json"));
        Assert.Null(SystemReportJobArguments.TryParse(null));
    }

    [Fact]
    public void VolumeResolver_FindsTheDisk_EvenForAFolderThatDoesNotExistYet()
    {
        var temp = Path.GetTempPath();
        var missing = Path.Combine(temp, "MyProjectTests", Guid.NewGuid().ToString("N"), "not", "yet");

        var existing = VolumeResolver.Resolve(temp);
        var resolved = VolumeResolver.Resolve(missing);

        Assert.NotNull(existing);
        Assert.True(existing!.TotalBytes > 0 && existing.FreeBytes >= 0);
        Assert.Equal(existing.Root, resolved!.Root);
        Assert.Null(VolumeResolver.Resolve(null));
        Assert.Null(VolumeResolver.Resolve("   "));
        var nearest = VolumeResolver.NearestExisting(Path.GetFullPath(missing))!;
        Assert.True(Directory.Exists(nearest));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(temp), nearest, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(DateTime Date, long UsedBytes)> Days(int count, Func<int, long> used)
        => Enumerable.Range(0, count).Select(day => (Today.AddDays(day - count + 1), used(day)));

    private static List<ValidationResult> Validate(SystemReportSettings settings)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results;
    }
}
