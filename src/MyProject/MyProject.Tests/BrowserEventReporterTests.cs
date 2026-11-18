using Microsoft.Extensions.Logging;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Shell;
using MyProject.Models.Others;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 瀏覽器端的非錯誤事件（0.10.36 起）：連線中斷、資源載入失敗、內容安全政策。只寫日誌檔、不帶例外物件
/// （系統例外紀錄只收「Error 以上且帶例外物件」），前端送來的路徑與網址一律去掉查詢字串。
/// </summary>
public sealed class BrowserEventReporterTests
{
    private readonly CapturingLogger logger = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 7, Account = "alice" } };

    [Fact]
    public void Network_WhileTheBrowserWasOnline_ShouldBeAWarning_WithoutAnException()
    {
        var reporter = CreateReporter();

        Assert.True(reporter.Report(new ClientReportDto
        {
            Kind = "network",
            BrowserOnline = true,
            FailedCalls = 4,
            StartedAt = new DateTimeOffset(2026, 11, 18, 2, 0, 0, TimeSpan.Zero),
            RecoveredAt = new DateTimeOffset(2026, 11, 18, 2, 3, 0, TimeSpan.Zero),
            Paths = ["/api/v1/project/search?keyword=王小明", "/api/v1/project/search", "/api/v1/shell/info#x"],
        }));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.StartsWith("Browser could not reach the server.", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Paths=/api/v1/project/search, /api/v1/shell/info,", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("王小明", entry.Message, StringComparison.Ordinal);
        Assert.Contains("FailedCalls=4", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Account=alice", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_WhileTheBrowserWasOffline_ShouldBeInformation()
    {
        var reporter = CreateReporter();

        reporter.Report(new ClientReportDto { Kind = "network", BrowserOnline = false, FailedCalls = 1 });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith("Browser was offline.", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Network_ShouldKeepAtMostTenDistinctPaths()
    {
        var reporter = CreateReporter();

        reporter.Report(new ClientReportDto { Kind = "network", BrowserOnline = true, Paths = Enumerable.Range(0, 30).Select(i => $"/api/v1/x{i}").ToList() });

        Assert.Equal(BrowserEventReporter.MaxPaths, Assert.Single(logger.Entries).Message.Split("/api/v1/x").Length - 1);
    }

    [Theory]
    [InlineData("resource", "Browser failed to load a resource.")]
    [InlineData("csp", "Browser blocked content by the security policy.")]
    public void ResourceAndCsp_ShouldBeWarnings_WithoutQueryStrings(string kind, string prefix)
    {
        var reporter = CreateReporter();

        reporter.Report(new ClientReportDto { Kind = kind, Message = "img", Source = "https://cdn.example.com/a.png?token=secret", Path = "/projects?id=1" });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.StartsWith(prefix, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Page=/projects,", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorKinds_ShouldNotBeHandledHere()
    {
        Assert.False(BrowserEventReporter.Handles("error"));
        Assert.False(CreateReporter().Report(new ClientReportDto { Kind = "caught", Message = "boom" }));
        Assert.Empty(logger.Entries);
    }

    private BrowserEventReporter CreateReporter() => new(logger, currentUser, TimeProvider.System);

    private sealed class CapturingLogger : ILogger<BrowserEventReporter>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
