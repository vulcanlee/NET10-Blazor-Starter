namespace MyProject.Web.Client.Layout;

/// <summary>
/// 請 WASM 外框的某個區塊立即重讀（0.10.6 起，WASM 遷移 M7）。外框平常 60 秒輪詢一次；Server 版靠伺服器端的訊號即時更新，
/// WASM 頁面做了「會讓外框內容改變」的事之後呼叫 <see cref="RequestRefresh"/>，不必等下一次輪詢。
/// 訂閱者一定要在 Dispose 時取消訂閱（scoped，整個 WASM 執行期間只有一個）。
/// </summary>
public abstract class ClientRefreshSignal
{
    public event Func<Task>? RefreshRequested;

    /// <summary>請所有訂閱者重讀；<paramref name="except"/> 是呼叫端自己的處理常式（它自己已經重讀過，不必再打一次 API）。</summary>
    public void RequestRefresh(Func<Task>? except = null)
    {
        if (RefreshRequested is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            if (except is not null && handler.Equals(except))
            {
                continue;
            }

            _ = handler();
        }
    }
}

/// <summary>通知鈴鐺與首頁「我的通知」（例如系統健康監控的「發送測試通知」之後）。</summary>
public sealed class ClientNotificationSignal : ClientRefreshSignal
{
}

/// <summary>外框上方的公告橫幅（公告管理頁新增、修改、刪除之後；Server 版是即時的，同一個分頁不必等 60 秒）。</summary>
public sealed class ClientAnnouncementSignal : ClientRefreshSignal
{
}
