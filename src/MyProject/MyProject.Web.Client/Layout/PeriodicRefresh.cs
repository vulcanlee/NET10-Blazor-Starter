using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 外框的定期更新（0.10.1 起）：每隔一段時間、以及切回這個分頁時，呼叫一次更新。
/// Server 版的通知鈴鐺與公告是伺服器即時推送；WASM 版在 M8 加上推播串流之前，靠這裡每 60 秒輪詢。
/// </summary>
public sealed class PeriodicRefresh : IAsyncDisposable
{
    private const string ModulePath = "./js/client-shell.js";
    private readonly CancellationTokenSource disposing = new();
    private readonly Func<Task> refresh;
    private readonly ILogger logger;
    private DotNetObjectReference<PeriodicRefresh>? selfReference;
    private IJSObjectReference? module;
    private IJSObjectReference? visibilitySubscription;
    private int disposed;

    private PeriodicRefresh(Func<Task> refresh, ILogger logger)
    {
        this.refresh = refresh;
        this.logger = logger;
    }

    public static async Task<PeriodicRefresh> StartAsync(IJSRuntime jsRuntime, TimeSpan interval, Func<Task> refresh, ILogger logger)
    {
        var instance = new PeriodicRefresh(refresh, logger);
        _ = instance.RunTimerAsync(interval, instance.disposing.Token);
        try
        {
            instance.selfReference = DotNetObjectReference.Create(instance);
            instance.module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
            instance.visibilitySubscription = await instance.module.InvokeAsync<IJSObjectReference>("onVisible", instance.selfReference, nameof(OnVisibleAsync));
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // 腳本沒載入（或還不能呼叫 JS）：只少了「切回分頁立即更新」，定期更新照常。
            logger.LogWarning(ex, "Failed to subscribe to page visibility changes.");
        }

        return instance;
    }

    [JSInvokable]
    public Task OnVisibleAsync() => SafeRefreshAsync();

    /// <summary>可重複呼叫；計時迴圈只拿啟動時的 token，釋放後不會再碰已釋放的 <see cref="CancellationTokenSource"/>。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        await disposing.CancelAsync();
        try
        {
            if (visibilitySubscription is not null)
            {
                await visibilitySubscription.InvokeVoidAsync("dispose");
                await visibilitySubscription.DisposeAsync();
            }

            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException)
        {
            // 頁面已關閉或腳本已不在：沒有東西要清。
            logger.LogDebug(ex, "Visibility subscription was already gone.");
        }
        finally
        {
            selfReference?.Dispose();
            disposing.Dispose();
        }
    }

    private async Task RunTimerAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SafeRefreshAsync();
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Periodic refresh stopped.");
        }
    }

    private async Task SafeRefreshAsync()
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        try
        {
            await refresh();
        }
        catch (Exception ex)
        {
            // 更新失敗不該讓版面壞掉；下一次再試。
            logger.LogWarning(ex, "Periodic refresh failed.");
        }
    }
}
