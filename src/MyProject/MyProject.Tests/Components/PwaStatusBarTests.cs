using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Commons;
using MyProject.Web.Client.Layout;

namespace MyProject.Tests.Components;

/// <summary>
/// PWA 的兩條提示列（0.10.11 起，WASM 遷移 M12；掛在根元件 ClientRoutes，登入頁等匿名頁也看得到）：
/// <list type="bullet">
/// <item>離線提示列：瀏覽器離線時出現；恢復連線時消失，並讓推播立即重連、鈴鐺與公告立即重讀（不等退避計時）。</item>
/// <item>新版本提示列：js/pwa.js 通知有新版的 Service Worker 在等待時出現；使用者按下才套用（不強制重新載入，可能正在編輯）。</item>
/// </list>
/// 瀏覽器的 online／offline 事件與 Service Worker 由實機驗證涵蓋。
/// </summary>
public sealed class PwaStatusBarTests : ComponentTestBase
{
    private const string OfflineText = "目前離線：畫面可以瀏覽，但需要連線的功能暫時無法使用；恢復連線後會自動重新連線。";

    private readonly ClientConnectivity connectivity;
    private readonly TaskCompletionSource waitingToRetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int connections;
    private int notificationRefreshes;
    private int announcementRefreshes;

    public PwaStatusBarTests()
    {
        connectivity = new ClientConnectivity(JSInterop.JSRuntime);
        var notificationSignal = new ClientNotificationSignal();
        var announcementSignal = new ClientAnnouncementSignal();
        notificationSignal.RefreshRequested += () =>
        {
            notificationRefreshes++;
            return Task.CompletedTask;
        };
        announcementSignal.RefreshRequested += () =>
        {
            announcementRefreshes++;
            return Task.CompletedTask;
        };

        // 推播：第一次連不上，之後一直連著；重試前真的等（等到被取消）。
        var push = new ClientPushConnection(
            notificationSignal, announcementSignal, NullLogger<ClientPushConnection>.Instance,
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref connections) == 1)
                {
                    return ApiResult<object>.FailureResult("目前離線，請恢復連線後再試。", 0);
                }

                reconnected.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                return ApiResult<object>.FailureResult("已取消。", 0);
            },
            () => Task.FromResult(true),
            async (_, cancellationToken) =>
            {
                waitingToRetry.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            },
            TimeProvider.System);

        Services.AddSingleton(connectivity);
        Services.AddSingleton(notificationSignal);
        Services.AddSingleton(announcementSignal);
        Services.AddSingleton(push);
    }

    [Fact]
    public void OfflineBar_WhenTheBrowserIsOnline_ShouldNotShow()
    {
        JSInterop.Setup<bool>("appPwa.watchConnectivity", _ => true).SetResult(true);

        var bar = Render<ClientOfflineBar>();

        JSInterop.VerifyInvoke("appPwa.watchConnectivity");
        Assert.Empty(bar.FindAll(".app-offline-bar"));
        Assert.True(connectivity.IsOnline);
    }

    [Fact]
    public async Task OfflineBar_ShouldShowWhileOffline_AndOnReconnectHideAndRefreshTheShellRightAway()
    {
        JSInterop.Setup<bool>("appPwa.watchConnectivity", _ => true).SetResult(false);
        var push = Services.GetRequiredService<ClientPushConnection>();
        push.Start();
        await waitingToRetry.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var bar = Render<ClientOfflineBar>();

        bar.WaitForAssertion(() => Assert.Equal(OfflineText, bar.Find(".app-offline-bar").TextContent.Trim()));
        Assert.Equal("status", bar.Find(".app-offline-bar").GetAttribute("role"));
        Assert.False(connectivity.IsOnline);

        await bar.InvokeAsync(() => connectivity.OnConnectivityChanged(true));

        bar.WaitForAssertion(() => Assert.Empty(bar.FindAll(".app-offline-bar")));
        Assert.Equal(1, notificationRefreshes);
        Assert.Equal(1, announcementRefreshes);
        // 推播不等退避計時（第一次失敗後要等 2 秒），立即重連。
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, connections);
        await push.StopAsync();
    }

    [Fact]
    public async Task OfflineBar_ShouldFollowLaterChanges()
    {
        JSInterop.Setup<bool>("appPwa.watchConnectivity", _ => true).SetResult(true);
        var bar = Render<ClientOfflineBar>();

        await bar.InvokeAsync(() => connectivity.OnConnectivityChanged(false));

        bar.WaitForAssertion(() => Assert.Equal(OfflineText, bar.Find(".app-offline-bar").TextContent.Trim()));
        Assert.Equal(0, notificationRefreshes);
    }

    [Fact]
    public async Task UpdateBar_ShouldAppearWhenANewVersionIsWaiting_AndApplyItOnlyWhenClicked()
    {
        JSInterop.SetupVoid("appPwa.watchUpdates", _ => true);
        var apply = JSInterop.SetupVoid("appPwa.applyUpdate");

        var bar = Render<ClientUpdateBar>();
        JSInterop.VerifyInvoke("appPwa.watchUpdates");
        Assert.Empty(bar.FindAll(".app-update-bar"));

        await bar.InvokeAsync(() => bar.Instance.OnUpdateAvailable());

        // 0.10.20 起：說明文字與按鈕分開，講清楚要先存檔、按哪個按鈕。
        Assert.Equal("系統已更新到新版本。請先儲存編輯中的資料，再按「立即更新」。", bar.Find(".app-update-text").TextContent.Trim());
        Assert.Equal("稍後", bar.Find("button.app-update-later").TextContent.Trim());
        var button = bar.Find("button.app-update-apply");
        Assert.Equal("立即更新", button.TextContent.Trim());
        apply.VerifyNotInvoke("appPwa.applyUpdate");

        button.Click();

        apply.VerifyInvoke("appPwa.applyUpdate");
    }

    [Fact]
    public async Task UpdateBar_Later_ShouldHideItWithoutApplying()
    {
        JSInterop.SetupVoid("appPwa.watchUpdates", _ => true);
        var apply = JSInterop.SetupVoid("appPwa.applyUpdate");
        var bar = Render<ClientUpdateBar>();
        await bar.InvokeAsync(() => bar.Instance.OnUpdateAvailable());

        bar.Find("button.app-update-later").Click();

        Assert.Empty(bar.FindAll(".app-update-bar"));
        apply.VerifyNotInvoke("appPwa.applyUpdate");
    }
}
