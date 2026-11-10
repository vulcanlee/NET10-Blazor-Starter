using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// WASM 遷移 M1（0.10.0 起）的 Token API：刷新 Token 輪替與重用偵測、登出、網頁 Cookie 換 Token（bridge）、
/// <c>/auth/me</c> 的工作階段資訊，以及強制改密碼期間擋下業務 API。用真的主機與資料庫。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class TokenApiIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private const string ClientHeader = "X-MyProject-Client";
    private readonly ApiTestApplicationFactory factory;

    public TokenApiIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    // ---------- 刷新與登出 ----------

    /// <summary>
    /// 刷新後舊的立即失效；寬限期內重送舊的（同時刷新、回應遺失）只回 401、不撤銷，新的照常可用。
    /// 寬限期過後的重用會撤銷整串，由 RefreshTokenStoreTests 以可控時鐘驗證。
    /// </summary>
    [Fact]
    public async Task Refresh_ShouldRotate_AndAnImmediateReplayShouldNotRevokeTheNewToken()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = await LoginAsync(client, user);

        var refreshed = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var next = (await refreshed.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        Assert.NotEqual(tokens.RefreshToken, next.RefreshToken);
        Assert.False(string.IsNullOrWhiteSpace(next.AccessToken));

        var replay = await RefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("Refresh Token 無效或已過期。", (await replay.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.ErrorMessage);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, next.RefreshToken)).StatusCode);
        Assert.False(await AuditExistsAsync(AuditActions.Token.ReuseDetected, user.Id));
    }

    /// <summary>
    /// 審查發現（M1）：Cookie 的版本已經過時、但快取還沒看到（別的行程換的版本、或快取競態），bridge 不可用資料庫的新版本發 Token ——
    /// 否則一個剛被撤銷的 Cookie 能換出一串帶新版本的 Token，完全躲過這次撤銷。
    /// 這裡以不經 EF 的 SQL 換版本（模擬別的行程），快取仍是舊值、Cookie 驗證會放行，bridge 必須自己擋下。
    /// </summary>
    [Fact]
    public async Task Bridge_WithACookieWhoseStampIsStale_ShouldBeRejectedEvenIfTheCacheStillAcceptsIt()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();
        var cookie = CookieFor(user, persistent: true, loginSession: "login-e");
        Assert.Equal(HttpStatusCode.OK, (await BridgeAsync(client, cookie)).StatusCode);

        await using (var context = await CreateContextAsync())
        {
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE MyUser SET SecurityStamp = $stamp WHERE Id = $id";
            var stamp = command.CreateParameter();
            stamp.ParameterName = "$stamp";
            stamp.Value = SecurityStamps.New();
            command.Parameters.Add(stamp);
            var id = command.CreateParameter();
            id.ParameterName = "$id";
            id.Value = user.Id;
            command.Parameters.Add(id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await BridgeAsync(client, cookie)).StatusCode);
    }

    [Fact]
    public async Task Bridge_ShouldNotLetTheTokensOutliveTheCookie()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();
        var cookieExpires = DateTimeOffset.UtcNow.AddHours(2);

        var response = await BridgeAsync(client, CookieFor(user, persistent: false, loginSession: "login-f", expiresUtc: cookieExpires));

        var tokens = (await response.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        Assert.True(tokens.RefreshTokenExpiresAt <= cookieExpires.UtcDateTime.AddSeconds(1), $"刷新 Token 到 {tokens.RefreshTokenExpiresAt:o}，Cookie 到 {cookieExpires.UtcDateTime:o}");
        await using var context = await CreateContextAsync();
        var row = await context.RefreshSession.SingleAsync(x => x.MyUserId == user.Id);
        Assert.NotNull(row.AbsoluteExpiresAtUtc);
    }

    [Fact]
    public async Task Logout_ShouldRevokeTheRefreshToken_AndAuditTheLogout()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = await LoginAsync(client, user);

        var logout = await client.PostAsJsonAsync("/api/v1/Auth/logout", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
        Assert.True(await AuditExistsAsync(AuditActions.Logout, user.Id));
    }

    // ---------- 網頁 Cookie 換 Token ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bridge_ShouldIssueTokensThatFollowTheRememberMeChoice(bool persistent)
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();

        var response = await BridgeAsync(client, CookieFor(user, persistent, loginSession: "login-a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        Assert.Equal(persistent, tokens.IsPersistent);
        Assert.Equal(user.Id, tokens.User.Id);

        await using var context = await CreateContextAsync();
        var row = await context.RefreshSession.SingleAsync(x => x.MyUserId == user.Id);
        Assert.Equal(RefreshClientKinds.WebBridge, row.ClientKind);
        Assert.Equal("login-a", row.LoginSessionId);
        Assert.Equal(persistent, row.IsPersistent);
    }

    [Fact]
    public async Task Bridge_WithoutTheClientHeader_ShouldBeRejected()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Auth/bridge");
        request.Headers.Add("Cookie", CookieFor(user, persistent: true, loginSession: "login-b"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var context = await CreateContextAsync();
        Assert.False(await context.RefreshSession.AnyAsync(x => x.MyUserId == user.Id));
    }

    [Fact]
    public async Task Bridge_WithoutAValidCookie_ShouldReturnApiResult401()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();

        var anonymous = await BridgeAsync(client, cookie: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.False((await anonymous.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Success);

        var cookie = CookieFor(user, persistent: true, loginSession: "login-c");
        await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(user.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await BridgeAsync(client, cookie)).StatusCode);
    }

    [Fact]
    public async Task Bridge_WithACookieFromBeforeTheLoginSessionClaim_ShouldReissueTheCookieWithOne()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();

        var response = await BridgeAsync(client, CookieFor(user, persistent: true, loginSession: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith(".MyProject.Auth=", StringComparison.Ordinal));
        await using var context = await CreateContextAsync();
        var row = await context.RefreshSession.SingleAsync(x => x.MyUserId == user.Id);
        Assert.False(string.IsNullOrEmpty(row.LoginSessionId));
    }

    [Fact]
    public async Task WebLogout_ShouldRevokeTheTokensThatLoginBridged()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();
        var cookie = CookieFor(user, persistent: true, loginSession: "login-d");
        var tokens = (await (await BridgeAsync(client, cookie)).Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;

        using (var logout = new HttpRequestMessage(HttpMethod.Get, "/Auths/Logout"))
        {
            logout.Headers.Add("Cookie", cookie);
            await client.SendAsync(logout);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    // ---------- 工作階段資訊與強制設定 ----------

    [Fact]
    public async Task Me_ShouldDescribeTheSessionFromTheDatabase()
    {
        var user = await AddUserAsync(isAdmin: true);
        using var client = factory.CreateClient();
        var tokens = await LoginAsync(client, user);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/v1/Auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = (await response.Content.ReadFromJsonAsync<ApiResult<SessionInfoDto>>())!.Data!;
        Assert.Equal(user.Id, me.Id);
        Assert.Equal(user.Account, me.Account);
        Assert.Equal(user.Name, me.Name);
        Assert.True(me.IsAdmin);
        Assert.False(me.MustChangePassword);
        Assert.False(me.RequiresTwoFactorSetup);
        Assert.NotNull(me.Permissions);
        Assert.NotNull(me.Teams);
    }

    [Fact]
    public async Task WhileAPasswordChangeIsForced_BusinessApisShouldBeBlocked_ButMeShouldWork()
    {
        var user = await AddUserAsync(isAdmin: true);
        using var client = factory.CreateClient();
        var tokens = await LoginAsync(client, user);
        await using (var context = await CreateContextAsync())
        {
            await context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.MustChangePassword, true));
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var blocked = await client.PostAsJsonAsync("/api/v1/Category/search", new { PageIndex = 1, PageSize = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var body = (await blocked.Content.ReadFromJsonAsync<ApiResult<object>>())!;
        Assert.Equal(ApiErrorCodes.PasswordChangeRequired, body.ErrorCode);

        var me = await client.GetAsync("/api/v1/Auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True((await me.Content.ReadFromJsonAsync<ApiResult<SessionInfoDto>>())!.Data!.MustChangePassword);
    }

    [Fact]
    public async Task AccessToken_ForAUserWhoWasForceLoggedOut_ShouldBeRejectedOnTheNextRequest()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = await LoginAsync(client, user);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/Auth/me")).StatusCode);

        await using (var context = await CreateContextAsync())
        {
            await context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, SecurityStamps.New()));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/Auth/me")).StatusCode);
    }

    // ---------- 輔助 ----------

    private HttpClient NoRedirectClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<TokenResponseDto> LoginAsync(HttpClient client, MyUser user)
    {
        var response = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = "Passw0rd-1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
        => client.PostAsJsonAsync("/api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = refreshToken });

    private static async Task<HttpResponseMessage> BridgeAsync(HttpClient client, string? cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Auth/bridge");
        request.Headers.Add(ClientHeader, "1");
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private string CookieFor(MyUser user, bool persistent, string? loginSession, DateTimeOffset? expiresUtc = null)
    {
        var principal = CookieClaims.Create(user);
        if (loginSession is null)
        {
            var identity = (ClaimsIdentity)principal.Identity!;
            identity.RemoveClaim(identity.FindFirst(CookieClaims.LoginSessionClaimType));
        }
        else
        {
            var identity = (ClaimsIdentity)principal.Identity!;
            identity.RemoveClaim(identity.FindFirst(CookieClaims.LoginSessionClaimType));
            identity.AddClaim(new Claim(CookieClaims.LoginSessionClaimType, loginSession));
        }

        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.CookieScheme);
        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, new AuthenticationProperties { IsPersistent = persistent, ExpiresUtc = expiresUtc }, MagicObjectHelper.CookieScheme));
        return $"{options.Cookie.Name}={ticket}";
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin = false)
    {
        await using var context = await CreateContextAsync();
        var account = $"token-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = "Token " + account[^6..],
            Password = SecurePasswordHasher.HashPassword("Passw0rd-1"),
            Status = true,
            IsAdmin = isAdmin,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<bool> AuditExistsAsync(string action, int userId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.AnyAsync(x => x.Action == action && (x.ActorUserId == userId || x.TargetId == userId.ToString()));
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
