using MyProject.Business.Helpers;

namespace MyProject.Tests;

/// <summary>
/// UTC 與伺服器本地時間的換算（0.10.44 起整併成 <see cref="LocalTime"/>）。
/// 除了基本行為，逐一和整併前各處的寫法比對：不同時區、整年每天兩個時刻（含日光節約時間切換前後），結果必須完全相同（含 Kind）。
/// </summary>
public sealed class LocalTimeTests
{
    private static readonly TimeZoneInfo Taipei = Zone(TimeSpan.FromHours(8), dst: false);
    private static readonly TimeZoneInfo WithDst = Zone(TimeSpan.FromHours(-5), dst: true);

    [Fact]
    public void ToLocal_TreatsAnyKindAsUtc()
    {
        var provider = new ZoneTimeProvider(Taipei);
        var expected = new DateTime(2026, 11, 26, 9, 30, 0);

        Assert.Equal(expected, provider.ToLocal(new DateTime(2026, 11, 26, 1, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(expected, provider.ToLocal(new DateTime(2026, 11, 26, 1, 30, 0, DateTimeKind.Unspecified)));
        Assert.Equal(new DateTime(2026, 11, 26, 1, 30, 0), provider.ToUtc(expected));
        Assert.Equal(DateTimeKind.Utc, provider.ToUtc(expected).Kind);
    }

    [Fact]
    public void Unzoned_ShouldDropTheKind()
    {
        var provider = new ZoneTimeProvider(Taipei);

        Assert.Equal(DateTimeKind.Unspecified, provider.ToLocalUnzoned(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Kind);
        Assert.Equal(new DateTime(2026, 1, 1, 8, 0, 0), provider.ToLocalUnzoned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(DateTimeKind.Unspecified, LocalTime.Unzoned(DateTime.Now).Kind);
        Assert.Null(LocalTime.Unzoned((DateTime?)null));
        Assert.Null(provider.ToLocalUnzoned((DateTime?)null));
    }

    /// <summary>整併前各處的寫法，與新的寫法逐一比對（Value 與 Kind 都相同）。</summary>
    [Theory]
    [InlineData("Taipei")]
    [InlineData("WithDst")]
    [InlineData("System")]
    public void ShouldMatchThePreviousImplementations(string zoneName)
    {
        var zone = zoneName switch { "Taipei" => Taipei, "WithDst" => WithDst, _ => TimeZoneInfo.Local };
        TimeProvider provider = zoneName == "System" ? TimeProvider.System : new ZoneTimeProvider(zone);

        for (var day = new DateTime(2026, 1, 1); day.Year == 2026; day = day.AddDays(1))
        {
            foreach (var hour in new[] { 1, 15 })
            {
                var utc = DateTime.SpecifyKind(day.AddHours(hour), DateTimeKind.Unspecified);

                // AnnouncementService／EmailOutboxQueryService／PromptTemplateService 的 ToLocal
                AssertSame(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), provider.ToLocal(utc));

                // DashboardController／NotificationsController／ProfileController 的 ToLocal（不帶時差）
                AssertSame(
                    DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), DateTimeKind.Unspecified),
                    provider.ToLocalUnzoned(utc));

                // LogLevelController／SystemHealthController（DateTimeOffset）
                var offset = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
                AssertSame(
                    DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(offset, zone).DateTime, DateTimeKind.Unspecified),
                    provider.ToLocalUnzoned(offset));

                // ScheduledJobOverviewService.ToLocal（ScheduleCalculator.AsUtc）
                var asUtc = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                AssertSame(TimeZoneInfo.ConvertTimeFromUtc(asUtc, zone), provider.ToLocal(utc));

                // AnnouncementService／EmailOutboxQueryService 的 ToUtc（本地 → UTC；日光節約時間跳過的那一小時兩邊都會丟例外，略過）
                var local = utc;
                if (zone.IsInvalidTime(local) == false)
                {
                    AssertSame(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone), provider.ToUtc(local));
                }

                if (zoneName == "System")
                {
                    // AuditLogQueryService.ToLocal 與 SystemParametersController 原本用機器時區的 ToLocalTime()
                    AssertSame(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(), AuditLogQueryServiceToLocal(utc));
                    AssertSame(
                        DateTime.SpecifyKind(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(), DateTimeKind.Unspecified),
                        TimeProvider.System.ToLocalUnzoned(utc));
                }
            }
        }
    }

    private static DateTime AuditLogQueryServiceToLocal(DateTime utc)
        => MyProject.Business.Services.DataAccess.AuditLogQueryService.ToLocal(utc);

    private static void AssertSame(DateTime expected, DateTime actual)
    {
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Kind, actual.Kind);
    }

    private static TimeZoneInfo Zone(TimeSpan offset, bool dst)
    {
        if (dst == false)
        {
            return TimeZoneInfo.CreateCustomTimeZone($"Test{offset.Hours}", offset, "Test", "Test");
        }

        // 三月第二個星期日 02:00 進入、十一月第一個星期日 02:00 離開（同美東）。
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("TestDst", offset, "TestDst", "TestStd", "TestDst", [rule]);
    }

    private sealed class ZoneTimeProvider(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
