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
/// WASM 遷移 M1（0.10.0 起）的 Token API：刷新 Token 輪替與重用偵測、兩個分頁同時刷新、登出（0.10.8 起網頁登出撤銷整次登入）、
/// <c>/auth/me</c> 的工作階段資訊，以及強制改密碼期間擋下業務 API。網頁 Cookie 換 Token（bridge）0.10.8 起已移除。用真的主機與資料庫。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class TokenApiIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
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

    /// <summary>
    /// 0.10.8 起（WASM 遷移 M9b）：兩個分頁（或同一分頁兩個請求）拿同一個刷新 Token 同時刷新 —— 只有一個換到新的，另一個在寬限期內只回 401、
    /// 不撤銷整串，換到的那組照常可用（使用者不會因此被登出；前端拿到 401 時先重讀 storage 沿用另一邊換到的）。
    /// </summary>
    [Fact]
    public async Task Refresh_FromTwoTabsAtOnce_ShouldNotSignTheUserOut()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = await WebLoginAsync(client, user, rememberMe: true);

        var responses = await Task.WhenAll(RefreshAsync(client, tokens.RefreshToken), RefreshAsync(client, tokens.RefreshToken));

        var winner = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        var next = (await winner.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, next.RefreshToken)).StatusCode);
        Assert.False(await AuditExistsAsync(AuditActions.Token.ReuseDetected, user.Id));
    }

    /// <summary>已被換掉的刷新 Token 在寬限期過後又被用（可能外洩）：整串撤銷、記重用稽核，連換到的新 Token 與已發出的存取 Token 都失效。</summary>
    [Fact]
    public async Task Refresh_WithAReplacedTokenAfterTheGracePeriod_ShouldRevokeTheWholeFamily()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = await WebLoginAsync(client, user, rememberMe: false);
        var next = (await (await RefreshAsync(client, tokens.RefreshToken)).Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;

        // 把舊的那一筆「被換掉的時間」往前推到寬限期之外（等同過了 30 秒以上才又被用）。
        await using (var context = await CreateContextAsync())
        {
            var past = DateTime.UtcNow - RefreshTokenStore.ReuseGracePeriod - TimeSpan.FromSeconds(5);
            await context.RefreshSession.Where(x => x.MyUserId == user.Id && x.ReplacedById != null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, past));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
        Assert.True(await AuditExistsAsync(AuditActions.Token.ReuseDetected, user.Id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, next.RefreshToken)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", next.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/Auth/me")).StatusCode);
    }

    /// <summary>網頁登出撤銷這次登入的每一串（每個分頁各一串，以登入識別串起來），別的登入不受影響；稽核與 Server 版登出頁相同（只記操作者）。</summary>
    [Fact]
    public async Task WebLogout_ShouldRevokeEveryTabOfThatLogin_ButNotOtherLogins()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var firstTab = await WebLoginAsync(client, user, rememberMe: false);
        var secondTab = (await (await client.PostAsJsonAsync("/api/v1/Auth/session/fork", new RefreshTokenRequestDto { RefreshToken = firstTab.RefreshToken }))
            .Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        var otherLogin = await WebLoginAsync(client, user, rememberMe: false);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/Auth/logout", new RefreshTokenRequestDto { RefreshToken = secondTab.RefreshToken })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, firstTab.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, secondTab.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, otherLogin.RefreshToken)).StatusCode);
        await using var context = await CreateContextAsync();
        Assert.True(await context.AuditLog.AnyAsync(x => x.Action == AuditActions.Logout && x.ActorUserId == user.Id && x.Detail == null));
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

    private static async Task<TokenResponseDto> LoginAsync(HttpClient client, MyUser user)
    {
        var response = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = "Passw0rd-1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
        => client.PostAsJsonAsync("/api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = refreshToken });

    private static async Task<TokenResponseDto> WebLoginAsync(HttpClient client, MyUser user, bool rememberMe)
    {
        var response = await client.PostAsJsonAsync("/api/v1/Auth/login/web", new WebLoginRequestDto { Account = user.Account, Password = "Passw0rd-1", RememberMe = rememberMe });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResult<WebLoginResultDto>>())!.Data!.Tokens!;
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
