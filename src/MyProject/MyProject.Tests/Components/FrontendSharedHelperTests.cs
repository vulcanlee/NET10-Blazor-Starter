using System.Globalization;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Client.Views.Admins;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 0.10.45 起整併的前端小工具：每個值都要和整併前各頁自己的寫法相同（期望值逐一抄自原本的 switch 與運算式）。
/// </summary>
public sealed class FrontendSharedHelperTests
{
    [Theory]
    [InlineData(JobRunStatuses.Running, "執行中", StatusTone.Accent)]
    [InlineData(JobRunStatuses.Succeeded, "成功", StatusTone.Positive)]
    [InlineData(JobRunStatuses.Failed, "失敗", StatusTone.Warning)]
    [InlineData(JobRunStatuses.Interrupted, "中斷", StatusTone.Warning)]
    [InlineData(JobRunStatuses.Skipped, "略過", StatusTone.Muted)]
    [InlineData("SomethingNew", "SomethingNew", StatusTone.Muted)]
    public void JobRunStatus_ShouldMatchTheFormerPageMappings(string status, string text, StatusTone tone)
    {
        Assert.Equal(text, JobRunText.Status(status));
        Assert.Equal(tone, JobRunText.Tone(status));
    }

    [Theory]
    [InlineData(JobRunTriggers.Schedule, "排程")]
    [InlineData(JobRunTriggers.CatchUp, "補跑")]
    [InlineData(JobRunTriggers.Manual, "手動")]
    [InlineData("Api", null)]
    [InlineData(null, null)]
    public void JobRunTrigger_UnknownCodes_ShouldBeLeftToThePage(string? trigger, string? text)
    {
        Assert.Equal(text, JobRunText.Trigger(trigger));
    }

    [Theory]
    [InlineData(100, "1")]
    [InlineData(125, "1.25")]
    [InlineData(150, "1.5")]
    public void AiFontScale_ShouldWriteAnInvariantCssNumber(int percent, string css)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // 小數點是逗號的文化：寫成 1,25 會讓 CSS 靜默失效。
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(css, AiFontScale.ToCss(percent));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AiFontScale_ShouldKeepTheThreeLevels()
    {
        Assert.Equal([(100, "一般"), (125, "中"), (150, "大")], AiFontScale.Options);
        Assert.Equal(100, AiFontScale.DefaultPercent);
    }

    [Fact]
    public void SafeHtmlWithinLimit_ShouldMatchTheFormerLengthCheck()
    {
        Assert.Equal(AiMarkdownRenderer.ToSafeHtml("# 標題"), AiMarkdownRenderer.ToSafeHtmlWithinLimit("# 標題"));
        Assert.Equal(string.Empty, AiMarkdownRenderer.ToSafeHtmlWithinLimit(null));

        // 原本各頁的判斷：html.Length > 512 KB 就不顯示。
        var huge = new string('a', AiMarkdownRenderer.MaxRenderedHtmlLength + 1);
        Assert.True(AiMarkdownRenderer.ToSafeHtml(huge).Length > 512 * 1024);
        Assert.Null(AiMarkdownRenderer.ToSafeHtmlWithinLimit(huge));
    }

    [Fact]
    public async Task ElapsedTicker_ShouldStopQuietlyWhenCanceled()
    {
        using var cts = new CancellationTokenSource();
        var ticks = 0;
        var ticker = AiStreaming.RunElapsedTickerAsync(() =>
        {
            ticks++;
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        await ticker.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, ticks);
        Assert.True(ticker.IsCompletedSuccessfully);
    }
}
