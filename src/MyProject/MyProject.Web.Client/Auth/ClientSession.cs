using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Web.Components.Auths;

namespace MyProject.Web.Client.Auth;

/// <summary>
/// WASM 前端的登入工作階段（0.10.1 起，WASM 遷移 M2；0.10.8 起純 Token 登入，M9）。
///
/// 流程：
/// <list type="number">
/// <item>啟動時讀瀏覽器存的 Token（勾「記住我」在 localStorage，否則 sessionStorage）；沒有就向已開的分頁要
/// （BroadcastChannel，對方在刷新鎖內以 <c>session/fork</c> 開一串新的交過來），<see cref="OtherTabsTimeout"/> 內沒人回應就帶 returnUrl 回登入頁。</item>
/// <item>以 <c>/api/v1/Auth/me</c> 取得身分；必須先改密碼或設定兩步驟驗證時，整頁導到那兩個頁面（已經在那一頁就不再導，否則無限重新載入）。
/// 帳號沒有主要角色（伺服器回 403 <see cref="ApiErrorCodes.NoPrimaryRole"/>）時登出，與 Server 版相同。</item>
/// <item>存取 Token 快到期或 API 回 401 時刷新：多個分頁以 Web Locks 排隊，拿到鎖先重讀 storage（別的分頁可能已經換好）；刷新失敗才回登入頁。</item>
/// <item>監聽其他分頁：localStorage 的 Token 被清掉（別的分頁登出）就回登入頁、換成別人就整頁重新載入；收到「已登出」廣播就重新確認自己的登入。</item>
/// </list>
/// 伺服器回 429（請求過於頻繁，0.10.5 起）是暫時的：不清 Token、不回登入頁，改由外框顯示 <see cref="ThrottledMessage"/> 請使用者稍後重新整理。
/// </summary>
public class ClientSession : IDisposable
{
    /// <summary>被限流時給使用者看的訊息（與伺服器 429 回應的訊息相同）。</summary>
    public const string ThrottledMessage = "請求過於頻繁，請稍後再試。";

    /// <summary>必須先變更密碼時的頁面。</summary>
    public const string ChangePasswordPath = "/ChangePassword";

    /// <summary>必須先設定兩步驟驗證時的頁面。</summary>
    public const string TwoFactorSetupPath = "/TwoFactorSetup";

    /// <summary>登入頁。</summary>
    public const string LoginPath = "/Auths/Login";

    /// <summary>新分頁向已開的分頁要 Token 時最多等多久（沒人回應就回登入頁）。</summary>
    public static readonly TimeSpan OtherTabsTimeout = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RefreshAhead = TimeSpan.FromSeconds(30);

    private readonly HttpClient http;
    private readonly TokenStore store;
    private readonly NavigationManager navigation;
    private readonly ILogger<ClientSession> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DotNetObjectReference<ClientSession>? selfReference;
    private IAsyncDisposable? listener;
    private StoredTokens? tokens;
    private bool hasNoPrimaryRole;
    private bool isLeaving;

    public ClientSession(HttpClient http, TokenStore store, NavigationManager navigation, ILogger<ClientSession> logger)
    {
        this.http = http;
        this.store = store;
        this.navigation = navigation;
        this.logger = logger;
    }

    /// <summary>
    /// 最近一次刷新 Token、讀取身分時被伺服器限流（429）。這時 <see cref="InitializeAsync"/> 回 false 但不導頁，
    /// <see cref="RefreshAsync"/> 回 false 時呼叫端也不要叫使用者重新登入。
    /// </summary>
    public bool IsThrottled { get; private set; }

    /// <summary>目前的身分（<c>/me</c>）；初始化成功前為 null。</summary>
    public SessionInfoDto? Info { get; private set; }

    /// <summary>身分資料變了（例如在個人資料頁改了姓名），外框要重畫右上角。</summary>
    public event Action? Changed;

    /// <summary>取得 Token 與身分；需要登入、改密碼或設定兩步驟驗證時會整頁導走並回 false。</summary>
    public virtual async Task<bool> InitializeAsync()
    {
        IsThrottled = false;
        tokens = await store.ReadAsync();
        if (tokens is null && await RequestFromOtherTabsAsync() is { } handed)
        {
            logger.LogInformation("Received tokens from another tab.");
            await SaveAsync(handed);
        }

        if (tokens is null)
        {
            await SignInAgainAsync();
            return false;
        }

        var me = await LoadMeAsync();
        if (me is null && !IsThrottled && !hasNoPrimaryRole && await RefreshAsync())
        {
            me = await LoadMeAsync();
        }

        if (me is null && hasNoPrimaryRole)
        {
            // 沒有主要角色的帳號不能使用網頁（與 Server 版的行為相同）：登出。
            logger.LogWarning("The account has no primary role; signing out.");
            await LogoutAsync();
            return false;
        }

        if (me is null)
        {
            return await SignInAgainUnlessThrottledAsync();
        }

        Info = me;
        await StartListeningAsync();

        // 已經在設定頁就留下來（0.10.8 起那兩頁也在 WASM，再導一次會無限重新載入）；兩頁互不導向（必須改密碼時 RequiresTwoFactorSetup 一律為 false）。
        if (me.MustChangePassword && !IsCurrentPage(ChangePasswordPath))
        {
            navigation.NavigateTo(ChangePasswordPath, forceLoad: true);
            return false;
        }

        if (me.RequiresTwoFactorSetup && !IsCurrentPage(TwoFactorSetupPath))
        {
            navigation.NavigateTo(TwoFactorSetupPath, forceLoad: true);
            return false;
        }

        return true;
    }

    /// <summary>目前網址是不是這一頁（不分大小寫，忽略 query 與錨點）。</summary>
    public bool IsCurrentPage(string path)
        => string.Equals(
            ClientRouteTable.Normalize(navigation.ToBaseRelativePath(navigation.Uri)),
            ClientRouteTable.Normalize(path),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 登入成功（登入頁、兩步驟驗證頁、Google 兌換），或改密碼、啟用／停用兩步驟驗證、管理員改到自己之後 API 回的新 Token：存起來。
    /// 存放位置照「記住我」（localStorage 或 sessionStorage）。
    /// </summary>
    public async Task ApplyTokensAsync(TokenResponseDto value)
        => await SaveAsync(ToStored(value));

    /// <summary>
    /// 登入成功（登入頁、兩步驟驗證頁、Google 兌換）：存起來並通知其他分頁有人登入了（0.10.8 獨立審查）——
    /// 顯示另一個人的分頁改用這次的登入，同一個瀏覽器只有一個身分，新分頁就不會拿到另一個人的 Token。
    /// </summary>
    public async Task SignedInAsync(TokenResponseDto value)
    {
        await ApplyTokensAsync(value);
        await TryAsync(() => store.BroadcastSignedInAsync(value.User.Id));
    }

    /// <summary>別的分頁有人登入了：顯示的是另一個人就丟掉這個分頁的 Token、重新載入（改用那次的登入）。</summary>
    [JSInvokable]
    public async Task OnSignedInBroadcastAsync(int userId)
    {
        if (isLeaving || tokens is null || tokens.UserId == userId)
        {
            return;
        }

        logger.LogInformation("Another user signed in in this browser; switching this tab to that login.");
        tokens = null;
        await TryAsync(store.ClearSessionAsync);
        navigation.Refresh(forceReload: true);
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

    /// <summary>刷新（同一個分頁內與跨分頁都排隊）。成功回 true；被限流時回 false 並設 <see cref="IsThrottled"/>。</summary>
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
        if (stored is not null && stored.RefreshToken != tokens?.RefreshToken && stored.AccessTokenExpiresAt > DateTime.UtcNow + RefreshAhead)
        {
            // 別的分頁剛換好了（記住我的 Token 是共用的），直接沿用。
            await SaveAsync(stored, write: false);
            return true;
        }

        if (tokens is null)
        {
            return false;
        }

        try
        {
            var response = await http.PostAsJsonAsync("api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });
            if (response.IsSuccessStatusCode && await ReadTokensAsync(response) is { } refreshed)
            {
                await SaveAsync(refreshed);
                return true;
            }

            WasThrottled(response);
            logger.LogInformation("Refreshing the token failed with HTTP {Status}.", (int)response.StatusCode);
            return false;
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
    /// 新開的分頁要 Token（由 <c>client-auth.js</c> 在刷新鎖內呼叫）：以自己的刷新 Token 呼叫 <c>session/fork</c> 開一串新的交出去（JSON），
    /// 自己這一串不受影響。只有沒勾「記住我」的分頁需要回應（記住我的 Token 在 localStorage，新分頁自己讀得到）。
    /// </summary>
    [JSInvokable]
    public async Task<string?> ForkForAnotherTabAsync()
    {
        if (tokens is not { IsPersistent: false } current || isLeaving)
        {
            return null;
        }

        try
        {
            var response = await http.PostAsJsonAsync("api/v1/Auth/session/fork", new RefreshTokenRequestDto { RefreshToken = current.RefreshToken });
            if (response.IsSuccessStatusCode && await ReadTokensAsync(response) is { } forked)
            {
                logger.LogInformation("Handed a new token family to another tab.");
                return TokenStore.Serialize(forked);
            }

            logger.LogInformation("Forking the session for another tab failed with HTTP {Status}.", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Forking the session for another tab failed because the server could not be reached.");
        }

        return null;
    }

    /// <summary>
    /// localStorage 的 Token 被別的分頁改了（由 <c>client-auth.js</c> 的 storage 事件呼叫）：
    /// 換成別人 → 整頁重新載入（畫面上的資料都是原本那個人的）；被清掉而自己用的就是那一份 → 已在別的分頁登出，回登入頁；
    /// 同一個人刷新了 → 沿用新的。
    /// </summary>
    [JSInvokable]
    public async Task OnStorageChangedAsync(string? newValue)
    {
        if (isLeaving)
        {
            return;
        }

        var changed = TokenStore.Parse(newValue);
        if (changed is null)
        {
            if (tokens is { IsPersistent: true })
            {
                logger.LogInformation("The remembered login was signed out in another tab.");
                tokens = null;
                Leave(LoginUrl());
            }

            return;
        }

        if (Info is not null && changed.UserId != Info.Id)
        {
            logger.LogInformation("Another user signed in in another tab; reloading the page.");
            isLeaving = true;
            navigation.Refresh(forceReload: true);
            return;
        }

        if (tokens is { IsPersistent: true } && changed.IsPersistent)
        {
            await SaveAsync(changed, write: false);
        }
    }

    /// <summary>別的分頁登出了（BroadcastChannel）：這次登入的每一串都已撤銷的話，刷新會失敗、回登入頁；別人的登出則不受影響。</summary>
    [JSInvokable]
    public async Task OnLogoutBroadcastAsync()
    {
        if (isLeaving || tokens is null)
        {
            return;
        }

        if (!await RefreshAsync() && !IsThrottled)
        {
            await SignInAgainAsync();
        }
    }

    /// <summary>
    /// 登出（0.10.8 起）：呼叫 <c>/api/v1/Auth/logout</c> 撤銷這次登入的每一串（每個分頁各一串，以登入識別串起來），
    /// 清掉瀏覽器存的 Token、通知其他分頁，再回登入頁。連不上伺服器時照樣清掉（伺服器的 Token 到期自然失效）。
    /// </summary>
    public virtual async Task LogoutAsync()
    {
        isLeaving = true;
        tokens ??= await store.ReadAsync();
        if (tokens is { } current)
        {
            try
            {
                await http.PostAsJsonAsync("api/v1/Auth/logout", new RefreshTokenRequestDto { RefreshToken = current.RefreshToken });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Logging out on the server failed because the server could not be reached.");
            }
        }

        tokens = null;
        await store.ClearAsync();
        await TryAsync(store.BroadcastLogoutAsync);
        navigation.NavigateTo(LoginPath, forceLoad: true);
    }

    /// <summary>Token 不能用了（或根本沒有）：清掉、帶 returnUrl 回登入頁。</summary>
    public virtual async Task SignInAgainAsync()
    {
        tokens = null;
        await store.ClearAsync();
        Leave(LoginUrl());
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
        if (listener is not null)
        {
            _ = listener.DisposeAsync().AsTask();
        }

        selfReference?.Dispose();
        refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>登入頁的網址，帶上目前的頁面（站內、不是登入相關頁面時）讓登入後回來。</summary>
    internal string LoginUrl()
    {
        var current = "/" + navigation.ToBaseRelativePath(navigation.Uri);
        var path = current.Split('?', '#')[0];
        if (path == "/" || path.StartsWith("/Auths", StringComparison.OrdinalIgnoreCase))
        {
            return LoginPath;
        }

        return $"{LoginPath}?returnUrl={Uri.EscapeDataString(ReturnUrlGuard.Sanitize(current))}";
    }

    private void Leave(string url)
    {
        isLeaving = true;
        navigation.NavigateTo(url, forceLoad: true);
    }

    private async Task<StoredTokens?> RequestFromOtherTabsAsync()
    {
        try
        {
            return await store.RequestFromOtherTabsAsync(OtherTabsTimeout);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Asking other tabs for tokens is not available.");
            return null;
        }
    }

    private async Task StartListeningAsync()
    {
        if (listener is not null)
        {
            return;
        }

        try
        {
            selfReference ??= DotNetObjectReference.Create(this);
            listener = await store.ListenAsync(selfReference);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Listening to other tabs is not available.");
        }
    }

    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            logger.LogDebug(ex, "A browser call failed; continuing.");
        }
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
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var denied = await response.Content.ReadFromJsonAsync<ApiResult<SessionInfoDto>>();
                hasNoPrimaryRole = denied?.ErrorCode == ApiErrorCodes.NoPrimaryRole;
                return null;
            }

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
        return data is null ? null : ToStored(data);
    }

    private static StoredTokens ToStored(TokenResponseDto data)
        => new(data.AccessToken, data.AccessTokenExpiresAt, data.RefreshToken, data.RefreshTokenExpiresAt, data.IsPersistent, data.User.Id);

    private async Task SaveAsync(StoredTokens value, bool write = true)
    {
        tokens = value;
        if (write)
        {
            await store.WriteAsync(value);
        }

        // 這個頁面一開始是 A，換回來的卻是 B（同一個瀏覽器另一個分頁換人登入了）：畫面上的資料都是 A 的，整頁重新載入成 B。
        if (Info is not null && value.UserId != Info.Id)
        {
            logger.LogInformation("The signed-in user changed in another tab; reloading the page.");
            isLeaving = true;
            navigation.Refresh(forceReload: true);
        }
    }

    /// <summary>被限流時只記下來（外框顯示訊息）；其他失敗才是 Token 真的不能用，回登入頁。</summary>
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
}
