using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Shell;
using MyProject.Models.Others;
using MyProject.Web.Client.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 前端診斷記錄（0.10.37 起）：管理員對某位使用者開啟、限時、只存記憶體；那位使用者的前端日誌依原等級寫進日誌檔（<c>MyProject.Browser.*</c>），
/// 不帶例外物件（不進系統例外紀錄）。前端只在開啟時收集本專案的日誌、回報管線自己的日誌不收。
/// </summary>
public sealed class ClientDiagnosticsTests
{
    // ---------- 伺服器：對象 ----------

    [Fact]
    public void Registry_ShouldExpire_Replace_AndDisable()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 18, 2, 0, 0, TimeSpan.Zero));
        var registry = new ClientDiagnosticsRegistry(clock, NullLogger<ClientDiagnosticsRegistry>.Instance);

        registry.Enable(7, "alice", LogLevel.Debug, TimeSpan.FromMinutes(15), "admin");
        registry.Enable(8, "bob", LogLevel.Trace, TimeSpan.FromMinutes(60), "admin");
        Assert.Equal([7, 8], registry.List().Select(x => x.UserId));

        registry.Enable(7, "alice", LogLevel.Trace, TimeSpan.FromMinutes(120), "admin2");   // 同一位使用者：取代
        Assert.Equal(LogLevel.Trace, registry.Find(7)!.Level);
        Assert.Equal([8, 7], registry.List().Select(x => x.UserId));

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Null(registry.Find(8));                       // 到期
        Assert.False(registry.Disable(8));
        Assert.True(registry.Disable(7));
        Assert.Empty(registry.List());
    }

    // ---------- 伺服器：轉送 ----------

    [Fact]
    public void Forwarder_ShouldWriteAtTheOriginalLevel_UnderTheBrowserCategory_WithoutAnException()
    {
        var factory = new CapturingLoggerFactory();
        var forwarder = new BrowserLogForwarder(factory, new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "alice" } },
            TimeProvider.System, NullLogger<BrowserLogForwarder>.Instance);

        var written = forwarder.Forward(new ClientLogBatchDto
        {
            Items =
            [
                new ClientLogEntryDto { Level = "Trace", Category = "MyProject.Web.Client.Api.ApiClient", Message = "too detailed" },
                new ClientLogEntryDto { Level = "Debug", Category = "MyProject.Web.Client.Api.ApiClient", Message = "list reloaded", Path = "/projects?id=5" },
                new ClientLogEntryDto { Level = "Error", Category = "Evil\nCategory", Message = "boom\n2026-01-01|FATAL|forged", ExceptionType = "System.InvalidOperationException: x" },
                new ClientLogEntryDto { Level = "Verbose", Category = "MyProject.X", Message = "unknown level" },
                new ClientLogEntryDto { Level = "Warning", Category = "MyProject.X", Message = new string('m', 5000) },
            ],
        }, LogLevel.Debug, maxItems: 200);

        Assert.Equal(3, written);
        Assert.Equal(
            [
                (LogLevel.Debug, "MyProject.Browser.Web.Client.Api.ApiClient"),
                (LogLevel.Error, "MyProject.Browser.Other"),
                (LogLevel.Warning, "MyProject.Browser.X"),
            ],
            factory.Entries.Select(x => (x.Level, x.Category)));
        Assert.All(factory.Entries, x => Assert.Null(x.Exception));
        Assert.Contains("Page=/projects,", factory.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("Account=alice", factory.Entries[0].Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', factory.Entries[1].Message);
        Assert.Contains("ExceptionType=System.InvalidOperationException: x", factory.Entries[1].Message, StringComparison.Ordinal);
        Assert.Contains(new string('m', BrowserLogForwarder.MaxMessageLength) + " (", factory.Entries[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forwarder_ShouldStopAtTheBatchLimit()
    {
        var factory = new CapturingLoggerFactory();
        var forwarder = new BrowserLogForwarder(factory, new CurrentUserService(), TimeProvider.System, NullLogger<BrowserLogForwarder>.Instance);

        var written = forwarder.Forward(new ClientLogBatchDto
        {
            Items = Enumerable.Range(0, 10).Select(i => new ClientLogEntryDto { Level = "Debug", Category = "MyProject.X", Message = $"m{i}" }).ToList(),
        }, LogLevel.Debug, maxItems: 4);

        Assert.Equal(4, written);
    }

    /// <summary>
    /// nlog.config：<c>MyProject.Browser.*</c> 的規則排在 <c>*</c> 之前、Trace 以上、final（不重複寫，也不受「日誌等級設定」影響）。
    /// </summary>
    [Fact]
    public void NLogConfig_ShouldHaveAFinalBrowserRule_BeforeTheApplicationRule()
    {
        var rules = XDocument.Load(SourceTree.WebFile("nlog.config")).Descendants().Where(x => x.Name.LocalName == "logger").ToList();
        var browser = rules.FindIndex(x => (string?)x.Attribute("name") == "MyProject.Browser.*");
        var application = rules.FindIndex(x => (string?)x.Attribute("name") == "*");

        Assert.True(browser >= 0 && browser < application, "MyProject.Browser.* 的規則必須排在 * 之前。");
        Assert.Equal("Trace", (string?)rules[browser].Attribute("minlevel"));
        Assert.Equal("true", (string?)rules[browser].Attribute("final"));
    }

    // ---------- 前端：狀態 ----------

    [Theory]
    [InlineData("Debug;15:30;3540", true, LogLevel.Debug, "15:30")]
    [InlineData("trace;09:05;60", true, LogLevel.Trace, "09:05")]
    [InlineData("Information;15:30;60", false, LogLevel.None, "")]   // 只接受 Debug、Trace
    [InlineData("Debug;15:30;0", false, LogLevel.None, "")]
    [InlineData("Debug;15:30", false, LogLevel.None, "")]
    [InlineData(null, false, LogLevel.None, "")]
    public void State_ShouldFollowTheHeader(string? header, bool active, LogLevel level, string until)
    {
        var state = new ClientDiagnosticsState();
        var changes = 0;
        state.Changed += () => changes++;

        state.Update(header);

        Assert.Equal(active, state.IsActive);
        Assert.Equal(active ? level : null, state.Level);
        Assert.Equal(until, state.Until);
        Assert.Equal(active ? 1 : 0, changes);

        state.Update(header);   // 沒變就不通知
        Assert.Equal(active ? 1 : 0, changes);
    }

    // ---------- 前端：日誌接收器 ----------

    [Fact]
    public async Task Provider_ShouldOnlyCollectOurLogs_AtTheThreshold_WhileActive_AndStopWhenTheServerSaysSo()
    {
        var state = new ClientDiagnosticsState();
        using var provider = new ClientDiagnosticsLoggerProvider(state);
        var sent = new List<ClientLogBatchDto>();
        var serverActive = true;
        provider.UseSender(batch =>
        {
            sent.Add(batch);
            return Task.FromResult<bool?>(serverActive);
        }, () => "/projects");
        var ours = provider.CreateLogger("MyProject.Web.Client.Views.Projects.ProjectView");
        var framework = provider.CreateLogger("Microsoft.AspNetCore.Components.RenderTree.Renderer");
        var pipeline = provider.CreateLogger(typeof(ClientErrorCapture).FullName!);

        ours.LogDebug("not active yet");
        Assert.Equal(0, provider.BufferedCount);

        state.Update("Debug;15:30;3600");
        ours.LogTrace("below the threshold");
        ours.LogDebug("Project list reloaded. Count={Count}", 3);
        ours.LogError(new InvalidOperationException("boom"), "Failed to save.");
        framework.LogWarning("framework");
        pipeline.LogWarning("pipeline");
        Assert.Equal(2, provider.BufferedCount);

        await provider.FlushAsync();

        var batch = Assert.Single(sent);
        Assert.Equal(["Debug", "Error"], batch.Items.Select(x => x.Level));
        Assert.Equal("Project list reloaded. Count=3", batch.Items[0].Message);
        Assert.Equal("/projects", batch.Items[0].Path);
        Assert.Equal("System.InvalidOperationException: boom", batch.Items[1].ExceptionType);

        serverActive = false;
        ours.LogDebug("one more");
        await provider.FlushAsync();
        Assert.False(state.IsActive);                       // 伺服器說已關閉：停止
        ours.LogDebug("after stop");
        Assert.Equal(0, provider.BufferedCount);
    }

    [Fact]
    public async Task Provider_ShouldCapTheBuffer_AndReportWhatWasDropped()
    {
        var state = new ClientDiagnosticsState();
        using var provider = new ClientDiagnosticsLoggerProvider(state);
        var sent = new List<ClientLogBatchDto>();
        state.Update("Trace;15:30;3600");
        var logger = provider.CreateLogger("MyProject.Web.Client.Test");

        // 還沒有送件者（還沒登入完成）：只暫存，最多 500 筆。
        for (var i = 0; i < ClientDiagnosticsLoggerProvider.MaxBuffered + 20; i++)
        {
            logger.LogTrace("m{Index}", i);
        }

        Assert.Equal(ClientDiagnosticsLoggerProvider.MaxBuffered, provider.BufferedCount);

        provider.UseSender(batch =>
        {
            sent.Add(batch);
            return Task.FromResult<bool?>(true);
        }, () => "/");
        await provider.FlushAsync();

        var batch = Assert.Single(sent);
        Assert.Equal(20, batch.Dropped);
        Assert.Equal(ClientDiagnosticsLoggerProvider.MaxPerBatch, batch.Items.Count);
        Assert.Equal("m20", batch.Items[0].Message);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Add((logLevel, category, formatter(state, exception), exception));
        }
    }
}
