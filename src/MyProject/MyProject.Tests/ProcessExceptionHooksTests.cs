using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 程序層級的最後一道網（LOG-04）。直接呼叫事件處理器，不靠 GC 觸發 UnobservedTaskException —— 後者時機不定，測試會不穩。
/// </summary>
public sealed class ProcessExceptionHooksTests : IDisposable
{
    private readonly string exceptionPath = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly ExceptionContextAccessor accessor = new();
    private readonly CapturingLogger logger;
    private readonly ProcessExceptionHooks hooks;

    public ProcessExceptionHooksTests()
    {
        logger = new CapturingLogger(accessor);
        var settings = new SystemSettings();
        settings.ExternalFileSystem.ExceptionPath = exceptionPath;
        hooks = new ProcessExceptionHooks(logger, accessor, Options.Create(settings));
    }

    [Fact]
    public void UnobservedTaskException_ShouldLogErrorWithProcessSourceAndMarkObserved()
    {
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("fire and forget")));

        hooks.OnUnobservedTaskException(null, args);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(ExceptionSources.Process, entry.Source);
        Assert.False(entry.Suppressed);
        Assert.True(args.Observed);
    }

    /// <summary>
    /// 0.9.172 起（瀏覽器人工驗收時發現）：使用者關掉分頁時，還在路上的 JS 呼叫會變成一批沒人等的
    /// JSDisconnectedException（實測每次 13～48 筆）。連線斷了本來就呼叫不到，是預期情況：只記 Debug，
    /// 不進系統例外、不計入暴增告警；但同一批裡只要混了別的例外就照常記 Error。
    /// </summary>
    [Fact]
    public void UnobservedTaskException_WithOnlyCircuitDisconnects_ShouldBeLoggedAsDebug()
    {
        var disconnected = new UnobservedTaskExceptionEventArgs(new AggregateException(
            new Microsoft.JSInterop.JSDisconnectedException("circuit gone"),
            new AggregateException(new Microsoft.JSInterop.JSDisconnectedException("circuit gone"))));
        var mixed = new UnobservedTaskExceptionEventArgs(new AggregateException(
            new Microsoft.JSInterop.JSDisconnectedException("circuit gone"), new InvalidOperationException("real bug")));

        hooks.OnUnobservedTaskException(null, disconnected);
        hooks.OnUnobservedTaskException(null, mixed);

        Assert.Equal([LogLevel.Debug, LogLevel.Error], logger.Entries.Select(x => x.Level).ToList());
        Assert.True(disconnected.Observed);
        Assert.True(mixed.Observed);
    }

    /// <summary>判斷偏保守（審查建議補上）：沒有內層例外、真正的 JS 錯誤、被包在別種例外裡的，都不算連線中斷。</summary>
    [Fact]
    public void CircuitDisconnect_ShouldOnlyMatchPureDisconnects()
    {
        Assert.True(CircuitDisconnect.IsOnlyDisconnect(new Microsoft.JSInterop.JSDisconnectedException("gone")));
        Assert.False(CircuitDisconnect.IsOnlyDisconnect(new AggregateException()));
        Assert.False(CircuitDisconnect.IsOnlyDisconnect(new Microsoft.JSInterop.JSException("real js error")));
        Assert.False(CircuitDisconnect.IsOnlyDisconnect(new InvalidOperationException("wrapped", new Microsoft.JSInterop.JSDisconnectedException("gone"))));
    }

    [Fact]
    public void UnobservedTaskException_ShouldRestorePreviousContext()
    {
        // 這個事件在終結器執行緒上觸發；不還原情境會污染同一條執行緒之後的工作。
        var previous = new ExceptionContext(ExceptionSources.Ui, "/projects", "support", 1);
        accessor.Set(previous);

        hooks.OnUnobservedTaskException(null, new UnobservedTaskExceptionEventArgs(new AggregateException()));

        Assert.Equal(previous, accessor.Current);
    }

    [Fact]
    public void UnhandledException_ShouldWriteCrashMarkerAndLogWhileSuppressed()
    {
        // 程序即將結束：寫補登檔讓下次啟動補進例外紀錄；日誌照寫但抑制收錄，避免同一次發生變兩列。
        hooks.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("thread crash"), isTerminating: true));

        var marker = Assert.Single(CrashMarkerStore.ReadAll(exceptionPath)).Entry;
        Assert.Equal(ExceptionSources.Process, marker.Source);
        Assert.Equal("thread crash", marker.Message);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.True(entry.Suppressed);
        Assert.False(accessor.IsSuppressed);
    }

    public void Dispose()
    {
        accessor.Clear();
        if (Directory.Exists(exceptionPath))
        {
            Directory.Delete(exceptionPath, recursive: true);
        }
    }

    /// <summary>記下每次呼叫當下的等級、例外來源情境與抑制旗標。</summary>
    private sealed class CapturingLogger(ExceptionContextAccessor accessor) : ILogger<ProcessExceptionHooks>
    {
        public List<(LogLevel Level, string? Source, bool Suppressed)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, accessor.Current?.Source, accessor.IsSuppressed));
    }
}
