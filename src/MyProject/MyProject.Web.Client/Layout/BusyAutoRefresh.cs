namespace MyProject.Web.Client.Layout;

/// <summary>
/// 「作業在佇列或執行中才定期更新」（0.10.45 起，整併自系統備份與資料保留兩頁相同的寫法）：
/// <see cref="Update"/> 傳入忙碌狀態，忙碌時每隔 <c>interval</c> 在元件的同步內容上呼叫一次更新，閒下來就停（不白白查資料庫）；離開頁面時 <see cref="Dispose"/>。
/// 與 <see cref="PeriodicRefresh"/> 不同：不會一直輪詢，也不會在切回分頁時更新。日誌由頁面自己寫（停止與失敗兩個回呼）。
/// </summary>
public sealed class BusyAutoRefresh : IDisposable
{
    private readonly TimeSpan interval;
    private readonly Func<Func<Task>, Task> invokeAsync;
    private readonly Func<Task> refresh;
    private readonly Action onStopped;
    private readonly Action<Exception> onFailed;
    private CancellationTokenSource? running;
    private bool disposed;

    /// <param name="invokeAsync">元件的 <c>InvokeAsync</c>：計時器回呼不在 renderer 的同步內容上。</param>
    public BusyAutoRefresh(TimeSpan interval, Func<Func<Task>, Task> invokeAsync, Func<Task> refresh, Action onStopped, Action<Exception> onFailed)
    {
        this.interval = interval;
        this.invokeAsync = invokeAsync;
        this.refresh = refresh;
        this.onStopped = onStopped;
        this.onFailed = onFailed;
    }

    public void Update(bool busy)
    {
        if (busy && running is null && !disposed)
        {
            running = new CancellationTokenSource();
            _ = RunAsync(running.Token);
        }
        else if (!busy && running is not null)
        {
            running.Cancel();
            running = null;
        }
    }

    public void Dispose()
    {
        disposed = true;
        running?.Cancel();
        running = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await invokeAsync(async () =>
                {
                    if (disposed || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await refresh();
                });
            }
        }
        catch (OperationCanceledException)
        {
            onStopped();
        }
        catch (Exception ex)
        {
            onFailed(ex);
        }
    }
}
