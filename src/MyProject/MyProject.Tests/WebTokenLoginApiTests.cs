using System.IdentityModel.Tokens.Jwt;
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
using MyProject.Web.Controllers;

namespace MyProject.Tests;

/// <summary>
/// WASM 遷移 M9a（0.10.8 起）：網頁以 Token 登入的 API —— 登入頁設定、網頁登入（記住我、記住裝置）、兩步驟驗證待驗票證、
/// Google 一次性代碼、新分頁取得 Token、存取 Token 帶串的識別（登出立即生效）、沒有主要角色的帳號不能用網頁。用真的主機與資料庫。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class WebTokenLoginApiTests : IClassFixture<ApiTestApplicationFactoryWithGoogle>
{
    private const string Password = "Passw0rd-1";
    private readonly ApiTestApplicationFactoryWithGoogle factory;

    public WebTokenLoginApiTests(ApiTestApplicationFactoryWithGoogle factory)
    {
        this.factory = factory;
    }

    // ---------- 登入頁設定 ----------

    [Fact]
    public async Task LoginSettings_ShouldBeAnonymous_AndDescribeTheLoginPage()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/Auth/login-settings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settings = (await Read<LoginPageSettingsDto>(response)).Data!;
        Assert.True(settings.GoogleEnabled);
        Assert.False(settings.EmailEnabled);
        Assert.True(settings.MaxFailedAttempts > 0);
        Assert.True(settings.LockoutMinutes > 0);
        Assert.Equal(30, settings.RememberMeDays);
        Assert.False(string.IsNullOrWhiteSpace(settings.PasswordRule));
        Assert.False(string.IsNullOrWhiteSpace(settings.SystemName));
        Assert.True(settings.ResetLinkLifetimeMinutes > 0);
    }

    /// <summary>寄信未啟用（出貨預設 None）：忘記與重設密碼明說不提供，與 Server 版頁面相同的文字。</summary>
    [Fact]
    public async Task PasswordReset_WhenEmailIsDisabled_ShouldSaySo()
    {
        using var client = factory.CreateClient();

        var forgot = await client.PostAsJsonAsync("/api/v1/Auth/password/forgot", new ForgotPasswordRequestDto { Identifier = "anyone" });
        var check = await client.PostAsJsonAsync("/api/v1/Auth/password/reset/check", new ResetPasswordCheckRequestDto { Token = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, forgot.StatusCode);
        Assert.Equal("系統目前未啟用寄信功能，無法自助重設密碼。請聯絡系統管理員協助。", (await Read<object>(forgot)).ErrorMessage);
        Assert.False((await Read<ResetPasswordCheckDto>(check)).Data!.Valid);
    }

    // ---------- 網頁登入 ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WebLogin_ShouldIssueWebTokens_WhoseLifetimesFollowRememberMe(bool rememberMe)
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var before = DateTime.UtcNow;

        var response = await WebLoginAsync(client, user.Account, rememberMe, returnUrl: "/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await Read<WebLoginResultDto>(response)).Data!;
        var tokens = result.Tokens!;
        Assert.Equal("/projects", result.ReturnUrl);
        Assert.Equal(rememberMe, tokens.IsPersistent);
        Assert.InRange(tokens.AccessTokenExpiresAt, before.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
        var expectedRefresh = rememberMe ? TimeSpan.FromDays(30) : TimeSpan.FromMinutes(480);
        Assert.InRange(tokens.RefreshTokenExpiresAt, before.Add(expectedRefresh).AddMinutes(-1), DateTime.UtcNow.Add(expectedRefresh).AddMinutes(1));

        await using var context = await CreateContextAsync();
        var row = await context.RefreshSession.SingleAsync(x => x.MyUserId == user.Id);
        Assert.Equal(RefreshClientKinds.Web, row.ClientKind);
        Assert.False(string.IsNullOrEmpty(row.LoginSessionId));
        Assert.Equal(rememberMe, row.AbsoluteExpiresAtUtc is not null);
        Assert.True(await AuditExistsAsync(AuditActions.Login.Success, user.Id));

        // 存取 Token 帶這一串的識別與發出管道。
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        Assert.Equal(row.FamilyId, jwt.Claims.Single(x => x.Type == "fid").Value);
        Assert.Equal(RefreshClientKinds.Web, jwt.Claims.Single(x => x.Type == "client_kind").Value);
    }

    [Fact]
    public async Task WebLogin_WithAForeignReturnUrl_ShouldFallBackToApp()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();

        var result = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false, returnUrl: "//evil.example/x"))).Data!;

        Assert.Equal("/App", result.ReturnUrl);
    }

    [Fact]
    public async Task WebLogin_WithAWrongPassword_ShouldUseTheSameMessageAsTheLoginPage_AndCountTheFailure()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/Auth/login/web", new WebLoginRequestDto { Account = user.Account, Password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(MyUserServiceLogin.InvalidCredentialsMessage, (await Read<WebLoginResultDto>(response)).ErrorMessage);
        Assert.Equal(1, await FailedCountAsync(user.Id));
        Assert.True(await AuditExistsAsync(AuditActions.Login.Failed, user.Id));
    }

    [Fact]
    public async Task WebLogin_ForAnAccountThatMustSetUpTwoFactor_ShouldIssueTokens_ButTheGateBlocksBusinessApis()
    {
        var roleId = await AddRoleAsync(requireTwoFactor: true);
        var user = await AddUserAsync(roleId);
        using var client = factory.CreateClient();

        var tokens = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false))).Data!.Tokens!;
        Bearer(client, tokens.AccessToken);

        var blocked = await client.GetAsync("/api/v1/profile");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal(ApiErrorCodes.TwoFactorSetupRequired, (await Read<object>(blocked)).ErrorCode);

        // 設定頁與外框要用的 API 放行，否則那一頁會被自己的外框一直導回來。
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/Auth/two-factor/setup")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/shell/info")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/shell/menu")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/notifications/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/announcements/active")).StatusCode);
    }

    /// <summary>App 的舊契約不變：同一個請求帶驗證碼；必須設定兩步驟驗證卻沒設定照舊拒絕（App 沒有地方設定）。</summary>
    [Fact]
    public async Task ApiLogin_ShouldKeepItsContract()
    {
        var (user, secret) = await AddEnrolledUserAsync();
        using var client = factory.CreateClient();
        var code = new TotpService().ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);

        var ok = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password, TwoFactorCode = code });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var tokens = (await Read<TokenResponseDto>(ok)).Data!;
        Assert.True(tokens.IsPersistent);
        await using var context = await CreateContextAsync();
        Assert.Equal(RefreshClientKinds.Api, (await context.RefreshSession.SingleAsync(x => x.MyUserId == user.Id)).ClientKind);
    }

    // ---------- 兩步驟驗證 ----------

    [Fact]
    public async Task TwoFactor_WrongCodeCounts_RightCodeSignsIn_TicketIsSingleUse_AndTheDeviceCanBeRemembered()
    {
        var (user, secret) = await AddEnrolledUserAsync();
        using var client = factory.CreateClient();

        var first = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, rememberMe: true, returnUrl: "/teams"))).Data!;
        Assert.True(first.RequiresTwoFactor);
        Assert.Null(first.Tokens);
        Assert.False(string.IsNullOrEmpty(first.TwoFactorTicket));
        Assert.Equal(0, await FailedCountAsync(user.Id));

        var wrong = await TwoFactorAsync(client, first.TwoFactorTicket!, "000000x", rememberDevice: false);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(MyUserServiceLogin.InvalidSecondFactorMessage, (await Read<WebLoginResultDto>(wrong)).ErrorMessage);
        Assert.Equal(1, await FailedCountAsync(user.Id));
        Assert.True(await AuditExistsAsync(AuditActions.Login.TwoFactorFailed, user.Id));

        // 錯了一次，同一張票證仍可再試。
        var code = new TotpService().ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);
        var ok = await TwoFactorAsync(client, first.TwoFactorTicket!, code, rememberDevice: true);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var done = (await Read<WebLoginResultDto>(ok)).Data!;
        Assert.True(done.Tokens!.IsPersistent);
        Assert.Equal("/teams", done.ReturnUrl);
        Assert.False(string.IsNullOrEmpty(done.RememberDeviceToken));
        Assert.Equal(0, await FailedCountAsync(user.Id));

        // 票證只能成功用一次。
        var replay = await TwoFactorAsync(client, first.TwoFactorTicket!, "anything", rememberDevice: false);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(ApiErrorCodes.TwoFactorTicketInvalid, (await Read<WebLoginResultDto>(replay)).ErrorCode);

        // 記住過的裝置：下次登入不必再輸入驗證碼。
        var remembered = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false, deviceToken: done.RememberDeviceToken))).Data!;
        Assert.False(remembered.RequiresTwoFactor);
        Assert.NotNull(remembered.Tokens);
        Assert.True(await AuditExistsAsync(AuditActions.Login.Success, user.Id, "mfa=device"));

        // 別人的記住裝置 Token 不算數。
        var (other, _) = await AddEnrolledUserAsync();
        var foreign = (await Read<WebLoginResultDto>(await WebLoginAsync(client, other.Account, false, deviceToken: done.RememberDeviceToken))).Data!;
        Assert.True(foreign.RequiresTwoFactor);
    }

    [Fact]
    public async Task TwoFactor_WithATamperedTicket_OrAfterTheSessionChanged_ShouldSendTheUserBackToLogin()
    {
        var (user, _) = await AddEnrolledUserAsync();
        using var client = factory.CreateClient();
        var ticket = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false))).Data!.TwoFactorTicket!;

        var tampered = await TwoFactorAsync(client, ticket[..^4] + "AAAA", "123456", false);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        Assert.Equal(ApiErrorCodes.TwoFactorTicketInvalid, (await Read<WebLoginResultDto>(tampered)).ErrorCode);

        // 等待輸入驗證碼的期間被強制登出（換了工作階段版本）：這張票證作廢，驗證碼不計入失敗。
        await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(user.Id);
        var stale = await TwoFactorAsync(client, ticket, "123456", false);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(ApiErrorCodes.TwoFactorTicketInvalid, (await Read<WebLoginResultDto>(stale)).ErrorCode);
        Assert.Equal(0, await FailedCountAsync(user.Id));
    }

    [Fact]
    public async Task TwoFactor_TicketShouldBeBoundToItsUser()
    {
        var (alice, _) = await AddEnrolledUserAsync();
        var (_, bobSecret) = await AddEnrolledUserAsync();
        using var client = factory.CreateClient();
        var aliceTicket = (await Read<WebLoginResultDto>(await WebLoginAsync(client, alice.Account, false))).Data!.TwoFactorTicket!;

        // 拿 Bob 的驗證碼配 Alice 的票證：驗的是 Alice 的密鑰，不會登入成任何人，而且算 Alice 的失敗。
        var bobCode = new TotpService().ComputeCode(bobSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);
        var response = await TwoFactorAsync(client, aliceTicket, bobCode, false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await FailedCountAsync(alice.Id));
    }

    // ---------- Google ----------

    [Fact]
    public async Task Google_WithABrowserBinding_ShouldRedirectWithAOneTimeCode_ThatOnlyThatBrowserCanExchangeOnce()
    {
        var user = await AddUserAsync(await AddRoleAsync());
        var verifier = "verifier-" + Guid.NewGuid().ToString("N");

        var callback = await GoogleCallbackAsync(user.Email!, GoogleLoginCodes.HashVerifier(verifier), returnUrl: "/projects");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!.OriginalString;
        Assert.StartsWith("/Auths/Login#code=", location);
        Assert.False(callback.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith(".MyProject.Auth=", StringComparison.Ordinal)));
        var code = Uri.UnescapeDataString(location["/Auths/Login#code=".Length..]);

        using var client = factory.CreateClient();
        var ok = await client.PostAsJsonAsync("/api/v1/Auth/google/exchange", new GoogleExchangeRequestDto { Code = code, Verifier = verifier });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var result = (await Read<WebLoginResultDto>(ok)).Data!;
        Assert.Equal("/projects", result.ReturnUrl);
        Assert.False(result.Tokens!.IsPersistent);
        Assert.True(await AuditExistsAsync(AuditActions.Login.SsoSuccess, user.Id, "provider=Google"));

        var again = await client.PostAsJsonAsync("/api/v1/Auth/google/exchange", new GoogleExchangeRequestDto { Code = code, Verifier = verifier });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task Google_CodePresentedByAnotherBrowser_ShouldBeRejectedAndBurned()
    {
        var user = await AddUserAsync(await AddRoleAsync());
        var verifier = "verifier-" + Guid.NewGuid().ToString("N");
        var callback = await GoogleCallbackAsync(user.Email!, GoogleLoginCodes.HashVerifier(verifier), returnUrl: null);
        var code = Uri.UnescapeDataString(callback.Headers.Location!.OriginalString["/Auths/Login#code=".Length..]);
        using var client = factory.CreateClient();

        var stolen = await client.PostAsJsonAsync("/api/v1/Auth/google/exchange", new GoogleExchangeRequestDto { Code = code, Verifier = "someone-else" });
        Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
        Assert.Equal(WebAuthControllerMessages.GoogleExpired, (await Read<WebLoginResultDto>(stolen)).ErrorMessage);

        var late = await client.PostAsJsonAsync("/api/v1/Auth/google/exchange", new GoogleExchangeRequestDto { Code = code, Verifier = verifier });
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
    }

    [Fact]
    public async Task Google_ForAnAccountWithTwoFactor_ShouldExchangeForATicket_NotTokens()
    {
        var (user, secret) = await AddEnrolledUserAsync();
        var verifier = "verifier-" + Guid.NewGuid().ToString("N");
        var callback = await GoogleCallbackAsync(user.Email!, GoogleLoginCodes.HashVerifier(verifier), returnUrl: null);
        var code = Uri.UnescapeDataString(callback.Headers.Location!.OriginalString["/Auths/Login#code=".Length..]);
        using var client = factory.CreateClient();

        var result = (await Read<WebLoginResultDto>(await client.PostAsJsonAsync("/api/v1/Auth/google/exchange", new GoogleExchangeRequestDto { Code = code, Verifier = verifier }))).Data!;

        Assert.True(result.RequiresTwoFactor);
        Assert.Null(result.Tokens);
        var totp = new TotpService().ComputeCode(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);
        var done = await TwoFactorAsync(client, result.TwoFactorTicket!, totp, false);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.True(await AuditExistsAsync(AuditActions.Login.SsoSuccess, user.Id, "provider=Google; mfa=totp"));
    }

    // ---------- 新分頁取得 Token ----------

    [Fact]
    public async Task Fork_ShouldOpenANewFamily_KeepingTheLoginPersistenceAndCap_WithoutRotatingTheSource()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, rememberMe: true))).Data!.Tokens!;

        var response = await client.PostAsJsonAsync("/api/v1/Auth/session/fork", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forked = (await Read<TokenResponseDto>(response)).Data!;
        Assert.True(forked.IsPersistent);
        Assert.NotEqual(tokens.RefreshToken, forked.RefreshToken);
        await using (var context = await CreateContextAsync())
        {
            var rows = await context.RefreshSession.Where(x => x.MyUserId == user.Id).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(2, rows.Select(x => x.FamilyId).Distinct().Count());
            Assert.Single(rows.Select(x => x.LoginSessionId).Distinct());
            Assert.Single(rows.Select(x => x.AbsoluteExpiresAtUtc).Distinct());
        }

        // 原本那一串照常可用。
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Fork_ShouldRefuseRotatedTokens_AndApiTokens()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var web = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false))).Data!.Tokens!;
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, web.RefreshToken)).StatusCode);

        var rotated = await client.PostAsJsonAsync("/api/v1/Auth/session/fork", new RefreshTokenRequestDto { RefreshToken = web.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);

        var api = (await Read<TokenResponseDto>(await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password }))).Data!;
        var fromApi = await client.PostAsJsonAsync("/api/v1/Auth/session/fork", new RefreshTokenRequestDto { RefreshToken = api.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, fromApi.StatusCode);
    }

    // ---------- 登出立即生效 ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Logout_ShouldRevokeTheAccessTokenImmediately(bool web)
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = web
            ? (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false))).Data!.Tokens!
            : (await Read<TokenResponseDto>(await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password }))).Data!;
        using var authed = factory.CreateClient();
        Bearer(authed, tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/v1/Auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/Auth/logout", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await authed.GetAsync("/api/v1/Auth/me")).StatusCode);
    }

    // ---------- 沒有主要角色 ----------

    /// <summary>
    /// 缺口（M7 起）：沒有主要角色的帳號，Server 版在換頁檢查時就登出，但網頁換出的 Token 照樣能呼叫 API（/App 等 WASM 頁可用）；
    /// M9a 先以 bridge 換出的 Token 證明了缺口。0.10.8 起網頁管道的 Token 一律 403 <see cref="ApiErrorCodes.NoPrimaryRole"/>；以 API 登入的 Token（App）不受影響。
    /// </summary>
    [Fact]
    public async Task AccountWithoutAPrimaryRole_ShouldNotUseTheWebApp_ButApiLoginStillWorks()
    {
        var user = await AddUserAsync(roleId: null);
        using var client = NoRedirectClient();
        using var webClient = factory.CreateClient();

        var web = (await Read<WebLoginResultDto>(await WebLoginAsync(client, user.Account, false))).Data!.Tokens!;
        Bearer(webClient, web.AccessToken);
        var denied = await webClient.GetAsync("/api/v1/Auth/me");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(ApiErrorCodes.NoPrimaryRole, (await Read<SessionInfoDto>(denied)).ErrorCode);

        // App（API 登入）照舊可用：權限由 RBAC 判斷。
        var api = (await Read<TokenResponseDto>(await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password }))).Data!;
        Bearer(webClient, api.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await webClient.GetAsync("/api/v1/Auth/me")).StatusCode);
    }

    // ---------- 輔助 ----------

    private HttpClient NoRedirectClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void Bearer(HttpClient client, string accessToken)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private static async Task<ApiResult<T>> Read<T>(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ApiResult<T>>())!;

    private static Task<HttpResponseMessage> WebLoginAsync(HttpClient client, string account, bool rememberMe, string? returnUrl = null, string? deviceToken = null)
        => client.PostAsJsonAsync("/api/v1/Auth/login/web", new WebLoginRequestDto
        {
            Account = account,
            Password = Password,
            RememberMe = rememberMe,
            ReturnUrl = returnUrl,
            RememberDeviceToken = deviceToken,
        });

    private static Task<HttpResponseMessage> TwoFactorAsync(HttpClient client, string ticket, string code, bool rememberDevice)
        => client.PostAsJsonAsync("/api/v1/Auth/two-factor", new TwoFactorLoginRequestDto { Ticket = ticket, Code = code, RememberDevice = rememberDevice });

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
        => client.PostAsJsonAsync("/api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = refreshToken });

    private async Task<HttpResponseMessage> GoogleCallbackAsync(string email, string? binding, string? returnUrl)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.ExternalCookieScheme);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, $"google-{Guid.NewGuid():N}"), new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
            "Google");
        var properties = new AuthenticationProperties();
        if (binding is not null)
        {
            properties.Items[GoogleLoginCodes.BindingItemKey] = binding;
        }

        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, MagicObjectHelper.ExternalCookieScheme));
        using var client = NoRedirectClient();
        var url = returnUrl is null ? "/Auths/Google/Callback" : $"/Auths/Google/Callback?returnUrl={Uri.EscapeDataString(returnUrl)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{options.Cookie.Name}={ticket}");
        return await client.SendAsync(request);
    }

    private async Task<int> AddRoleAsync(bool requireTwoFactor = false)
    {
        await using var context = await CreateContextAsync();
        var role = new RoleView { Name = $"m9-{Guid.NewGuid():N}", TabViewJson = "[]", RequireTwoFactor = requireTwoFactor };
        context.RoleView.Add(role);
        await context.SaveChangesAsync();
        return role.Id;
    }

    private async Task<MyUser> AddUserAsync()
        => await AddUserAsync(await AddRoleAsync());

    private async Task<MyUser> AddUserAsync(int? roleId)
    {
        await using var context = await CreateContextAsync();
        var account = $"m9-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Email = $"{account}@example.com",
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            RoleViewId = roleId,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private async Task<(MyUser User, string Secret)> AddEnrolledUserAsync()
    {
        var user = await AddUserAsync();
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var enrollment = service.BeginEnrollment(user.Account);
        var code = new TotpService().ComputeCode(enrollment.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.True((await service.EnableAsync(user.Id, enrollment.Secret, code)).Success);
        return (user, enrollment.Secret);
    }

    private async Task<int> FailedCountAsync(int userId)
    {
        await using var context = await CreateContextAsync();
        return await context.MyUser.Where(x => x.Id == userId).Select(x => x.AccessFailedCount).SingleAsync();
    }

    private async Task<bool> AuditExistsAsync(string action, int userId, string? detail = null)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.AnyAsync(x => x.Action == action && x.ActorUserId == userId && (detail == null || x.Detail == detail));
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();

    /// <summary>伺服器的固定訊息（與 WebAuthController 相同）。</summary>
    private static class WebAuthControllerMessages
    {
        public const string GoogleExpired = "Google 登入已逾時，請重新登入。";
    }
}
