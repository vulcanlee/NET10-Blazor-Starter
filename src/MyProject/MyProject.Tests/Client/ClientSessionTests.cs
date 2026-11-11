using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
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
/// WASM 前端的登入工作階段與 API 呼叫（0.10.1 起，WASM 遷移 M2）：Token 的取得、刷新、多分頁、失敗時的退路與導頁。
/// 用假的 HTTP 回應與假的 Token 存放（瀏覽器的 storage 與 Web Locks 由實機驗證涵蓋）。
/// </summary>
public sealed class ClientSessionTests
{
    [Fact]
    public async Task Initialize_WithoutStoredTokens_ShouldExchangeTheWebSession_AndStoreThemByRememberMe()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7, Account = "alice" }));
        var (session, store, navigation) = Create(handler);

        Assert.True(await session.InitializeAsync());

        Assert.Equal("alice", session.Info!.Account);
        Assert.Equal("r1", store.Stored!.RefreshToken);
        Assert.True(store.Stored.IsPersistent);
        Assert.Null(navigation.NavigatedTo);
        Assert.Equal("1", handler.Requests.Single(x => x.Path == "api/v1/Auth/bridge").ClientHeader);
    }

    [Fact]
    public async Task Initialize_WhenTheWebSessionCannotBeExchanged_ShouldGoBackToTheLoginPage()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.Unauthorized, Fail());
        var (session, store, navigation) = Create(handler);

        Assert.False(await session.InitializeAsync());

        Assert.Equal("/Auths/Login", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);
        Assert.True(store.Cleared);
    }

    [Theory]
    [InlineData(true, false, "/ChangePassword")]
    [InlineData(false, true, "/TwoFactorSetup")]
    public async Task Initialize_WhenSetupIsForced_ShouldGoToThatPage(bool mustChangePassword, bool requiresTwoFactor, string expected)
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: false));
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
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
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
    public async Task Refresh_WhenTheRefreshTokenIsRejected_ShouldExchangeTheWebSessionAgain()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true), Tokens("a3", "r3", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.Unauthorized, Fail());
        var (session, store, _) = Create(handler);
        await session.InitializeAsync();

        Assert.True(await session.RefreshAsync());

        Assert.Equal("r3", store.Stored!.RefreshToken);
        Assert.Equal(2, handler.Requests.Count(x => x.Path == "api/v1/Auth/bridge"));
    }

    [Fact]
    public async Task Initialize_WithTokensOfAnotherLogin_ShouldDiscardThem_AndExchangeTheWebSession()
    {
        // 同一個瀏覽器先由 9 號登入、沒清掉 storage，現在 Cookie 是 7 號的：不可沿用 9 號的 Token。
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, _) = Create(handler);
        store.Stored = new StoredTokens("a9", DateTime.UtcNow.AddMinutes(30), "r9", DateTime.UtcNow.AddDays(7), true, 9);
        session.ExpectedUserId = 7;

        Assert.True(await session.InitializeAsync());

        Assert.Equal("r1", store.Stored!.RefreshToken);
        Assert.Equal(7, store.Stored.UserId);
        Assert.DoesNotContain(handler.Requests, x => x.Authorization == "Bearer a9");
    }

    [Fact]
    public async Task Refresh_WhenAnotherTabStoredTokensOfAnotherUser_ShouldNotAdoptThem()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        handler.On("api/v1/Auth/refresh", HttpStatusCode.OK, Tokens("a2", "r2", persistent: true));
        var (session, store, _) = Create(handler);
        session.ExpectedUserId = 7;
        await session.InitializeAsync();
        store.Stored = new StoredTokens("a9", DateTime.UtcNow.AddMinutes(30), "r9", DateTime.UtcNow.AddDays(7), true, 9);

        Assert.True(await session.RefreshAsync());

        Assert.Equal("a2", await session.GetAccessTokenAsync());
        Assert.Contains(handler.Requests, x => x.Path == "api/v1/Auth/refresh");
    }

    [Fact]
    public async Task Refresh_WhenTheServerCannotBeReached_ShouldReturnFalseInsteadOfThrowing()
    {
        // 刷新是從 JS（Web Locks）回呼進來的：例外往外丟會變成整頁錯誤，必須回 false。
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();
        handler.Offline = true;

        Assert.False(await session.RefreshAsync());

        Assert.Equal("r1", store.Stored!.RefreshToken);
        Assert.Null(navigation.NavigatedTo);
    }

    [Fact]
    public async Task Logout_ShouldClearTheStoredTokens_AndHandOverToTheWebLogoutPage()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
        handler.On("api/v1/Auth/me", HttpStatusCode.OK, Ok(new SessionInfoDto { Id = 7 }));
        var (session, store, navigation) = Create(handler);
        await session.InitializeAsync();

        await session.LogoutAsync();

        Assert.True(store.Cleared);
        Assert.Equal("/Auths/Logout", navigation.NavigatedTo);
        Assert.True(navigation.ForceLoad);
    }

    [Fact]
    public async Task ApiClient_On401_ShouldRefreshAndRetryOnce()
    {
        var handler = new FakeHandler();
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
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
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
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
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
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
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
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
        handler.On("api/v1/Auth/bridge", HttpStatusCode.OK, Tokens("a1", "r1", persistent: true));
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
    [InlineData("/projects", false)]
    [InlineData("/", false)]
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

    [Theory]
    [InlineData(0, "0")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    public void BellBadge_ShouldMatchTheServerBell(int count, string expected)
        => Assert.Equal(expected, ClientNotificationBell.BadgeText(count));

    private static (ClientSession Session, FakeTokenStore Store, TestNavigationManager Navigation) Create(FakeHandler handler)
    {
        var store = new FakeTokenStore();
        var navigation = new TestNavigationManager();
        return (new ClientSession(handler.Client, store, navigation, NullLogger<ClientSession>.Instance), store, navigation);
    }

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
    }

    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("測試不呼叫 JS。");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException("測試不呼叫 JS。");
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://localhost/", "https://localhost/Profile");

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
            return Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
}
