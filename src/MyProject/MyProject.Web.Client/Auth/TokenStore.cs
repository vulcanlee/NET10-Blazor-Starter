using System.Text.Json;
using Microsoft.JSInterop;

namespace MyProject.Web.Client.Auth;

/// <summary>瀏覽器裡存的一組 Token（0.10.1 起）。</summary>
public sealed record StoredTokens(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt,
    bool IsPersistent,
    int UserId);

/// <summary>
/// Token 存放（localStorage／sessionStorage，見 <c>wwwroot/js/client-auth.js</c>）、跨分頁刷新鎖與分頁間溝通（0.10.8 起）。
/// 讀不到或格式不對一律當成沒有 Token。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "BL0016:JS interop call is not guarded with a try/catch block", Justification = "JS 互通的薄包裝：例外刻意交給呼叫端處理，在這裡吞掉反而讓呼叫端無從得知失敗（.NET 11 起的分析器）。")]
public class TokenStore : IAsyncDisposable
{
    /// <summary>WASM 前端專案的 wwwroot 直接掛在網站根目錄（不是 <c>_content/</c>，那是 Razor 類別庫才有的路徑）。</summary>
    private const string ModulePath = "./js/client-auth.js";
    private readonly IJSRuntime jsRuntime;
    private IJSObjectReference? module;

    public TokenStore(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

    public virtual async Task<StoredTokens?> ReadAsync()
    {
        try
        {
            var json = await (await ModuleAsync()).InvokeAsync<string?>("read");
            return Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public virtual async Task WriteAsync(StoredTokens tokens)
        => await (await ModuleAsync()).InvokeVoidAsync("write", Serialize(tokens), tokens.IsPersistent);

    public virtual async Task ClearAsync()
        => await (await ModuleAsync()).InvokeVoidAsync("clear");

    /// <summary>在跨分頁的鎖內呼叫 <paramref name="target"/> 的 <paramref name="method"/>（JSInvokable）。</summary>
    public virtual async Task<bool> RunLockedAsync<T>(DotNetObjectReference<T> target, string method)
        where T : class
        => await (await ModuleAsync()).InvokeAsync<bool>("runLocked", "myproject-token-refresh", target, method);

    /// <summary>
    /// 向其他已開的分頁要一組 Token（0.10.8 起；沒勾「記住我」時 Token 在 sessionStorage，新分頁讀不到）。
    /// 已開的分頁在刷新鎖內以自己的刷新 Token 開一串新的交過來；<paramref name="timeout"/> 內沒人回應回 null。
    /// </summary>
    public virtual async Task<StoredTokens?> RequestFromOtherTabsAsync(TimeSpan timeout)
    {
        try
        {
            return Parse(await (await ModuleAsync()).InvokeAsync<string?>("requestTokens", (int)timeout.TotalMilliseconds));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 監聽其他分頁（0.10.8 起）：localStorage 的 Token 換了或被清掉（<c>OnStorageChangedAsync</c>）、別的分頁登出（<c>OnLogoutBroadcastAsync</c>）、
    /// 新分頁要 Token（<c>ForkForAnotherTabAsync</c>）。回傳的物件用來取消；瀏覽器不支援時回 null。
    /// </summary>
    public virtual async Task<IAsyncDisposable?> ListenAsync<T>(DotNetObjectReference<T> target)
        where T : class
    {
        var listener = await (await ModuleAsync()).InvokeAsync<IJSObjectReference?>("listen", target);
        return listener is null ? null : new Listener(listener);
    }

    /// <summary>通知其他分頁「有人登入了」（0.10.8 起）：顯示另一個人的分頁會改用這次的登入。</summary>
    public virtual async Task BroadcastSignedInAsync(int userId)
        => await (await ModuleAsync()).InvokeVoidAsync("broadcastSignedIn", userId);

    /// <summary>只清這個分頁（sessionStorage）的 Token。</summary>
    public virtual async Task ClearSessionAsync()
        => await (await ModuleAsync()).InvokeVoidAsync("clearSession");

    /// <summary>通知其他分頁「這次登入已登出」。</summary>
    public virtual async Task BroadcastLogoutAsync()
        => await (await ModuleAsync()).InvokeVoidAsync("broadcastLogout");

    /// <summary>登入頁用的其他鍵（待驗票證、記住裝置 Token、Google 驗證值）。<paramref name="persistent"/>：localStorage，否則 sessionStorage。</summary>
    public virtual async Task<string?> GetItemAsync(string key, bool persistent)
        => await (await ModuleAsync()).InvokeAsync<string?>("getItem", persistent ? "local" : "session", key);

    public virtual async Task SetItemAsync(string key, string value, bool persistent)
        => await (await ModuleAsync()).InvokeVoidAsync("setItem", persistent ? "local" : "session", key, value);

    public virtual async Task RemoveItemAsync(string key, bool persistent)
        => await (await ModuleAsync()).InvokeVoidAsync("removeItem", persistent ? "local" : "session", key);

    /// <summary>storage 裡的 JSON（或其他分頁交來的）轉成 Token；空的或格式錯回 null。</summary>
    public static StoredTokens? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredTokens>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(StoredTokens tokens) => JsonSerializer.Serialize(tokens);

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // 頁面已關閉。
            }
        }

        GC.SuppressFinalize(this);
    }

    private async Task<IJSObjectReference> ModuleAsync()
        => module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);

    private sealed class Listener(IJSObjectReference reference) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await reference.InvokeVoidAsync("dispose");
                await reference.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // 頁面已關閉。
            }
        }
    }
}
