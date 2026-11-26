using System.Collections.Concurrent;
using AntDesign;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.Web.Client.Api;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 0.10.45 起整併的兩個頁面操作：<see cref="FileDownload.SaveDownloadAsync"/>（8 個頁面與 CRUD 樣板的下載）與
/// <see cref="ClipboardCopy.CopyTextAsync"/>（4 處複製）。每個分支顯示的通知與回傳值必須和整併前各頁的寫法相同。
///
/// 放進 <see cref="IntegrationHostCollection"/>：<see cref="ViewNotification"/> 的 logger 是行程層級的靜態欄位（同 <see cref="ViewNotificationTests"/>）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ViewActionHelperTests : ComponentTestBase
{
    private const string DownloadFunction = "appFileDownload.downloadFromStream";

    public ViewActionHelperTests()
    {
        ToastCopyButtonTests.AddCopyServices(Services);
    }

    [Fact]
    public async Task Rejected_ShouldShowTheLabelAndServerMessage_AndNotTouchTheBrowser()
    {
        var (result, shown) = await RunAsync(new ApiDownload(false, "沒有權限", null, null, null, 403));

        Assert.Equal(DownloadResult.Rejected, result);
        Assert.Equal([(LogLevel.Warning, "匯出失敗：沒有權限")], shown);
        Assert.Equal(["Error"], lastKinds);
        Assert.Empty(JSInterop.Invocations[DownloadFunction]);
    }

    [Fact]
    public async Task BadRequest_WithCaution_ShouldShowOnlyTheServerMessage()
    {
        var (result, shown) = await RunAsync(new ApiDownload(false, "超過 10,000 筆，請縮小範圍", null, null, null, 400), cautionOnBadRequest: true);

        Assert.False(result.Saved);
        Assert.Null(result.Error);
        Assert.Equal([(LogLevel.Warning, "超過 10,000 筆，請縮小範圍")], shown);
        Assert.Equal(["Warning"], lastKinds); // 提醒（Caution），不是錯誤
    }

    [Fact]
    public async Task BadRequest_WithoutCaution_ShouldStillUseTheLabel()
    {
        var (_, shown) = await RunAsync(new ApiDownload(false, "條件不正確", null, null, null, 400));

        Assert.Equal([(LogLevel.Warning, "匯出失敗：條件不正確")], shown);
        Assert.Equal(["Error"], lastKinds);
    }

    [Fact]
    public async Task Success_ShouldHandTheFileToTheBrowser_AndReportItsSize()
    {
        JSInterop.SetupVoid(DownloadFunction, _ => true).SetVoidResult();

        var (result, shown) = await RunAsync(new ApiDownload(true, "", [1, 2, 3], "a.xlsx", "application/x", 200));

        Assert.Equal(new DownloadResult(true, 3, null), result);
        Assert.Empty(shown);
        var call = Assert.Single(JSInterop.Invocations[DownloadFunction]);
        Assert.Equal("a.xlsx", call.Arguments[0]);
        Assert.Equal("application/x", call.Arguments[2]);
    }

    [Fact]
    public async Task BrowserFailure_ShouldShowAnUnexpectedError_AndReturnTheException()
    {
        var failure = new JSException("blocked");
        JSInterop.SetupVoid(DownloadFunction, _ => true).SetException(failure);

        var (result, shown) = await RunAsync(new ApiDownload(true, "", [1], "a.pdf", "application/pdf", 200));

        Assert.False(result.Saved);
        Assert.Same(failure, result.Error);
        Assert.Equal([(LogLevel.Error, "匯出失敗：JSException。")], shown);
        Assert.Equal(["Error"], lastKinds);
    }

    private const string CopyFunction = "appClipboard.copyText";

    [Fact]
    public async Task Copy_Accepted_ShouldShowTheCopiedMessage()
    {
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(true);

        var container = Render<AntContainer>();

        var (result, shown) = await CopyAsync();

        Assert.Equal(CopyResult.Done, result);
        Assert.Empty(shown); // Info 不寫日誌（ViewNotificationTests），改看畫面上的通知
        container.WaitForAssertion(() => Assert.Equal("已複製。", container.Find(".ant-notification-notice-description").TextContent.Trim()));
        Assert.Equal("要複製的文字", Assert.Single(JSInterop.Invocations[CopyFunction]).Arguments[0]);
    }

    [Fact]
    public async Task Copy_RefusedByTheBrowser_ShouldShowTheRefusedWarning()
    {
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(false);

        var container = Render<AntContainer>();

        var (result, shown) = await CopyAsync();

        Assert.Equal(CopyResult.Refused, result);
        Assert.Empty(shown); // Warning（沿用為一般提示）不寫日誌，改看畫面上的通知
        container.WaitForAssertion(() => Assert.Equal(ClipboardCopy.RefusedMessage, container.Find(".ant-notification-notice-description").TextContent.Trim()));
    }

    [Fact]
    public async Task Copy_Throwing_ShouldShowTheFailedMessageAsAnUnexpectedError()
    {
        var failure = new JSException("denied");
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetException(failure);

        var (result, shown) = await CopyAsync();

        Assert.False(result.Copied);
        Assert.Same(failure, result.Error);
        Assert.Equal([(LogLevel.Error, "複製失敗，請手動選取複製。")], shown);
        Assert.Equal(["Error"], lastKinds);
    }

    private async Task<(CopyResult Result, (LogLevel Level, string Text)[] Shown)> CopyAsync()
    {
        var factory = new CapturingLoggerFactory();
        var notifications = Services.GetRequiredService<NotificationService>();
        var js = Services.GetRequiredService<IJSRuntime>();
        ViewNotification.UseLoggerFactory(factory);
        try
        {
            var result = await js.CopyTextAsync("要複製的文字", notifications, "已複製。", "複製失敗，請手動選取複製。");
            lastKinds = factory.Kinds.ToArray();
            return (result, factory.Entries.ToArray());
        }
        finally
        {
            ViewNotification.UseLoggerFactory(NullLoggerFactory.Instance);
        }
    }

    private string[] lastKinds = [];

    private async Task<(DownloadResult Result, (LogLevel Level, string Text)[] Shown)> RunAsync(ApiDownload outcome, bool cautionOnBadRequest = false)
    {
        var factory = new CapturingLoggerFactory();
        var notifications = Services.GetRequiredService<NotificationService>();
        var js = Services.GetRequiredService<IJSRuntime>();
        ViewNotification.UseLoggerFactory(factory);
        try
        {
            var result = await js.SaveDownloadAsync(outcome, notifications, "匯出失敗", cautionOnBadRequest);
            lastKinds = factory.Kinds.ToArray();
            return (result, factory.Entries.ToArray());
        }
        finally
        {
            ViewNotification.UseLoggerFactory(NullLoggerFactory.Instance);
        }
    }

    /// <summary>通知顯示時寫的日誌（等級＋通知內文），用來看實際顯示了哪一種通知、什麼文字。</summary>
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
