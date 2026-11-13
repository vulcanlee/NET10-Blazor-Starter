using Microsoft.Extensions.Logging;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 外框的通知推播連線（0.10.7 起，WASM 遷移 M8）：整個 WASM 執行期間只有一條（scoped，由外框啟動），
/// 收到 <c>notifications</c> 就請鈴鐺與首頁「我的通知」重讀（<see cref="ClientNotificationSignal"/>），
/// 收到 <c>announcements</c> 就請公告橫幅重讀（<see cref="ClientAnnouncementSignal"/>）—— 與 Server 版靠伺服器訊號即時更新相同。
/// <para>
/// 推播只是加速：鈴鐺、橫幅、首頁的 60 秒輪詢一直都在，推播斷線或關閉時照樣會更新。
/// 斷線後以 2、4、8…最多 60 秒的間隔重試（<see cref="PushReconnectBackoff"/>）；重新接上時兩邊各重讀一次，補上斷線期間的變動。
/// 401 由 <see cref="ApiClient.StreamAsync{T}"/> 刷新 Token 後重送；伺服器因 Token 快到期結束串流（renew）時先刷新再重連。
/// </para>
/// </summary>
public sealed class ClientPushConnection : IAsyncDisposable
{
    internal const string StreamUrl = "api/v1/notifications/stream";

    private readonly ClientNotificationSignal notificationSignal;
    private readonly ClientAnnouncementSignal announcementSignal;
    private readonly ILogger<ClientPushConnection> logger;
    private readonly Func<Func<PushFrameDto, Task>, CancellationToken, Task<ApiResult<object>>> stream;
    private readonly Func<Task<bool>> renewToken;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider timeProvider;
    private readonly PushReconnectBackoff backoff = new();
    private CancellationTokenSource? running;
    private Task? loop;
    private CancellationTokenSource? waiting;

    public ClientPushConnection(
        ApiClient api, ClientSession session, ClientNotificationSignal notificationSignal, ClientAnnouncementSignal announcementSignal, ILogger<ClientPushConnection> logger)
        : this(
            notificationSignal, announcementSignal, logger,
            (onFrame, cancellationToken) => api.StreamAsync(StreamUrl, null, onFrame, cancellationToken),
            session.RefreshAsync,
            (wait, cancellationToken) => Task.Delay(wait, cancellationToken),
            TimeProvider.System)
    {
    }

    /// <summary>測試用：換掉連線、刷新 Token、等待與時鐘。</summary>
    internal ClientPushConnection(
        ClientNotificationSignal notificationSignal,
        ClientAnnouncementSignal announcementSignal,
        ILogger<ClientPushConnection> logger,
        Func<Func<PushFrameDto, Task>, CancellationToken, Task<ApiResult<object>>> stream,
        Func<Task<bool>> renewToken,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider timeProvider)
    {
        this.notificationSignal = notificationSignal;
        this.announcementSignal = announcementSignal;
        this.logger = logger;
        this.stream = stream;
        this.renewToken = renewToken;
        this.delay = delay;
        this.timeProvider = timeProvider;
    }

    /// <summary>已經在跑就不做事（外框只會有一個，但重複呼叫也不會開第二條）。</summary>
    public void Start()
    {
        if (running is not null)
        {
            return;
        }

        running = new CancellationTokenSource();
        loop = RunAsync(running.Token);
    }

    /// <summary>停止並中斷目前的連線（伺服器端隨即取消訂閱）。</summary>
    public async Task StopAsync()
    {
        var stopping = running;
        running = null;
        if (stopping is null)
        {
            return;
        }

        await stopping.CancelAsync();
        if (loop is not null)
        {
            await loop;
        }

        stopping.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// 瀏覽器恢復連線（0.10.11 起，由離線提示列呼叫）：正在等下一次重試時立即重連，不必等完退避的時間（最長 60 秒）。
    /// 沒有在等（還沒啟動、已停止、正連著或正在連）時什麼都不做。
    /// </summary>
    public void ReconnectNow()
    {
        try
        {
            waiting?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 剛好等完、正要重連：不用再做什麼。
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var connectedBefore = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var attempt = new Attempt(connectedBefore);
            var startedAt = timeProvider.GetUtcNow();
            ApiResult<object> result;
            try
            {
                result = await stream(frame => OnFrameAsync(frame, attempt), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // StreamAsync 本身不丟例外；這裡只是保險：重讀的處理常式出錯也不能讓推播永遠停掉。
                logger.LogWarning(ex, "Notification push connection failed unexpectedly.");
                result = ApiResult<object>.FailureResult(ex.Message, 0);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (result.StatusCode is 401 or 403 or 404)
            {
                // 登入已失效（ApiClient 已帶去登入頁）、被要求先完成設定（已導頁），或伺服器關閉了推播：不再重試，只靠輪詢。
                logger.LogInformation("Notification push stopped; polling continues. Status={Status}", result.StatusCode);
                break;
            }

            connectedBefore |= attempt.Ready;
            var serverAsked = attempt.EndKind is PushFrameKinds.Renew or PushFrameKinds.Reconnect;
            if (attempt.EndKind == PushFrameKinds.Renew && !await renewToken())
            {
                serverAsked = false;
            }

            var wait = backoff.Next(serverAsked, attempt.Ready ? timeProvider.GetUtcNow() - startedAt : null);
            if (!serverAsked)
            {
                logger.LogDebug("Notification push disconnected; retrying in {Delay}. Status={Status}", wait, result.StatusCode);
            }

            if (wait > TimeSpan.Zero)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waiting = wake;
                try
                {
                    await delay(wait, wake.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // ReconnectNow：恢復連線了，立即重連。
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    waiting = null;
                }
            }
        }
    }

    private Task OnFrameAsync(PushFrameDto frame, Attempt attempt)
    {
        switch (frame.Kind)
        {
            case PushFrameKinds.Ready:
                attempt.Ready = true;
                if (attempt.ConnectedBefore)
                {
                    // 重新接上：斷線期間的變動沒有推過來，兩邊各重讀一次。
                    notificationSignal.RequestRefresh();
                    announcementSignal.RequestRefresh();
                }

                break;
            case PushFrameKinds.Notifications:
                notificationSignal.RequestRefresh();
                break;
            case PushFrameKinds.Announcements:
                announcementSignal.RequestRefresh();
                break;
            case PushFrameKinds.Renew:
            case PushFrameKinds.Reconnect:
                attempt.EndKind = frame.Kind;
                break;
        }

        return Task.CompletedTask;
    }

    private sealed class Attempt(bool connectedBefore)
    {
        public bool ConnectedBefore { get; } = connectedBefore;

        public bool Ready { get; set; }

        public string? EndKind { get; set; }
    }
}

/// <summary>
/// 推播斷線後的重試間隔（0.10.7 起）：2、4、8、16、32、60、60…秒。
/// 伺服器主動請前端重連（Token 快到期、連線滿最長時間）時立即重連；連線穩定一段時間後斷掉，從 2 秒重新算起。
/// 連線才接上就又被結束（例如瀏覽器與伺服器的時鐘差很多，Token 一接上就算快到期）也照失敗計，避免不停重連。
/// </summary>
internal sealed class PushReconnectBackoff
{
    internal static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan Maximum = TimeSpan.FromSeconds(60);

    /// <summary>連線維持這麼久才斷，視為穩定過，重試間隔從頭算。</summary>
    internal static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    /// <summary>伺服器請前端重連時，連線至少要維持這麼久才立即重連。</summary>
    internal static readonly TimeSpan MinimumHealthy = TimeSpan.FromSeconds(10);

    private int failures;

    /// <param name="serverAskedToReconnect">伺服器送了 renew／reconnect 才結束（而且需要的 Token 刷新成功）。</param>
    /// <param name="connectedFor">從連線到結束多久；沒有接上（沒收到 ready）時為 null。</param>
    public TimeSpan Next(bool serverAskedToReconnect, TimeSpan? connectedFor)
    {
        if (connectedFor >= StableAfter)
        {
            failures = 0;
        }

        if (serverAskedToReconnect && connectedFor >= MinimumHealthy)
        {
            failures = 0;
            return TimeSpan.Zero;
        }

        var seconds = Initial.TotalSeconds * Math.Pow(2, Math.Min(failures, 10));
        failures++;
        return seconds >= Maximum.TotalSeconds ? Maximum : TimeSpan.FromSeconds(seconds);
    }
}
