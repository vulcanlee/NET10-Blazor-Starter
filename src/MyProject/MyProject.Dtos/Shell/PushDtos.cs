namespace MyProject.Dtos.Shell;

/// <summary>
/// 通知推播串流的一行（0.10.7 起，WASM 遷移 M8；<c>POST /api/v1/notifications/stream</c>，NDJSON）。
/// 推播不帶內容，只告訴外框「你的通知有變動」或「公告有變動」，外框自己重新查詢（與 Server 版 <c>INotificationSignal</c> 相同）。
/// 依序為一筆 <see cref="PushFrameKinds.Ready"/>、零到多筆變動與 <see cref="PushFrameKinds.Ping"/>，
/// 伺服器主動結束時最後一筆是 <see cref="PushFrameKinds.Renew"/> 或 <see cref="PushFrameKinds.Reconnect"/>。
/// </summary>
public class PushFrameDto
{
    /// <summary><see cref="PushFrameKinds"/> 的其中一個。</summary>
    public string Kind { get; set; } = string.Empty;
}

public static class PushFrameKinds
{
    /// <summary>已訂閱，之後的變動都收得到。</summary>
    public const string Ready = "ready";

    /// <summary>自己的通知有變動（新通知、標已讀、刪除）。</summary>
    public const string Notifications = "notifications";

    /// <summary>公告有變動。</summary>
    public const string Announcements = "announcements";

    /// <summary>心跳：閒置時每 25 秒一筆，讓中間的代理伺服器不把連線當成閒置切斷；收到後忽略。</summary>
    public const string Ping = "ping";

    /// <summary>存取 Token 快到期，伺服器結束串流：先刷新 Token 再重新連線。</summary>
    public const string Renew = "renew";

    /// <summary>連線已達最長時間，伺服器結束串流：立即重新連線。</summary>
    public const string Reconnect = "reconnect";
}
