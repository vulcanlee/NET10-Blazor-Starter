using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Shell;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>瀏覽器端錯誤回報（LOG-20）。前端送來的內容一律不信任。</summary>
public sealed class BrowserErrorReporterTests : IDisposable
{
    private readonly ExceptionContextAccessor accessor = new();
    private readonly CapturingLogger logger;
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 7, Account = "alice" } };

    public BrowserErrorReporterTests()
    {
        logger = new CapturingLogger(accessor);
    }

    [Fact]
    public void Report_ShouldLogErrorWithBrowserSourceAndUser()
    {
        var reporter = CreateReporter();

        Assert.True(reporter.Report("error", "x is not defined", "at foo (app.js:1:2)", "https://site/app.js:1:2", "/projects?token=secret#top"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        var exception = Assert.IsType<BrowserScriptException>(entry.Exception);
        Assert.Equal("x is not defined", exception.Message);
        Assert.Contains("at foo (app.js:1:2)", exception.ToString());
        Assert.Equal(ExceptionSources.Browser, entry.Context?.Source);
        Assert.Equal("/projects", entry.Context?.Page);   // 查詢字串與片段可能含 token，不得進紀錄
        Assert.Equal("alice", entry.Context?.Account);
        Assert.Equal(7, entry.Context?.UserId);
        Assert.Null(accessor.Current);                      // 情境用完要還原
    }

    [Fact]
    public void Report_WhenDisabled_ShouldIgnore()
    {
        var reporter = CreateReporter(new ClientErrorReportingSettings { Enabled = false });

        Assert.False(reporter.Report("error", "boom", null, null, "/"));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Report_ShouldTruncateOversizedInput()
    {
        var reporter = CreateReporter();

        reporter.Report("weird-kind", new string('m', 5000), new string('s', 9000), null, "/" + new string('p', 500));

        var entry = Assert.Single(logger.Entries);
        var exception = Assert.IsType<BrowserScriptException>(entry.Exception);
        Assert.Equal(1000, exception.Message.Length);
        Assert.Equal(4000, exception.ScriptStack.Length);
        Assert.Equal("error", exception.Kind);              // 不認得的種類一律當 error
        Assert.Equal(200, entry.Context!.Page!.Length);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("projects", "/projects")]
    [InlineData("/logs?keyword=abc", "/logs")]
    [InlineData("/a#b", "/a")]
    public void NormalizePath_ShouldKeepOnlyThePath(string? input, string expected)
    {
        Assert.Equal(expected, BrowserErrorReporter.NormalizePath(input));
    }

    // ---------- 0.10.36 起的批次回報 ----------

    /// <summary>瀏覽器產生的追蹤碼（畫面上已經顯示給使用者）就是日誌檔與例外紀錄的追蹤碼。</summary>
    [Fact]
    public void ReportItem_ShouldUseTheBrowserTraceCode_AndAppendTheContextOutsideTheMessage()
    {
        var reporter = CreateReporter();

        Assert.True(reporter.Report(new ClientReportDto { Kind = "caught", Message = "Object reference not set", Stack = "at Page.Save()", Path = "/projects", TraceCode = "ABCD2345" },
            beforeSignIn: false, context: "Browser context:\n  AppBuild: 1a2b3c4d"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("ABCD2345", entry.TraceCode);
        Assert.Equal("Browser script error. Kind={Kind}", entry.Template);
        var exception = Assert.IsType<BrowserScriptException>(entry.Exception);
        Assert.Equal("caught", exception.Kind);
        Assert.Equal("Object reference not set", exception.Message);
        Assert.Contains("AppBuild: 1a2b3c4d", exception.ToString());
        Assert.Null(TraceCode.Current);   // 範圍用完要還原
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abcd1234")]       // 小寫不是追蹤碼的字元
    [InlineData("ABCD12345")]
    [InlineData("ABCDILOU")]       // I／L／O／U 不在字元集裡
    [InlineData("AB\nCD123")]
    public void ReportItem_WithAnInvalidTraceCode_ShouldNotUseIt(string? code)
    {
        var reporter = CreateReporter();

        reporter.Report(new ClientReportDto { Kind = "error", Message = "boom", TraceCode = code }, beforeSignIn: false, context: null);

        Assert.Null(Assert.Single(logger.Entries).TraceCode);
    }

    /// <summary>登入前發生的錯誤用另一個樣板：例外紀錄的「操作」欄看得出來，也與登入後的同一個錯誤分開計次。</summary>
    [Fact]
    public void ReportItem_BeforeSignIn_ShouldUseItsOwnTemplate_AndUnknownKindsBecomeError()
    {
        var reporter = CreateReporter();

        reporter.Report(new ClientReportDto { Kind = "network", Message = "boom" }, beforeSignIn: true, context: null);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("Browser script error before sign-in. Kind={Kind}", entry.Template);
        Assert.Equal("error", Assert.IsType<BrowserScriptException>(entry.Exception).Kind);
    }

    /// <summary>背景資訊（前端指紋、瀏覽器、操作軌跡）不在訊息裡，所以只差這些的同一個錯誤會合併成一筆。</summary>
    [Fact]
    public void ReportItem_DifferingOnlyInContext_ShouldShareTheSignature()
    {
        var reporter = CreateReporter();

        reporter.Report(new ClientReportDto { Kind = "error", Message = "boom", Path = "/a" }, beforeSignIn: false, context: "AppBuild: 11111111\nUserAgent: Chrome");
        reporter.Report(new ClientReportDto { Kind = "error", Message = "boom", Path = "/a" }, beforeSignIn: false, context: "AppBuild: 22222222\nUserAgent: Firefox");

        var signatures = logger.Entries
            .Select(x => ExceptionSignature.Compute(x.Exception!.GetType().FullName!, x.Exception.Message, x.Context?.Page, x.Template))
            .Distinct()
            .ToList();
        Assert.Single(signatures);
    }

    [Fact]
    public void ReportItem_WhenDisabled_ShouldIgnore()
    {
        var reporter = CreateReporter(new ClientErrorReportingSettings { Enabled = false });

        Assert.False(reporter.Report(new ClientReportDto { Kind = "error", Message = "boom" }, beforeSignIn: false, context: null));
        Assert.Empty(logger.Entries);
    }

    private BrowserErrorReporter CreateReporter(ClientErrorReportingSettings? settings = null)
        => new(logger, accessor, currentUser, new StaticMonitor(settings ?? new ClientErrorReportingSettings()));

    public void Dispose() => accessor.Clear();

    private sealed class CapturingLogger(ExceptionContextAccessor accessor) : ILogger<BrowserErrorReporter>
    {
        public List<(LogLevel Level, Exception? Exception, ExceptionContext? Context, string? TraceCode, string? Template)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, accessor.Current, TraceCode.Current,
                (state as IReadOnlyList<KeyValuePair<string, object?>>)?.FirstOrDefault(x => x.Key == "{OriginalFormat}").Value as string));
    }

    private sealed class StaticMonitor(ClientErrorReportingSettings value) : IOptionsMonitor<ClientErrorReportingSettings>
    {
        public ClientErrorReportingSettings CurrentValue { get; } = value;

        public ClientErrorReportingSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ClientErrorReportingSettings, string?> listener) => null;
    }
}
