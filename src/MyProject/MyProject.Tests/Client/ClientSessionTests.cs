using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Client;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Client.Layout;

namespace MyProject.Tests.Client;

/// <summary>
/// WASM 前端的登入工作階段與 API 呼叫（0.10.1 起，WASM 遷移 M2；0.10.8 起純 Token 登入）：Token 的取得（storage、向已開的分頁要）、刷新、多分頁、登出、失敗時的退路與導頁。
/// 用假的 HTTP 回應與假的 Token 存放（瀏覽器的 storage 與 Web Locks 由實機驗證涵蓋）。
/// </summary>
public sealed class ClientSessionTests
{
    [Fact]
    public async Task Initialize_WithStoredTokens_ShouldLoadTheSession()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, Account = "alice" }));
        var (session, store, navigation) = Create(handler);

        Assert.True(await session.InitializeAsync());

        Assert.Equal("alice", session.Info!.Account);
        Assert.Equal("Bearer a1", handler.Requests.Single(x => x.Path == "api/v1/Auth/me").Authorization);
        Assert.Null(navigation.NavigatedTo);
        Assert.True(store.Listening);
    }

    /// <summary>沒有 Token、也沒有已開的分頁給（1 秒內沒人回應）：回登入頁，帶上原本要去的頁面。</summary>
    [Fact]
    public async Task Initialize_WithoutTokens_AndNoOpenTab_ShouldGoToTheLoginPageWithTheReturnUrl()
    {
        var handler = new FakeHandler();
        var (session, store, navigation) = Create(handler, "projects?deleted=1");

        Assert.False(await session.InitializeAsync());

        Assert.Equal("/Auths/Login?returnUrl=%2Fprojects%3Fdeleted%3D1", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);
        Assert.Equal(ClientSession.OtherTabsTimeout, store.AskedOtherTabsFor);
        Assert.Empty(handler.Requests);
    }

    /// <summary>沒勾「記住我」的 Token 在 sessionStorage，新分頁讀不到：向已開的分頁要到一組（對方以 session/fork 開的新一串），存進自己的分頁。</summary>
    [Fact]
    public async Task Initialize_InANewTab_ShouldTakeTokensFromAnOpenTab()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, navigation) = Create(handler);
        store.FromOtherTab = new StoredTokens("a5", DateTime.UtcNow.AddMinutes(15), "r5", DateTime.UtcNow.AddHours(8), false, 7);

        Assert.True(await session.InitializeAsync());

        Assert.Equal("r5", store.Stored!.RefreshToken);
        Assert.False(store.Stored.IsPersistent);
        Assert.Equal("Bearer a5", handler.Requests.Single(x => x.Path == "api/v1/Auth/me").Authorization);
        Assert.Null(navigation.NavigatedTo);
    }

    /// <summary>已開的分頁回應新分頁：以自己的刷新 Token 呼叫 session/fork，交出新的一串；記住我的分頁不回應（新分頁讀得到 localStorage）。</summary>
    [Fact]
    public async Task ForkForAnotherTab_ShouldHandOverANewFamily_OnlyForSessionLogins()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: false));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/session/fork", HttpStatusCode.OK, Tokens("a6", "r6", persistent: false));
        var (session, store, _) = Create(handler);
        await session.InitializeAsync();

        var handed = TokenStore.Parse(await session.ForkForAnotherTabAsync());

        Assert.Equal("r6", handed!.RefreshToken);
        Assert.Equal("r1", store.Stored!.RefreshToken);

        var persistentHandler = new FakeHandler();
        persistentHandler.SignedIn(Tokens("a1", "r1", persistent: true));
        persistentHandler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (persistentSession, _, _) = Create(persistentHandler);
        await persistentSession.InitializeAsync();
        Assert.Null(await persistentSession.ForkForAnotherTabAsync());
        Assert.DoesNotContain(persistentHandler.Requests, x => x.Path == "api/v1/Auth/session/fork");
    }

    [Fact]
    public async Task Initialize_WhenTheTokensNoLongerWork_ShouldClearThemAndGoBackToTheLoginPage()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.Unauthorized, Fail());
        handler.On("api/v1/Auth/refresh", HttpStatusCode.Unauthorized, Fail());
        var (session, store, navigation) = Create(handler, "App");

        Assert.False(await session.InitializeAsync());

        Assert.Equal("/Auths/Login?returnUrl=%2FApp", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);
        Assert.True(store.Cleared);
    }

    [Theory]
    [InlineData(true, false, "/ChangePassword")]
    [InlineData(false, true, "/TwoFactorSetup")]
    public async Task Initialize_WhenSetupIsForced_ShouldGoToThatPage(bool mustChangePassword, bool requiresTwoFactor, string expected)
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: false));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, MustChangePassword = mustChangePassword, RequiresTwoFactorSetup = requiresTwoFactor }));
        var (session, _, navigation) = Create(handler);

        Assert.False(await session.InitializeAsync());

        Assert.Equal(expected, navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);
    }

    [Fact]
    public async Task Refresh_WhenAnotherTabAlreadyRefreshed_ShouldUseItsTokensWithoutCallingTheServer()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, _) = Create(handler);
        await session.InitializeAsync();

        // 別的分頁換好了一組新的。
        store.Stored = new StoredTokens("a2", DateTime.UtcNow.AddMinutes(30), "r2", DateTime.UtcNow.AddDays(7), true, 7);

        Assert.True(await session.RefreshAsync());

        Assert.Equal("a2", await session.GetAccessTokenAsync());
        Assert.DoesNotContain(handler.Requests, x => x.Path == "api/v1/Auth/refresh");
    }

    [Fact]
    public async Task ApiClient_WhenTheRefreshTokenIsRejected_ShouldSendTheUserToTheLoginPage()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.Unauthorized, Fail());
        handler.On("api/v1/profile", HttpStatusCode.Unauthorized, Fail());
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.GetAsync<ProfileDto>("api/v1/profile");

        Assert.Equal(401, result.StatusCode);
        Assert.Equal("/Auths/Login?returnUrl=%2FProfile", navigation.NavigatedTo);
        Assert.True(store.Cleared);
    }

    /// <summary>
    /// 0.10.8 起不再有「這次網頁登入是誰」可比對（ExpectedUserId 隨 Cookie 移除）：別的分頁換人登入（寫進 localStorage）時，
    /// 這個分頁收到 storage 事件就整頁重新載入成那個人；被清掉而自己用的就是那一份就回登入頁；同一個人刷新了就沿用。
    /// </summary>
    [Fact]
    public async Task StorageEvents_ShouldFollowLogoutAndUserChangesInOtherTabs()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();

        await session.OnStorageChangedAsync(TokenStore.Serialize(new StoredTokens("a2", DateTime.UtcNow.AddMinutes(15), "r2", DateTime.UtcNow.AddDays(30), true, 7)));
        Assert.Null(navigation.NavigatedTo);
        Assert.Equal("a2", await session.GetAccessTokenAsync());

        await session.OnStorageChangedAsync(TokenStore.Serialize(new StoredTokens("a9", DateTime.UtcNow.AddMinutes(15), "r9", DateTime.UtcNow.AddDays(30), true, 9)));
        Assert.Equal("https://localhost/Profile", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);

        var (other, _, otherNavigation) = Create(handler);
        await other.InitializeAsync();
        await other.OnStorageChangedAsync(null);
        Assert.Equal("/Auths/Login?returnUrl=%2FProfile", otherNavigation.NavigatedTo);
    }

    /// <summary>
    /// 同一個瀏覽器只有一個身分（0.10.8 獨立審查，與 Server 版的 Cookie 相同）：登入成功時廣播「誰登入了」；
    /// 顯示另一個人的分頁丟掉自己這個分頁的 Token、整頁重新載入（改用那次的登入），同一個人則不受影響。
    /// </summary>
    [Fact]
    public async Task SignedInBroadcast_ShouldSwitchTabsOfAnotherUser_AndIgnoreTheSameUser()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: false));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();

        await session.OnSignedInBroadcastAsync(7);
        Assert.Null(navigation.NavigatedTo);
        Assert.False(store.SessionCleared);

        await session.OnSignedInBroadcastAsync(9);
        Assert.True(store.SessionCleared);
        Assert.True(navigation.ForceLoad);

        await session.SignedInAsync(Tokens("a5", "r5", persistent: false).Data!);
        Assert.Equal(7, store.BroadcastSignedIn);
    }

    /// <summary>別的分頁登出（廣播）：這次登入的每一串都已撤銷，刷新失敗就回登入頁；別人的登出不影響（刷新照常成功）。</summary>
    [Fact]
    public async Task LogoutBroadcast_ShouldOnlySignOutTabsOfTheRevokedLogin()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: false));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", (HttpStatusCode.OK, Tokens("a2", "r2", persistent: false)), (HttpStatusCode.Unauthorized, Fail()));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();

        await session.OnLogoutBroadcastAsync();
        Assert.Null(navigation.NavigatedTo);

        await session.OnLogoutBroadcastAsync();
        Assert.Equal("/Auths/Login?returnUrl=%2FProfile", navigation.NavigatedTo);
    }

    [Fact]
    public async Task Refresh_WhenAnotherTabSignedInAsAnotherUser_ShouldReloadAsThatUser()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();
        store.Stored = new StoredTokens("a9", DateTime.UtcNow.AddMinutes(30), "r9", DateTime.UtcNow.AddDays(7), true, 9);

        Assert.True(await session.RefreshAsync());

        // 畫面上的資料都是 7 號的：整頁重新載入成 9 號。
        Assert.Equal("https://localhost/Profile", navigation.NavigatedTo);
        Assert.DoesNotContain(handler.Requests, x => x.Path == "api/v1/Auth/refresh");
    }

    [Fact]
    public async Task Refresh_WhenTheServerCannotBeReached_ShouldReturnFalseInsteadOfThrowing()
    {
        // 刷新是從 JS（Web Locks）回呼進來的：例外往外丟會變成整頁錯誤，必須回 false。
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();
        handler.Offline = true;

        Assert.False(await session.RefreshAsync());

        Assert.Equal("r1", store.Stored!.RefreshToken);
        Assert.Null(navigation.NavigatedTo);
    }

    /// <summary>登出（0.10.8 起）：呼叫登出 API（撤銷這次登入的每一串）、清掉 Token、通知其他分頁，再回登入頁。</summary>
    [Fact]
    public async Task Logout_ShouldRevokeOnTheServer_ClearTheTokens_TellOtherTabs_AndGoToLogin()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/logout", HttpStatusCode.OK, Ok(new object()));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();

        await session.LogoutAsync();

        Assert.Contains(handler.Requests, x => x.Path == "api/v1/Auth/logout");
        Assert.True(store.Cleared);
        Assert.True(store.BroadcastLogout);
        Assert.Equal("/Auths/Login", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);

        // 登出中收到自己的廣播或 storage 事件不再處理。
        await session.OnStorageChangedAsync(null);
        await session.OnLogoutBroadcastAsync();
        Assert.Equal("/Auths/Login", navigation.NavigatedTo);
    }

    [Fact]
    public async Task ApiClient_On401_ShouldRefreshAndRetryOnce()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.OK, Tokens("a2", "r2", persistent: true));
        handler.On("api/v1/profile", (HttpStatusCode.Unauthorized, Fail()), (HttpStatusCode.OK, Ok(new ProfileDto { Account = "alice" })));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.GetAsync<ProfileDto>("api/v1/profile");

        Assert.True(result.Success);
        Assert.Equal("alice", result.Data!.Account);
        Assert.Equal(new[] { "Bearer a1", "Bearer a2" }, handler.Requests.Where(x => x.Path == "api/v1/profile").Select(x => x.Authorization ?? string.Empty));
    }

    [Fact]
    public async Task ApiClient_WhenThePasswordMustBeChanged_ShouldGoToTheChangePasswordPage()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var forbidden = ApiResult<object>.ForbiddenResult("請先變更密碼。");
        forbidden.ErrorCode = ApiErrorCodes.PasswordChangeRequired;
        handler.On("api/v1/profile", HttpStatusCode.Forbidden, forbidden);
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.GetAsync<ProfileDto>("api/v1/profile");

        Assert.False(result.Success);
        Assert.Equal("/ChangePassword", navigation.NavigatedTo);
    }

    [Fact]
    public async Task ApiClient_WhenTheServerCannotBeReached_ShouldReturnAFailureInsteadOfThrowing()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        handler.Offline = true;
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.PutAsync<object>("api/v1/profile/name", new UpdateProfileNameDto { Name = "x" });

        Assert.False(result.Success);
        Assert.Equal(ApiClient.OfflineMessage, result.ErrorMessage);
    }

    /// <summary>審查修正（0.10.2）：匯出被拒（例如超過筆數上限）時，畫面要顯示伺服器給的原因，不是「驗證失敗」這種分類字。</summary>
    [Fact]
    public async Task Download_WhenTheServerRefuses_ShouldReturnTheReason()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var refused = ApiResult<object>.ValidationError("符合條件的資料有 10001 筆，超過一次匯出的上限 10000 筆；請縮小搜尋或篩選條件後再匯出。");
        refused.TraceId = "ABCD1234";
        handler.On("api/v1/Category/export", HttpStatusCode.BadRequest, refused);
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.DownloadAsync("api/v1/Category/export", new { });

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.StartsWith("符合條件的資料有 10001 筆", result.Message);

        // 追蹤碼只給緊接著的那一則錯誤通知，取用一次就沒了。
        Assert.Equal("ABCD1234", ApiClient.TakeLastFailureTraceId());
        Assert.Null(ApiClient.TakeLastFailureTraceId());
    }

    /// <summary>審查補測（0.10.2）：異動歷程在斷線時不讓對話窗出錯：清單丟「讀不到」（對話窗顯示訊息），詳細內容回說明文字。</summary>
    [Fact]
    public async Task AuditHistorySource_WhenTheServerCannotBeReached_ShouldFailGracefully()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, IsAdmin = true }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        handler.Offline = true;
        var source = new ApiAuditHistorySource(new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance));

        var error = await Assert.ThrowsAsync<MyProject.Web.Components.Commons.AuditHistoryUnavailableException>(
            () => source.LoadAsync("Category", "1", 1, 10, opening: true));
        var content = await source.ReadContentAsync(new MyProject.Models.AdapterModel.AuditLogAdapterModel { Id = 1, ContentFile = "x" });

        Assert.Equal(ApiClient.OfflineMessage, error.Message);
        Assert.StartsWith("讀取詳細內容失敗。", content);
    }

    [Theory]
    [InlineData("/Profile", true)]
    [InlineData("profile", true)]
    [InlineData("/Profile?tab=1#x", true)]
    [InlineData("/%50rofile", true)]
    [InlineData("/Profile/", true)]
    [InlineData("/%E0%A4%A", false)]
    [InlineData("/categories", true)]
    [InlineData("/teams", true)]
    [InlineData("/Categories?deleted=1", true)]
    [InlineData("/projects", true)]
    [InlineData("/Projects?deleted=1", true)]
    [InlineData("/myusers", true)]
    [InlineData("/roleviews?deleted=1", true)]
    [InlineData("/audit-logs", true)]
    [InlineData("/email-logs", true)]
    [InlineData("/logs", true)]
    [InlineData("/log-level-setting", true)]
    [InlineData("/", true)]
    [InlineData("/ChangePassword", true)]
    [InlineData("/TwoFactorSetup", true)]
    [InlineData("/Error?trace=ABC", true)]
    [InlineData("/not-found", true)]
    [InlineData("/Auths/Login", true)]
    [InlineData("/Auths/TwoFactor", true)]
    [InlineData("/Auths/Logout", true)]
    [InlineData("/Auths/Google/Login", false)]
    [InlineData("/Auths/RefreshSession", false)]
    public void ClientRouteTable_ShouldKnowWhichPagesRunInWebAssembly(string path, bool expected)
        => Assert.Equal(expected, ClientRouteTable.IsClientRoute(path));

    [Fact]
    public void PageTitle_ShouldUseTheMenuFirst_ThenTheHelpTopic_LikeTheServerLayout()
    {
        var menu = new List<ClientMenuItem>
        {
            new() { Name = "資料定義", SubMenu = [new ClientMenuItem { Name = "分類清單", Url = "/categories" }] },
        };
        var topics = new List<HelpTopicDto> { new() { Route = "/Profile", Title = "個人資料" } };

        Assert.Equal("分類清單", ClientMainLayout.ResolvePageTitle(menu, topics, "/categories"));
        Assert.Equal("個人資料", ClientMainLayout.ResolvePageTitle(menu, topics, "/Profile"));
        Assert.Equal("系統首頁", ClientMainLayout.ResolvePageTitle(menu, topics, "/unknown"));
        Assert.Equal("個人資料", ClientPageHelpDialog.MatchTopic(topics, "profile?x=1")!.Title);
    }

    /// <summary>帶附件的存檔由瀏覽器直接送出（0.10.3 起）：401 時與一般 API 呼叫一樣刷新後重送一次，第二次帶新的 Token。</summary>
    [Fact]
    public async Task SendForm_On401_ShouldRefreshAndResendWithTheNewToken()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.OK, Tokens("a2", "r2", persistent: true));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);
        var tokens = new List<string?>();

        var result = await api.SendFormAsync<object>("api/v1/Project", token =>
        {
            tokens.Add(token);
            return Task.FromResult(tokens.Count == 1
                ? new RawResponse(401, JsonSerializer.Serialize(Fail()))
                : new RawResponse(200, JsonSerializer.Serialize(ApiResult<object>.SuccessResult(new object(), "新增專案成功"))));
        });

        Assert.True(result.Success);
        Assert.Equal(["a1", "a2"], tokens);
    }

    /// <summary>瀏覽器送不出去（離線、伺服器關閉）時 JS 丟例外：回失敗結果，畫面顯示提示而不是整頁出錯。</summary>
    [Fact]
    public async Task SendForm_WhenTheBrowserCannotReachTheServer_ShouldReturnAFailure()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.SendFormAsync<object>("api/v1/Project", _ => throw new JSException("TypeError: Failed to fetch"));

        Assert.False(result.Success);
        Assert.Equal(ApiClient.OfflineMessage, result.ErrorMessage);
    }

    /// <summary>IIS 擋下過大的請求時回 HTML 錯誤頁（不是 ApiResult）：顯示狀態碼，不丟例外。</summary>
    [Fact]
    public async Task SendForm_WhenTheResponseIsNotAnApiResult_ShouldReportTheStatus()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.SendFormAsync<object>("api/v1/Project", _ => Task.FromResult(new RawResponse(404, "<html>HTTP Error 404.13 - Not Found</html>")));

        Assert.False(result.Success);
        Assert.Contains("HTTP 404", result.UserMessage());
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    public void BellBadge_ShouldMatchTheServerBell(int count, string expected)
        => Assert.Equal(expected, ClientNotificationBell.BadgeText(count));

    private static (ClientSession Session, FakeTokenStore Store, TestNavigationManager Navigation) Create(FakeHandler handler, string page = "Profile")
    {
        var store = new FakeTokenStore { Stored = handler.Seed };
        var navigation = new TestNavigationManager(page);
        return (new ClientSession(handler.Client, store, navigation, NullLogger<ClientSession>.Instance), store, navigation);
    }

    // ---------- 0.10.8 起（WASM 遷移 M9a）：強制設定頁搬到 WASM、沒有主要角色、改到自己之後的新 Token、匿名呼叫 ----------

    /// <summary>
    /// 變更密碼、兩步驟驗證設定頁也在 WASM 之後：已經在那一頁就不可再整頁導過去（否則無限重新載入），外框照常顯示；
    /// 在另一頁時照舊導過去。網址大小寫與 query 不影響判斷。
    /// </summary>
    [Theory]
    [InlineData(true, false, "ChangePassword")]
    [InlineData(true, false, "changepassword?x=1")]
    [InlineData(false, true, "TwoFactorSetup")]
    public async Task Initialize_WhenAlreadyOnTheForcedSetupPage_ShouldStayThere(bool mustChangePassword, bool requiresTwoFactor, string page)
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: false));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, MustChangePassword = mustChangePassword, RequiresTwoFactorSetup = requiresTwoFactor }));
        var (session, _, navigation) = Create(handler, page);

        Assert.True(await session.InitializeAsync());

        Assert.Null(navigation.NavigatedTo);
    }

    /// <summary>必須改密碼時，人在兩步驟驗證設定頁也要先去改密碼（兩頁互不導向，與 Server 版相同）。</summary>
    [Fact]
    public async Task Initialize_WhenThePasswordMustChange_ButTheUserIsOnTheTwoFactorPage_ShouldGoToChangePassword()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: false));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, MustChangePassword = true }));
        var (session, _, navigation) = Create(handler, "TwoFactorSetup");

        Assert.False(await session.InitializeAsync());

        Assert.Equal("/ChangePassword", navigation.NavigatedTo);
    }

    /// <summary>設定頁上的 API 回 403「請先變更密碼」（例如外框某個呼叫）時，已在那一頁就不再導（M9 以前會無限重新載入）。</summary>
    [Fact]
    public async Task ApiClient_WhenForcedSetupIsReportedOnThatVeryPage_ShouldNotNavigateAgain()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, MustChangePassword = true }));
        var forbidden = ApiResult<object>.ForbiddenResult("請先變更密碼。");
        forbidden.ErrorCode = ApiErrorCodes.PasswordChangeRequired;
        handler.On("api/v1/profile", HttpStatusCode.Forbidden, forbidden);
        var (session, _, navigation) = Create(handler, "ChangePassword");
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.GetAsync<ProfileDto>("api/v1/profile");

        Assert.False(result.Success);
        Assert.Null(navigation.NavigatedTo);
    }

    /// <summary>沒有主要角色的帳號（伺服器回 403 NO_PRIMARY_ROLE）不能用網頁：登出，與 Server 版相同；不是叫人重新登入。</summary>
    [Fact]
    public async Task Initialize_WhenTheAccountHasNoPrimaryRole_ShouldSignOut()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        var denied = ApiResult<SessionInfoDto>.ForbiddenResult("此帳號沒有主要角色，無法使用網頁。請聯絡系統管理員。");
        denied.ErrorCode = ApiErrorCodes.NoPrimaryRole;
        handler.On("api/v1/Auth/me", HttpStatusCode.Forbidden, denied);
        handler.On("api/v1/Auth/logout", HttpStatusCode.OK, Ok(new object()));
        var (session, store, navigation) = Create(handler);

        Assert.False(await session.InitializeAsync());

        Assert.Contains(handler.Requests, x => x.Path == "api/v1/Auth/logout");
        Assert.Equal("/Auths/Login", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);
        Assert.True(store.Cleared);
        Assert.DoesNotContain(handler.Requests, x => x.Path == "api/v1/Auth/refresh");
    }

    /// <summary>改密碼、啟用兩步驟驗證之後 API 回的新 Token：存起來，之後的呼叫用新的，存放位置照「記住我」。</summary>
    [Fact]
    public async Task ApplyTokens_ShouldStoreTheRenewedTokens_AndUseThemNext()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/profile", HttpStatusCode.OK, Ok(new ProfileDto { Account = "alice" }));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();

        await session.ApplyTokensAsync(new TokenResponseDto
        {
            AccessToken = "a9",
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15),
            RefreshToken = "r9",
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(30),
            IsPersistent = true,
            User = new CurrentUserDto { Id = 7 },
        });
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);
        await api.GetAsync<ProfileDto>("api/v1/profile");

        Assert.Equal("r9", store.Stored!.RefreshToken);
        Assert.True(store.Stored.IsPersistent);
        Assert.Equal("Bearer a9", handler.Requests.Last(x => x.Path == "api/v1/profile").Authorization);
        Assert.Null(navigation.NavigatedTo);
    }

    /// <summary>匿名頁的呼叫（登入頁設定、啟動頁的系統名稱）：不帶 Token、401 不刷新也不把人帶去登入頁。</summary>
    [Fact]
    public async Task AnonymousCalls_ShouldNeitherSendATokenNorSendTheUserToTheLoginPage()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/login-settings", HttpStatusCode.Unauthorized, Fail());
        var (session, store, navigation) = Create(handler);
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.GetAnonymousAsync<LoginPageSettingsDto>("api/v1/Auth/login-settings");

        Assert.False(result.Success);
        Assert.Null(navigation.NavigatedTo);
        Assert.False(store.Cleared);
        Assert.Null(handler.Requests.Single().Authorization);
        Assert.DoesNotContain(handler.Requests, x => x.Path == "api/v1/Auth/refresh");
    }

    [Fact]
    public async Task InitializeAsync_WhenTheRefreshIsThrottled_ShouldNotSendTheUserBackToTheLoginPage()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.Unauthorized, Fail());
        handler.On("api/v1/Auth/refresh", HttpStatusCode.TooManyRequests, Throttled());
        var (session, store, navigation) = Create(handler);

        var ready = await session.InitializeAsync();

        Assert.False(ready);
        Assert.True(session.IsThrottled);
        Assert.Null(navigation.NavigatedTo);
        Assert.False(store.Cleared);
    }

    [Fact]
    public async Task InitializeAsync_WhenTheServerThrottlesMe_ShouldNeitherRefreshNorSignInAgain()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.TooManyRequests, Throttled());
        var (session, store, navigation) = Create(handler);

        var ready = await session.InitializeAsync();

        Assert.False(ready);
        Assert.True(session.IsThrottled);
        Assert.Null(navigation.NavigatedTo);
        Assert.DoesNotContain(handler.Requests, x => x.Path == "api/v1/Auth/refresh");
        Assert.NotNull(store.Stored);
    }

    [Fact]
    public async Task ApiClient_WhenTheRefreshIsThrottled_ShouldReturnTheThrottleMessageAndKeepTheUserSignedIn()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.TooManyRequests, Throttled());
        handler.On("api/v1/profile", HttpStatusCode.Unauthorized, Fail());
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);

        var result = await api.GetAsync<ProfileDto>("api/v1/profile");

        Assert.False(result.Success);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(ClientSession.ThrottledMessage, result.UserMessage());
        Assert.Null(navigation.NavigatedTo);
        Assert.NotNull(store.Stored);
    }

    [Fact]
    public async Task StreamAsync_ShouldHandEachLineToTheCallerInOrder()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/ai/test", HttpStatusCode.OK, Ndjson(
            new AiStreamFrameDto { Kind = AiStreamFrameKinds.Start },
            new AiStreamFrameDto { Kind = AiStreamFrameKinds.Delta, Text = "你" },
            new AiStreamFrameDto { Kind = AiStreamFrameKinds.Delta, Text = "好" },
            new AiStreamFrameDto { Kind = AiStreamFrameKinds.Result, Result = new AiResultDto { Success = true, Markdown = "你好" } }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);
        var frames = new List<AiStreamFrameDto>();

        var result = await api.StreamAsync<AiStreamFrameDto>("api/v1/ai/test", new { }, frame => { frames.Add(frame); return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "start", "delta", "delta", "result" }, frames.Select(x => x.Kind));
        Assert.Equal("你好", string.Concat(frames.Select(x => x.Text)));
        Assert.Equal("你好", frames[^1].Result!.Markdown);
    }

    [Fact]
    public async Task StreamAsync_On401_ShouldRefreshAndRetryOnce()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.OK, Tokens("a2", "r2", persistent: true));
        handler.On("api/v1/ai/test", (HttpStatusCode.Unauthorized, Fail()), (HttpStatusCode.OK, Ndjson(new AiStreamFrameDto { Kind = AiStreamFrameKinds.Result })));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);
        var count = 0;

        var result = await api.StreamAsync<AiStreamFrameDto>("api/v1/ai/test", null, _ => { count++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, count);
        Assert.Equal(new[] { "Bearer a1", "Bearer a2" }, handler.Requests.Where(x => x.Path == "api/v1/ai/test").Select(x => x.Authorization ?? string.Empty));
    }

    [Fact]
    public async Task StreamAsync_WhenTheServerRefuses_ShouldReturnItsMessageWithoutCallingTheCaller()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/ai/test", HttpStatusCode.Forbidden, ApiResult<object>.ForbiddenResult("只有管理員可以使用這個功能。"));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);
        var called = false;

        var result = await api.StreamAsync<AiStreamFrameDto>("api/v1/ai/test", null, _ => { called = true; return Task.CompletedTask; }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.Contains("只有管理員", result.UserMessage());
        Assert.False(called);
    }

    [Fact]
    public async Task StreamAsync_WhenTheCallerCancels_ShouldReturnAFailureInsteadOfThrowing()
    {
        var handler = new FakeHandler();
        handler.SignedIn(Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/ai/test", HttpStatusCode.OK, Ndjson(
            new AiStreamFrameDto { Kind = AiStreamFrameKinds.Delta, Text = "一" },
            new AiStreamFrameDto { Kind = AiStreamFrameKinds.Delta, Text = "二" }));
        var (session, _, navigation) = Create(handler);
        await session.InitializeAsync();
        var api = new ApiClient(handler.Client, session, navigation, NullLogger<ApiClient>.Instance);
        using var cancel = new CancellationTokenSource();
        var received = 0;

        var result = await api.StreamAsync<AiStreamFrameDto>("api/v1/ai/test", null, _ => { received++; cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);

        Assert.False(result.Success);
        Assert.Equal(1, received);
    }

    private static StringContent Ndjson(params AiStreamFrameDto[] frames)
        => new(string.Concat(frames.Select(frame => JsonSerializer.Serialize(frame, JsonSerializerOptions.Web) + Environment.NewLine)),
            System.Text.Encoding.UTF8, "application/x-ndjson");

    private static ApiResult<TokenResponseDto> Tokens(string access, string refresh, bool persistent)
        => ApiResult<TokenResponseDto>.SuccessResult(new TokenResponseDto
        {
            AccessToken = access,
            AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(60),
            RefreshToken = refresh,
            RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(7),
            IsPersistent = persistent,
            User = new CurrentUserDto { Id = 7 },
        });

    private static ApiResult<T> Ok<T>(T data) => ApiResult<T>.SuccessResult(data);

    private static ApiResult<object> Fail() => ApiResult<object>.UnauthorizedResult("無效");

    private static ApiResult<object> Throttled() => ApiResult<object>.FailureResult("請求過於頻繁，請稍後再試。", 429);

    private sealed class FakeTokenStore : TokenStore
    {
        public FakeTokenStore()
            : base(new NoJsRuntime())
        {
        }

        public StoredTokens? Stored { get; set; }

        public bool Cleared { get; private set; }

        public override Task<StoredTokens?> ReadAsync() => Task.FromResult(Stored);

        public override Task WriteAsync(StoredTokens tokens)
        {
            Stored = tokens;
            return Task.CompletedTask;
        }

        public override Task ClearAsync()
        {
            Stored = null;
            Cleared = true;
            return Task.CompletedTask;
        }

        // 瀏覽器的 Web Locks 不在這裡測：直接執行。
        public override Task<bool> RunLockedAsync<T>(DotNetObjectReference<T> target, string method)
            => ((ClientSession)(object)target.Value).RefreshUnderLockAsync();

        /// <summary>已開的分頁會交出的 Token（null＝沒有分頁回應）。</summary>
        public StoredTokens? FromOtherTab { get; set; }

        public TimeSpan? AskedOtherTabsFor { get; private set; }

        public bool Listening { get; private set; }

        public bool BroadcastLogout { get; private set; }

        public override Task<StoredTokens?> RequestFromOtherTabsAsync(TimeSpan timeout)
        {
            AskedOtherTabsFor = timeout;
            return Task.FromResult(FromOtherTab);
        }

        public override Task<IAsyncDisposable?> ListenAsync<T>(DotNetObjectReference<T> target)
        {
            Listening = true;
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public override Task BroadcastLogoutAsync()
        {
            BroadcastLogout = true;
            return Task.CompletedTask;
        }

        public int? BroadcastSignedIn { get; private set; }

        public bool SessionCleared { get; private set; }

        public override Task BroadcastSignedInAsync(int userId)
        {
            BroadcastSignedIn = userId;
            return Task.CompletedTask;
        }

        public override Task ClearSessionAsync()
        {
            SessionCleared = true;
            if (Stored is { IsPersistent: false })
            {
                Stored = null;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("測試不呼叫 JS。");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("測試不呼叫 JS。");
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string page = "Profile") => Initialize("https://localhost/", "https://localhost/" + page);

        public string? NavigatedTo { get; private set; }

        public bool ForceLoad { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigatedTo = uri;
            ForceLoad = options.ForceLoad;
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(HttpStatusCode Status, object Body)>> responses = new(StringComparer.OrdinalIgnoreCase);

        public FakeHandler() => Client = new HttpClient(this) { BaseAddress = new Uri("https://localhost/") };

        public HttpClient Client { get; }

        public bool Offline { get; set; }

        /// <summary>瀏覽器已經存著這組 Token（Create 時放進假的 storage）。</summary>
        public StoredTokens? Seed { get; private set; }

        public void SignedIn(ApiResult<TokenResponseDto> tokens)
        {
            var data = tokens.Data!;
            Seed = new StoredTokens(data.AccessToken, data.AccessTokenExpiresAt, data.RefreshToken, data.RefreshTokenExpiresAt, data.IsPersistent, data.User.Id);
        }

        public List<(string Path, string? Authorization, string? ClientHeader)> Requests { get; } = [];

        public void On(string path, HttpStatusCode status, params object[] bodies)
            => On(path, bodies.Select(body => (status, body)).ToArray());

        public void On(string path, params (HttpStatusCode Status, object Body)[] sequence)
            => responses[path] = new Queue<(HttpStatusCode, object)>(sequence);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Add((path, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-MyProject-Client", out var values) ? values.Single() : null));
            if (!responses.TryGetValue(path, out var queue) || queue.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            // 最後一個回應重複使用，前面的依序用掉。
            var (status, body) = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            // HttpContent 照原樣回（例如 NDJSON 串流），其他序列化成 JSON。
            var content = body as HttpContent ?? JsonContent.Create(body, body.GetType());
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
}
