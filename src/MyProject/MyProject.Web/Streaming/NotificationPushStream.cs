using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Streaming;

/// <summary>
/// 通知推播串流的時間與名額（0.10.7 起，WASM 遷移 M8）。singleton；每次連線開始時讀一次（測試可改小）。
/// </summary>
public sealed class NotificationPushOptions
{
    /// <summary>閒置時多久送一次心跳（中間的代理伺服器常以 60 秒以上沒資料就切斷連線）。</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 一條連線最長多久就由伺服器結束、請前端重連：推播是長連線，請求開始時才檢查一次身分，
    /// 帳號停用、強制登出要靠重連（或任何一次查詢 API）才生效；也讓意外遺留的連線有上限。
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>存取 Token 到期前多久結束串流（送 <see cref="PushFrameKinds.Renew"/>，前端刷新 Token 後重連）。</summary>
    public TimeSpan RenewBeforeExpiry { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 每位使用者同時最多幾條推播連線（每個分頁一條）。超過的回 429，那個分頁只靠 60 秒輪詢。
    /// 也是 HTTP/1.1 下瀏覽器每個網域只有 6 條連線的保護：推播最多佔 3 條，其他留給一般 API。
    /// </summary>
    public int MaxConnectionsPerUser { get; set; } = 3;
}

/// <summary>每位使用者目前的推播連線數（singleton，單一行程內）。</summary>
public sealed class NotificationPushConnections
{
    private readonly ConcurrentDictionary<int, int> counts = new();

    public int CountOf(int userId) => counts.TryGetValue(userId, out var count) ? count : 0;

    /// <summary>取得一個名額；已達上限回 null。回傳的物件 Dispose 時歸還（可重複呼叫）。</summary>
    public IDisposable? TryAcquire(int userId, int limit)
    {
        while (true)
        {
            var current = CountOf(userId);
            if (current >= limit)
            {
                return null;
            }

            if (current == 0 ? counts.TryAdd(userId, 1) : counts.TryUpdate(userId, current + 1, current))
            {
                return new Lease(this, userId);
            }
        }
    }

    private void Release(int userId)
    {
        while (counts.TryGetValue(userId, out var current))
        {
            var released = current <= 1
                ? counts.TryRemove(new KeyValuePair<int, int>(userId, current))
                : counts.TryUpdate(userId, current - 1, current);
            if (released)
            {
                return;
            }
        }
    }

    private sealed class Lease(NotificationPushConnections owner, int userId) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(userId);
            }
        }
    }
}

/// <summary>
/// 通知推播串流的內容（0.10.7 起，WASM 遷移 M8）：訂閱這位使用者的通知訊號與公告訊號（<see cref="INotificationSignal"/>，
/// 與 Server 版鈴鐺、橫幅相同的來源），有變動就送一筆，不帶內容。短時間內的多次變動合併成一筆（前端反正是重查）。
/// <para>
/// ⚠️ 這是無限長的序列：<see cref="NdjsonResult{T}"/> 不會替它停止，這裡自己看 <c>RequestAborted</c> 結束列舉並取消訂閱
/// （訊號是 singleton，不取消會一直留著委派）。時間到（Token 快到期或連線最長時間）也由這裡結束，最後送一筆告訴前端怎麼重連。
/// </para>
/// </summary>
public static class NotificationPushStream
{
    /// <summary>這條連線可以開多久，以及結束時要不要請前端先刷新 Token（Token 先到期時）。</summary>
    public static (TimeSpan Lifetime, bool Renew) ResolveLifetime(DateTimeOffset now, DateTimeOffset? tokenExpiresAt, NotificationPushOptions options)
    {
        if (tokenExpiresAt is { } expires && expires - options.RenewBeforeExpiry - now < options.MaxDuration)
        {
            var untilRenew = expires - options.RenewBeforeExpiry - now;
            return (untilRenew > TimeSpan.Zero ? untilRenew : TimeSpan.Zero, true);
        }

        return (options.MaxDuration, false);
    }

    /// <param name="serverStopping">網站正在關閉（IIS 回收、重新部署）：結束串流請前端重連，不讓長連線拖住關閉。</param>
    public static async IAsyncEnumerable<PushFrameDto> ProduceAsync(
        INotificationSignal signal, int userId, TimeSpan lifetime, bool renew, TimeProvider timeProvider,
        CancellationToken serverStopping, [EnumeratorCancellation] CancellationToken requestAborted)
    {
        var pendingNotifications = 0;
        var pendingAnnouncements = 0;
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        // 訊號的處理常式只立旗標、叫醒迴圈；同時來好幾次只會送一筆。
        using var notifications = signal.Subscribe(userId, () =>
        {
            Interlocked.Exchange(ref pendingNotifications, 1);
            wake.Writer.TryWrite(true);
            return Task.CompletedTask;
        });
        using var announcements = signal.SubscribeAnnouncements(() =>
        {
            Interlocked.Exchange(ref pendingAnnouncements, 1);
            wake.Writer.TryWrite(true);
            return Task.CompletedTask;
        });
        using var expiry = new CancellationTokenSource(lifetime, timeProvider);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, serverStopping, expiry.Token);

        yield return new PushFrameDto { Kind = PushFrameKinds.Ready };

        while (await WaitAsync(wake.Reader, stop.Token))
        {
            if (Interlocked.Exchange(ref pendingNotifications, 0) == 1)
            {
                yield return new PushFrameDto { Kind = PushFrameKinds.Notifications };
            }

            if (Interlocked.Exchange(ref pendingAnnouncements, 0) == 1)
            {
                yield return new PushFrameDto { Kind = PushFrameKinds.Announcements };
            }
        }

        if (!requestAborted.IsCancellationRequested)
        {
            yield return new PushFrameDto { Kind = renew && !serverStopping.IsCancellationRequested ? PushFrameKinds.Renew : PushFrameKinds.Reconnect };
        }
    }

    /// <summary>等下一次變動；連線中斷或時間到回 false。</summary>
    private static async Task<bool> WaitAsync(ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        try
        {
            await reader.ReadAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
