using System.Collections.Concurrent;
using AntDesign;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Dtos.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// <see cref="DestructiveAction.RunAsync"/>（0.10.45 起，整併四個日誌頁的「批次清除／清空全部」）：
/// 取消就不呼叫伺服器；成功顯示指定的那一句並回 true（呼叫端重新讀取）；失敗顯示伺服器的原因並回 false —— 與整併前各頁相同。
///
/// 放進 <see cref="IntegrationHostCollection"/>：<see cref="ViewNotification"/> 的 logger 是行程層級的靜態欄位（同 <see cref="ViewNotificationTests"/>）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class DestructiveActionTests : ComponentTestBase
{
    public DestructiveActionTests()
    {
        ToastCopyButtonTests.AddCopyServices(Services);
    }

    [Fact]
    public async Task Cancel_ShouldNotCallTheServer()
    {
        var container = Render<AntContainer>();
        var calls = 0;

        var pending = Run(() =>
        {
            calls++;
            return Task.FromResult(ApiResult<object>.SuccessResult(new object(), "已清除 3 筆紀錄。"));
        });
        container.WaitForElement(".ant-modal-confirm .ant-btn:not(.ant-btn-dangerous)").Click();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Confirmed_Success_ShouldShowTheSuccessMessage()
    {
        var container = Render<AntContainer>();
        var factory = new CapturingLoggerFactory();
        ViewNotification.UseLoggerFactory(factory);
        try
        {
            var pending = Run(() => Task.FromResult(ApiResult<object>.SuccessResult(new object(), "已清除 3 筆紀錄。")));
            var ok = container.WaitForElement(".ant-modal-confirm .ant-btn-dangerous");
            Assert.Contains("清除", ok.TextContent, StringComparison.Ordinal);
            Assert.Contains("將刪除所有紀錄", container.Markup, StringComparison.Ordinal);
            ok.Click();

            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            container.WaitForAssertion(() => Assert.Contains("已清除 3 筆紀錄。", container.Markup, StringComparison.Ordinal));

            // 成功走 Warning（成功訊息的樣式，不寫日誌）；錯誤與提醒都會寫日誌，所以沒有日誌＝不是那兩種。
            Assert.Empty(factory.Entries);
        }
        finally
        {
            ViewNotification.UseLoggerFactory(NullLoggerFactory.Instance);
        }
    }

    [Fact]
    public async Task Confirmed_Failure_ShouldShowTheServerReason()
    {
        var container = Render<AntContainer>();
        var factory = new CapturingLoggerFactory();
        ViewNotification.UseLoggerFactory(factory);
        try
        {
            var pending = Run(() => Task.FromResult(ApiResult<object>.FailureResult("備份正在執行，請稍後再清除。", 409)));
            container.WaitForElement(".ant-modal-confirm .ant-btn-dangerous").Click();

            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains((LogLevel.Warning, "備份正在執行，請稍後再清除。"), factory.Entries);
            Assert.Equal(["Error"], factory.Kinds.ToArray()); // 錯誤通知（不是提醒）
        }
        finally
        {
            ViewNotification.UseLoggerFactory(NullLoggerFactory.Instance);
        }
    }

    [Fact]
    public void ServerMessageOr_ShouldFallBackWhenTheServerSaidNothing()
    {
        Assert.Equal("已清除 3 筆紀錄。", DestructiveAction.ServerMessageOr(ApiResult<object>.SuccessResult(new object(), "已清除 3 筆紀錄。"), "清除完成"));
        Assert.Equal("清除完成", DestructiveAction.ServerMessageOr(ApiResult<object>.SuccessResult(new object(), " "), "清除完成"));
    }

    private Task<bool> Run(Func<Task<ApiResult<object>>> call)
        => DestructiveAction.RunAsync(
            Services.GetRequiredService<ModalService>(),
            Services.GetRequiredService<NotificationService>(),
            "清空全部紀錄",
            "將刪除所有紀錄。此動作無法復原。",
            "清除",
            call,
            result => result.Message);

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Entries { get; } = new();

        /// <summary>通知的種類（<c>NotificationType</c>：Error／Warning…）。提醒（Caution）與錯誤（Error）的日誌等級都是 Warning，要靠它分辨。</summary>
        public ConcurrentQueue<string> Kinds { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(pair => pair.Key == "NotificationText").Value as string
                : null;
            Entries.Enqueue((logLevel, text ?? formatter(state, exception)));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs && pairs.FirstOrDefault(pair => pair.Key == "NotificationKind").Value is { } kind)
            {
                Kinds.Enqueue(kind.ToString()!);
            }
        }
    }
}
