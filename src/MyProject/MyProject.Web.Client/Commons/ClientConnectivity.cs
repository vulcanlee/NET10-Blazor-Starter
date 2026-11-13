using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// 瀏覽器的連線狀態（0.10.11 起，WASM 遷移 M12）：包住 JS 的 <c>navigator.onLine</c> 與 <c>online</c>／<c>offline</c> 事件（<c>wwwroot/js/pwa.js</c>），
/// 讓 .NET 端可以測試。由根元件的離線提示列（<c>ClientOfflineBar</c>）啟動，整個 WASM 執行期間只有一個（scoped）。
/// <para>
/// ⚠️ <see cref="IsOnline"/> 為 false 表示「瀏覽器確定離線」（沒有網路）；為 true 只代表有網路，伺服器仍可能連不上。
/// <c>ApiClient</c> 依它決定連不上時顯示「目前離線」還是「無法連線到伺服器」。
/// </para>
/// </summary>
public sealed class ClientConnectivity : IAsyncDisposable
{
    private readonly IJSRuntime js;
    private readonly ILogger<ClientConnectivity>? logger;
    private DotNetObjectReference<ClientConnectivity>? self;

    public ClientConnectivity(IJSRuntime js, ILogger<ClientConnectivity>? logger = null)
    {
        this.js = js;
        this.logger = logger;
    }

    /// <summary>瀏覽器是否連線（啟動前一律當成連線）。</summary>
    public bool IsOnline { get; private set; } = true;

    /// <summary>連線狀態變了（離線或恢復連線）。</summary>
    public event Func<Task>? Changed;

    /// <summary>開始追蹤（重複呼叫只做一次）：讀目前的狀態，之後瀏覽器每次變化都會呼叫 <see cref="OnConnectivityChanged"/>。</summary>
    public async Task StartAsync()
    {
        if (self is not null)
        {
            return;
        }

        self = DotNetObjectReference.Create(this);
        try
        {
            await OnConnectivityChanged(await js.InvokeAsync<bool>("appPwa.watchConnectivity", self));
        }
        catch (JSException ex)
        {
            // pwa.js 沒載入（例如被瀏覽器擋下）：當成一直連線，畫面照舊。
            logger?.LogWarning(ex, "Failed to watch the browser's connectivity.");
        }
    }

    /// <summary>由 <c>pwa.js</c> 在 <c>online</c>／<c>offline</c> 事件時呼叫（測試也直接呼叫）。狀態沒變就不通知。</summary>
    [JSInvokable]
    public async Task OnConnectivityChanged(bool online)
    {
        if (IsOnline == online)
        {
            return;
        }

        IsOnline = online;
        if (Changed is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<Task>>())
            {
                await handler();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
