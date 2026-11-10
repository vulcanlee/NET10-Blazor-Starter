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
/// Token 存放（localStorage／sessionStorage，見 <c>wwwroot/js/client-auth.js</c>）與跨分頁刷新鎖。
/// 讀不到或格式不對一律當成沒有 Token（之後會以網頁 Cookie 重新換一組）。
/// </summary>
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
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<StoredTokens>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public virtual async Task WriteAsync(StoredTokens tokens)
        => await (await ModuleAsync()).InvokeVoidAsync("write", JsonSerializer.Serialize(tokens), tokens.IsPersistent);

    public virtual async Task ClearAsync()
        => await (await ModuleAsync()).InvokeVoidAsync("clear");

    /// <summary>在跨分頁的鎖內呼叫 <paramref name="target"/> 的 <paramref name="method"/>（JSInvokable）。</summary>
    public virtual async Task<bool> RunLockedAsync<T>(DotNetObjectReference<T> target, string method)
        where T : class
        => await (await ModuleAsync()).InvokeAsync<bool>("runLocked", "myproject-token-refresh", target, method);

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
}
