using MyProject.Models.Systems;
using MyProject.Web.SystemReports;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理指定區間巡檢（0.10.42 起，M4）的區間解析：模型不知道今天幾號，使用者說的日期原樣傳進來，年份由程式推。
/// 產生、存檔、稽核與不寫 AI 說明見 <see cref="SystemReportsApiTests"/>。
/// </summary>
public sealed class SystemReportAssistantServiceTests
{
    private static readonly DateTime Today = new(2026, 11, 23);

    private static SystemReportRangeResult Resolve(string? start, string? end, DateTime? today = null, int retention = 90)
        => SystemReportAssistantService.ResolveRange(start, end, today ?? Today, retention);

    [Fact]
    public void NoDates_IsYesterdayToNow()
    {
        var period = Resolve(null, " ").Period!;

        Assert.Equal((SystemReportFrequencies.Custom, new DateTime(2026, 11, 22), new DateTime(2026, 11, 24)), (period.Frequency, period.Start, period.End));
        Assert.Equal("2026/11/22～2026/11/23", period.Display);
        Assert.False(period.IsPeriodic);
    }

    [Theory]
    [InlineData("2026-11-01", "2026-11-15", "2026-11-01", "2026-11-15")]
    [InlineData("2026/11/1", "2026/11/15", "2026-11-01", "2026-11-15")]
    [InlineData("11/1", "11/15", "2026-11-01", "2026-11-15")]
    [InlineData("11-01", "11-15", "2026-11-01", "2026-11-15")]
    [InlineData("11/15", "11/1", "2026-11-01", "2026-11-15")] // 起大於迄就對調
    [InlineData("11/20", null, "2026-11-20", "2026-11-20")] // 只給一天
    [InlineData(null, "11/20", "2026-11-20", "2026-11-20")]
    [InlineData("12/28", "1/3", "2025-12-28", "2026-01-03")] // 沒有年份：不晚於今天的最近那一年
    public void Dates_AreParsed_AndTheYearIsInferred(string? start, string? end, string expectedStart, string expectedEnd)
    {
        var period = Resolve(start, end, retention: 3650).Period!;

        Assert.Equal((DateTime.Parse(expectedStart), DateTime.Parse(expectedEnd).AddDays(1)), (period.Start, period.End));
    }

    [Fact]
    public void ACrossYearRange_InJanuary_UsesLastDecember()
    {
        var period = Resolve("12/28", "1/3", today: new DateTime(2027, 1, 5)).Period!;

        Assert.Equal((new DateTime(2026, 12, 28), new DateTime(2027, 1, 4)), (period.Start, period.End));
    }

    [Fact]
    public void LeapDay_FindsTheLastLeapYear()
        => Assert.Equal(new DateTime(2024, 2, 29), Resolve("2/29", null, retention: 3650).Period!.Start);

    [Theory]
    [InlineData("2026-11-30", null, "晚於今天")]
    [InlineData("10/1", "11/20", "最多產生 31 天")]
    [InlineData("2026-07-01", null, "最近 90 天內")]
    [InlineData("上週", null, "看不懂日期「上週」")]
    [InlineData("13/1", null, "看不懂日期")]
    [InlineData("2/30", null, "看不懂日期")]
    public void InvalidRanges_ReturnAnErrorForTheModel(string? start, string? end, string expected)
    {
        var result = Resolve(start, end);

        Assert.Null(result.Period);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirtyOneDays_IsAllowed()
        => Assert.NotNull(Resolve("10/24", "11/23").Period);
}
