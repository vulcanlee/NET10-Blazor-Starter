using MyProject.Dtos.Shell;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 瀏覽器端錯誤的背景資訊（0.10.36 起）：伺服器把前端送來的結構化欄位組成文字，附在堆疊後面。
/// 前端送來的一律不信任：去掉查詢字串、換行（避免偽造日誌行），只留允許的方法與格式正確的追蹤碼。
/// </summary>
public sealed class BrowserReportContextTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.CreateCustomTimeZone("Test+8", TimeSpan.FromHours(8), "Test+8", "Test+8");

    [Fact]
    public void Format_ShouldListTheContext_InServerLocalTime()
    {
        var text = BrowserReportContext.Format(new ClientReportDto
        {
            TraceCode = "ABCD2345",
            OccurredAt = new DateTimeOffset(2026, 11, 18, 2, 0, 1, 234, TimeSpan.Zero),
            AppBuild = "1a2b3c4d",
            UserAgent = "Mozilla/5.0 Chrome/140",
            Breadcrumbs =
            [
                new ClientBreadcrumbDto { At = new DateTimeOffset(2026, 11, 18, 2, 0, 0, TimeSpan.Zero), Kind = "nav", Path = "/projects?id=5" },
                new ClientBreadcrumbDto { At = new DateTimeOffset(2026, 11, 18, 2, 0, 0, 500, TimeSpan.Zero), Kind = "api", Method = "post", Path = "/api/v1/project/search", Status = 200, ElapsedMs = 120, TraceCode = "WXYZ6789" },
                new ClientBreadcrumbDto { At = new DateTimeOffset(2026, 11, 18, 2, 0, 1, TimeSpan.Zero), Kind = "api", Method = "GET", Path = "/api/v1/shell/info", Status = 0 },
            ],
        }, beforeSignIn: false, systemVersion: "0.10.36 (2026/11/18)", Taipei);

        Assert.Contains("TraceCode: ABCD2345", text, StringComparison.Ordinal);
        Assert.Contains("OccurredAt: 2026-11-18 10:00:01.234 (browser clock)", text, StringComparison.Ordinal);
        Assert.Contains("SignedIn: yes", text, StringComparison.Ordinal);
        Assert.Contains("AppBuild: 1a2b3c4d", text, StringComparison.Ordinal);
        Assert.Contains("SystemVersion: 0.10.36 (2026/11/18)", text, StringComparison.Ordinal);
        Assert.Contains("UserAgent: Mozilla/5.0 Chrome/140", text, StringComparison.Ordinal);
        Assert.Contains("10:00:00.000 nav /projects" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("10:00:00.500 api POST /api/v1/project/search 200 120ms WXYZ6789", text, StringComparison.Ordinal);
        Assert.Contains("10:00:01.000 api GET /api/v1/shell/info unreachable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ShouldNotTrustTheBrowser()
    {
        var crumbs = Enumerable.Range(0, 30)
            .Select(i => new ClientBreadcrumbDto { At = DateTimeOffset.UnixEpoch, Kind = "nav", Path = $"/p{i}" })
            .Append(new ClientBreadcrumbDto { At = DateTimeOffset.UnixEpoch, Kind = "api", Method = "TRACE\nFAKE", Path = "/x\r\n2026-01-01|ERROR|forged", TraceCode = "nope" })
            .Append(new ClientBreadcrumbDto { At = DateTimeOffset.UnixEpoch, Kind = "script", Path = "/ignored" })
            .ToList();

        var text = BrowserReportContext.Format(new ClientReportDto
        {
            TraceCode = "bad",
            AppBuild = "build\nERROR forged line",
            UserAgent = new string('u', 1000),
            Breadcrumbs = crumbs,
        }, beforeSignIn: true, systemVersion: "0.10.36", TimeZoneInfo.Utc);

        Assert.DoesNotContain("TraceCode:", text, StringComparison.Ordinal);
        Assert.Contains("SignedIn: no (reported after sign-in)", text, StringComparison.Ordinal);
        Assert.Contains("AppBuild: buildERROR forged line", text, StringComparison.Ordinal);   // 換行被拿掉，不會變成新的日誌行
        Assert.Contains("UserAgent: " + new string('u', BrowserReportContext.MaxUserAgentLength) + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("api ? /x2026-01-01|ERROR|forged ?", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/ignored", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" nav /p11" + Environment.NewLine, text, StringComparison.Ordinal);   // 只留最後 20 筆
        Assert.Contains(" nav /p12" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.All(text.Split(Environment.NewLine), line => Assert.DoesNotContain('\n', line));
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/logs?keyword=abc", "/logs")]
    [InlineData("/a#b", "/a")]
    public void StripQuery_ShouldKeepOnlyThePath(string? input, string expected)
        => Assert.Equal(expected, BrowserReportContext.StripQuery(input));
}
