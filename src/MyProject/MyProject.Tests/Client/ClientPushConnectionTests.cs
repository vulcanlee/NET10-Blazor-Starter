using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Layout;

namespace MyProject.Tests.Client;

/// <summary>
/// WASM 外框的通知推播連線（0.10.7 起，WASM 遷移 M8）：收到變動就請對應的區塊重讀、斷線時以遞增間隔重試、
/// 伺服器請前端重連時立即重連（renew 先刷新 Token）、重新接上時補讀一次、登入失效或推播關閉時停止（輪詢照常）。
/// 用假的連線與等待（瀏覽器的 fetch 串流由實機驗證涵蓋）。
/// </summary>
public sealed class ClientPushConnectionTests
{
    [Fact]
    public async Task Frames_ShouldAskTheMatchingShellPartsToRefresh_AndIgnorePings()
    {
        var harness = new Harness();
        harness.Script(Connected(PushFrameKinds.Ready, PushFrameKinds.Ping, PushFrameKinds.Notifications, PushFrameKinds.Announcements, PushFrameKinds.Notifications));

        await harness.RunAsync();

        Assert.Equal(2, harness.NotificationRefreshes);
        Assert.Equal(1, harness.AnnouncementRefreshes);
    }

    [Fact]
    public async Task WhenTheServerCannotBeReached_ShouldRetryWithExponentialBackoff_UpTo60Seconds()
    {
        var harness = new Harness();
        for (var i = 0; i < 8; i++)
        {
            harness.Script(Offline());
        }

        await harness.RunAsync();

        Assert.Equal(new[] { 2, 4, 8, 16, 32, 60, 60, 60 }, harness.Waits.Select(x => (int)x.TotalSeconds));
        Assert.Equal(9, harness.Connections);
    }

    [Fact]
    public async Task AfterAStableConnectionDrops_ShouldStartTheBackoffFromTheBeginning()
    {
        var harness = new Harness();
        harness.Script(Offline());
        harness.Script(Offline());
        harness.Script(Connected(TimeSpan.FromMinutes(5), PushFrameKinds.Ready)); // 接上 5 分鐘後斷掉（沒有 renew／reconnect）
        harness.Script(Offline());

        await harness.RunAsync();

        Assert.Equal(new[] { 2, 4, 2, 4 }, harness.Waits.Select(x => (int)x.TotalSeconds));
    }

    [Fact]
    public async Task WhenTheServerAsksToRenew_ShouldRefreshTheTokenAndReconnectImmediately()
    {
        var harness = new Harness();
        harness.Script(Connected(TimeSpan.FromMinutes(29), PushFrameKinds.Ready, PushFrameKinds.Renew));

        await harness.RunAsync();

        Assert.Equal(1, harness.Renewals);
        Assert.Empty(harness.Waits);
        Assert.Equal(2, harness.Connections);
    }

    [Fact]
    public async Task WhenTheServerAsksToReconnect_ShouldReconnectImmediately_WithoutRefreshingTheToken()
    {
        var harness = new Harness();
        harness.Script(Connected(TimeSpan.FromMinutes(30), PushFrameKinds.Ready, PushFrameKinds.Reconnect));

        await harness.RunAsync();

        Assert.Equal(0, harness.Renewals);
        Assert.Empty(harness.Waits);
        Assert.Equal(2, harness.Connections);
    }

    [Fact]
    public async Task WhenTheServerKeepsEndingTheStreamRightAway_ShouldBackOffInsteadOfLooping()
    {
        var harness = new Harness();
        for (var i = 0; i < 3; i++)
        {
            harness.Script(Connected(TimeSpan.FromSeconds(1), PushFrameKinds.Ready, PushFrameKinds.Renew));
        }

        await harness.RunAsync();

        Assert.Equal(new[] { 2, 4, 8 }, harness.Waits.Select(x => (int)x.TotalSeconds));
    }

    [Fact]
    public async Task WhenTheTokenCannotBeRenewed_ShouldBackOff()
    {
        var harness = new Harness { RenewSucceeds = false };
        harness.Script(Connected(TimeSpan.FromMinutes(29), PushFrameKinds.Ready, PushFrameKinds.Renew));

        await harness.RunAsync();

        Assert.Equal(new[] { 2 }, harness.Waits.Select(x => (int)x.TotalSeconds));
    }

    [Fact]
    public async Task Reconnecting_ShouldRefreshBothParts_ToCatchUpOnMissedChanges_ButTheFirstConnectionShouldNot()
    {
        var harness = new Harness();
        harness.Script(Connected(TimeSpan.FromMinutes(5), PushFrameKinds.Ready));
        harness.Script(Connected(PushFrameKinds.Ready));

        await harness.RunAsync();

        Assert.Equal(1, harness.NotificationRefreshes);
        Assert.Equal(1, harness.AnnouncementRefreshes);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task WhenSignedOutForbiddenOrPushIsDisabled_ShouldStopRetrying(int status)
    {
        var harness = new Harness();
        harness.Script((_, _) => Task.FromResult(ApiResult<object>.FailureResult("x", status)));

        await harness.RunUntilStoppedAsync();

        Assert.Equal(1, harness.Connections);
        Assert.Empty(harness.Waits);
    }

    [Fact]
    public async Task TooManyConnections_ShouldBackOff()
    {
        var harness = new Harness();
        harness.Script((_, _) => Task.FromResult(ApiResult<object>.FailureResult("推播連線數已達上限。", 429)));

        await harness.RunAsync();

        Assert.Equal(new[] { 2 }, harness.Waits.Select(x => (int)x.TotalSeconds));
    }

    [Fact]
    public async Task Start_Twice_ShouldOpenOnlyOneConnection_AndStopShouldEndIt()
    {
        var harness = new Harness();

        harness.Connection.Start();
        harness.Connection.Start();
        await harness.Exhausted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.Connection.StopAsync();

        Assert.Equal(1, harness.Connections);
        Assert.True(harness.LastConnectionCancelled);
    }

    private static Func<Func<PushFrameDto, Task>, Harness, Task<ApiResult<object>>> Connected(params string[] kinds)
        => Connected(TimeSpan.Zero, kinds);

    private static Func<Func<PushFrameDto, Task>, Harness, Task<ApiResult<object>>> Connected(TimeSpan duration, params string[] kinds)
        => async (onFrame, harness) =>
        {
            foreach (var kind in kinds)
            {
                await onFrame(new PushFrameDto { Kind = kind });
            }

            harness.Clock.Advance(duration);
            return ApiResult<object>.SuccessResult(null!);
        };

    private static Func<Func<PushFrameDto, Task>, Harness, Task<ApiResult<object>>> Offline()
        => (_, _) => Task.FromResult(ApiResult<object>.FailureResult("目前無法連線到伺服器，請稍後再試。", 0));

    private sealed class Harness
    {
        private readonly Queue<Func<Func<PushFrameDto, Task>, Harness, Task<ApiResult<object>>>> script = new();

        public Harness()
        {
            var notificationSignal = new ClientNotificationSignal();
            var announcementSignal = new ClientAnnouncementSignal();
            notificationSignal.RefreshRequested += () =>
            {
                NotificationRefreshes++;
                return Task.CompletedTask;
            };
            announcementSignal.RefreshRequested += () =>
            {
                AnnouncementRefreshes++;
                return Task.CompletedTask;
            };
            Connection = new ClientPushConnection(
                notificationSignal, announcementSignal, NullLogger<ClientPushConnection>.Instance,
                StreamAsync,
                () =>
                {
                    Renewals++;
                    return Task.FromResult(RenewSucceeds);
                },
                (wait, _) =>
                {
                    Waits.Add(wait);
                    return Task.CompletedTask;
                },
                Clock);
        }

        public ClientPushConnection Connection { get; }

        public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero));

        public List<TimeSpan> Waits { get; } = [];

        public TaskCompletionSource Exhausted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RenewSucceeds { get; init; } = true;

        public int Connections { get; private set; }

        public int Renewals { get; private set; }

        public int NotificationRefreshes { get; private set; }

        public int AnnouncementRefreshes { get; private set; }

        public bool LastConnectionCancelled { get; private set; }

        public void Script(Func<Func<PushFrameDto, Task>, Harness, Task<ApiResult<object>>> attempt) => script.Enqueue(attempt);

        /// <summary>跑完腳本後停在一條「一直連著」的連線上，再停止。</summary>
        public async Task RunAsync()
        {
            Connection.Start();
            await Exhausted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Connection.StopAsync();
        }

        /// <summary>預期連線自己停止（不會走到腳本之外）。</summary>
        public async Task RunUntilStoppedAsync()
        {
            Connection.Start();
            await Task.Delay(100);
            Assert.False(Exhausted.Task.IsCompleted);
            await Connection.StopAsync();
        }

        private async Task<ApiResult<object>> StreamAsync(Func<PushFrameDto, Task> onFrame, CancellationToken cancellationToken)
        {
            Connections++;
            if (script.TryDequeue(out var attempt))
            {
                return await attempt(onFrame, this);
            }

            Exhausted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LastConnectionCancelled = true;
            }

            return ApiResult<object>.FailureResult("已取消。", 0);
        }
    }
}
