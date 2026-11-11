using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Auth;

/// <summary>
/// WASM 前端的登入工作階段（0.10.1 起，WASM 遷移 M2；過渡期，M9 改為純 Token 登入）。
///
/// 流程：
/// <list type="number">
/// <item>啟動時讀瀏覽器存的 Token；沒有就以網頁登入的 Cookie 呼叫 <c>/api/v1/Auth/bridge</c> 換一組，依「記住我」存 localStorage 或 sessionStorage。</item>
/// <item>以 <c>/api/v1/Auth/me</c> 取得身分；必須先改密碼或設定兩步驟驗證時，整頁導到那兩個頁面（目前還是 Server 頁面）。</item>
/// <item>存取 Token 快到期或 API 回 401 時刷新：多個分頁以 Web Locks 排隊，拿到鎖先重讀 storage（別的分頁可能已經換好）；
/// 刷新失敗（例如換出的 Token 已到 Cookie 的期限）再以 Cookie 重換一次；都不行才回登入頁。</item>
/// </list>
/// 伺服器回 429（請求過於頻繁，0.10.5 起）是暫時的：不清 Token、不回登入頁，改由外框顯示 <see cref="ThrottledMessage"/> 請使用者稍後重新整理。
/// 以前會一路試到「重新登入」，結果短時間內快速切換頁面的人被丟回登入頁（M6 實機發現）。
/// </summary>
public class ClientSession : IDisposable
{
    public const string ClientHeaderName = "X-MyProject-Client";

    /// <summary>被限流時給使用者看的訊息（與伺服器 429 回應的訊息相同）。</summary>
    public const string ThrottledMessage = "請求過於頻繁，請稍後再試。";
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly TokenStore store;
    private readonly NavigationManager navigation;
    private readonly ILogger<ClientSession> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DotNetObjectReference<ClientSession>? selfReference;
    private StoredTokens? tokens;

    public ClientSession(HttpClient http, TokenStore store, NavigationManager navigation, ILogger<ClientSession> logger)
    {
        this.http = http;
        this.store = store;
        this.navigation = navigation;
        this.logger = logger;
    }

    /// <summary>
    /// 最近一次取得或刷新 Token、讀取身分時被伺服器限流（429）。這時 <see cref="InitializeAsync"/> 回 false 但不導頁，
    /// <see cref="RefreshAsync"/> 回 false 時呼叫端也不要叫使用者重新登入。
    /// </summary>
    public bool IsThrottled { get; private set; }

    /// <summary>目前的身分（<c>/me</c>）；初始化成功前為 null。</summary>
    public SessionInfoDto? Info { get; private set; }

    /// <summary>
    /// 這個頁面是誰的網頁登入（主機從 Cookie 取出、經 <c>ClientRoutes</c> 傳進來；0＝不知道）。
    /// 瀏覽器存的 Token 只有屬於這個人才沿用，否則丟掉、重新以 Cookie 換 —— 同一個瀏覽器換人登入時，不會沿用上一個人的 Token
    /// （不能只靠登入頁清除 storage：從重設密碼信等頁面進入登入頁時，Blazor 的導覽不一定會執行那支 script）。
    /// </summary>
    public int ExpectedUserId { get; set; }

    /// <summary>身分資料變了（例如在個人資料頁改了姓名），外框要重畫右上角。</summary>
    public event Action? Changed;

    /// <summary>取得 Token 與身分；需要改密碼、設定兩步驟驗證或重新登入時會整頁導走並回 false。</summary>
    public virtual async Task<bool> InitializeAsync()
    {
        tokens = await store.ReadAsync();
        if (tokens is not null && !BelongsToThisLogin(tokens))
        {
            logger.LogInformation("Stored tokens belong to another login; exchanging the web session again.");
            tokens = null;
            await store.ClearAsync();
        }

        IsThrottled = false;
        if (tokens is null && !await BridgeAsync())
        {
            return await SignInAgainUnlessThrottledAsync();
        }

        var me = await LoadMeAsync();
        if (me is null && !IsThrottled && await RefreshAsync())
        {
            me = await LoadMeAsync();
        }

        if (me is null)
        {
            return await SignInAgainUnlessThrottledAsync();
        }

        Info = me;
        if (me.MustChangePassword)
        {
            navigation.NavigateTo("/ChangePassword", forceLoad: true);
            return false;
        }

        if (me.RequiresTwoFactorSetup)
        {
            navigation.NavigateTo("/TwoFactorSetup", forceLoad: true);
            return false;
        }

        return true;
    }

    /// <summary>可用的存取 Token；快到期時先刷新。</summary>
    public virtual async Task<string?> GetAccessTokenAsync()
    {
        if (tokens is not null && tokens.AccessTokenExpiresAt <= DateTime.UtcNow + RefreshAhead)
        {
            await RefreshAsync();
        }

        return tokens?.AccessToken;
    }

    /// <summary>刷新（同一個分頁內與跨分頁都排隊）；失敗時以 Cookie 重換一組。成功回 true；被限流時回 false 並設 <see cref="IsThrottled"/>。</summary>
    public virtual async Task<bool> RefreshAsync()
    {
        IsThrottled = false;
        await refreshGate.WaitAsync();
        try
        {
            selfReference ??= DotNetObjectReference.Create(this);
            return await store.RunLockedAsync(selfReference, nameof(RefreshUnderLockAsync));
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>在跨分頁的鎖內執行（由 <c>client-auth.js</c> 呼叫）。</summary>
    [JSInvokable]
    public async Task<bool> RefreshUnderLockAsync()
    {
        var stored = await store.ReadAsync();
        if (stored is not null && BelongsToThisLogin(stored) && stored.RefreshToken != tokens?.RefreshToken && stored.AccessTokenExpiresAt > DateTime.UtcNow + RefreshAhead)
        {
            // 別的分頁剛換好了，直接沿用。
            tokens = stored;
            return true;
        }

        try
        {
            if (tokens is not null)
            {
                var response = await http.PostAsJsonAsync("api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });
                if (response.IsSuccessStatusCode && await ReadTokensAsync(response) is { } refreshed)
                {
                    await SaveAsync(refreshed);
                    return true;
                }

                if (WasThrottled(response))
                {
                    // 刷新 Token 沒被用掉（伺服器在進到端點之前就擋下），稍後還能用；不要改用 Cookie 重換，那也會被擋。
                    return false;
                }

                logger.LogInformation("Refreshing the token failed with HTTP {Status}; trying the web session.", (int)response.StatusCode);
            }

            return await BridgeAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // 連不上伺服器：這裡是經 JS（Web Locks）回呼進來的，例外不能往外丟（會變成 JSException、整頁出錯）。
            // 回 false 讓呼叫端照「暫時無法連線」處理。
            logger.LogWarning(ex, "Refreshing the token failed because the server could not be reached.");
            return false;
        }
    }

    /// <summary>
    /// 從 WASM 頁面登出：清掉瀏覽器存的 Token，交給網頁登出頁（清 Cookie、寫一筆登出稽核，並依登入工作階段撤銷這次登入換出的所有 Token）。
    /// 遷移期間不另外呼叫 <c>/api/v1/Auth/logout</c>：網頁登出頁已經會撤銷，兩邊都做會留下兩筆登出稽核。M9 改純 Token 登入後改呼叫 API。
    /// </summary>
    public virtual async Task LogoutAsync()
    {
        tokens = null;
        await store.ClearAsync();
        navigation.NavigateTo("/Auths/Logout", forceLoad: true);
    }

    /// <summary>Token 與 Cookie 都不能用了：清掉 Token、回登入頁。</summary>
    public virtual async Task SignInAgainAsync()
    {
        tokens = null;
        await store.ClearAsync();
        navigation.NavigateTo("/Auths/Login", forceLoad: true);
    }

    /// <summary>自己在個人資料頁改了姓名：更新右上角。</summary>
    public void UpdateName(string name)
    {
        if (Info is not null)
        {
            Info.Name = name;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        selfReference?.Dispose();
        refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> BridgeAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/Auth/bridge");
        request.Headers.Add(ClientHeaderName, "1");
        var response = await http.SendAsync(request);
        if (response.IsSuccessStatusCode && await ReadTokensAsync(response) is { } bridged)
        {
            await SaveAsync(bridged);
            return true;
        }

        WasThrottled(response);

        logger.LogInformation("Exchanging the web session for tokens failed with HTTP {Status}.", (int)response.StatusCode);
        return false;
    }

    private async Task<SessionInfoDto?> LoadMeAsync()
    {
        if (tokens is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/Auth/me");
        request.Headers.Authorization = new("Bearer", tokens.AccessToken);
        try
        {
            var response = await http.SendAsync(request);
            WasThrottled(response);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync<ApiResult<SessionInfoDto>>())?.Data
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Loading the session failed because the server could not be reached.");
            return null;
        }
    }

    private static async Task<StoredTokens?> ReadTokensAsync(HttpResponseMessage response)
    {
        var data = (await response.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())?.Data;
        return data is null
            ? null
            : new StoredTokens(data.AccessToken, data.AccessTokenExpiresAt, data.RefreshToken, data.RefreshTokenExpiresAt, data.IsPersistent, data.User.Id);
    }

    private async Task SaveAsync(StoredTokens value)
    {
        tokens = value;
        await store.WriteAsync(value);

        // 這個頁面一開始是 A，換回來的卻是 B（同一個瀏覽器另一個分頁換人登入了）：畫面上的資料都是 A 的，整頁重新載入成 B。
        if (Info is not null && value.UserId != Info.Id)
        {
            logger.LogInformation("The signed-in user changed in another tab; reloading the page.");
            navigation.Refresh(forceReload: true);
        }
    }

    /// <summary>被限流時只記下來（外框顯示訊息）；其他失敗才是 Token 與 Cookie 真的不能用，回登入頁。</summary>
    private async Task<bool> SignInAgainUnlessThrottledAsync()
    {
        if (IsThrottled)
        {
            logger.LogWarning("The server is throttling this user; showing a retry message instead of signing in again.");
            return false;
        }

        await SignInAgainAsync();
        return false;
    }

    private bool WasThrottled(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            IsThrottled = true;
        }

        return IsThrottled;
    }

    /// <summary>這組 Token 是不是這次網頁登入的人的（不知道是誰時一律接受，由 <c>/me</c> 決定身分）。</summary>
    private bool BelongsToThisLogin(StoredTokens value) => ExpectedUserId <= 0 || value.UserId == ExpectedUserId;
}
