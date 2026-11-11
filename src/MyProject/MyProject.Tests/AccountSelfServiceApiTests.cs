using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// WASM 遷移 M9a（0.10.8 起）：忘記／重設密碼、變更自己的密碼、兩步驟驗證設定的 API。規則與稽核沿用 Server 版的服務；
/// 改密碼、啟用／停用兩步驟驗證後這台裝置拿到新的 Token（舊的一串撤銷、沿用「記住我」與整串上限），其他裝置登出。
/// 寄信用 Pickup（信寫到測試自己的暫存資料夾）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AccountSelfServiceApiTests : IClassFixture<ApiTestApplicationFactoryWithPickupEmail>
{
    private const string Password = "Passw0rd-1";
    private const string NewPassword = "Brand-New-Passw0rd!";
    private readonly ApiTestApplicationFactoryWithPickupEmail factory;

    public AccountSelfServiceApiTests(ApiTestApplicationFactoryWithPickupEmail factory)
    {
        this.factory = factory;
    }

    // ---------- 忘記密碼 ----------

    [Fact]
    public async Task Forgot_ShouldNotRevealWhetherTheAccountExists_ButShouldSaySupportIsNotServed()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();

        var known = await client.PostAsJsonAsync("/api/v1/Auth/password/forgot", new ForgotPasswordRequestDto { Identifier = user.Account });
        var unknown = await client.PostAsJsonAsync("/api/v1/Auth/password/forgot", new ForgotPasswordRequestDto { Identifier = "ghost-" + Guid.NewGuid().ToString("N") });
        var support = await client.PostAsJsonAsync("/api/v1/Auth/password/forgot", new ForgotPasswordRequestDto { Identifier = "support" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal((await Read<object>(known)).Message, (await Read<object>(unknown)).Message);
        Assert.Contains("no-store", known.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, support.StatusCode);
        Assert.Contains("不提供忘記密碼服務", (await Read<object>(support)).ErrorMessage);
        Assert.True(await AuditExistsAsync(AuditActions.Password.ResetRequested, user.Id));
    }

    // ---------- 重設密碼 ----------

    [Fact]
    public async Task Reset_CheckShouldNotConsume_RuleFailuresKeepTheLink_AndASuccessfulResetConsumesIt()
    {
        var user = await AddUserAsync();
        var token = await AddResetTokenAsync(user.Id);
        using var client = factory.CreateClient();

        var check = (await Read<ResetPasswordCheckDto>(await client.PostAsJsonAsync("/api/v1/Auth/password/reset/check", new ResetPasswordCheckRequestDto { Token = token }))).Data!;
        Assert.True(check.Valid);
        Assert.Equal(user.Account, check.Account);
        Assert.False(string.IsNullOrWhiteSpace(check.PasswordRule));

        // 不合規則：不消耗連結，可以改正後再送。
        var weak = await client.PostAsJsonAsync("/api/v1/Auth/password/reset", new ResetPasswordRequestDto { Token = token, NewPassword = "1", ConfirmPassword = "1" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.False((await Read<ResetPasswordResultDto>(weak)).Data!.LinkInvalid);

        var ok = await client.PostAsJsonAsync("/api/v1/Auth/password/reset", new ResetPasswordRequestDto { Token = token, NewPassword = NewPassword, ConfirmPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(await AuditExistsAsync(AuditActions.Password.ResetCompleted, user.Id));

        // 用過就無效。
        var again = await client.PostAsJsonAsync("/api/v1/Auth/password/reset", new ResetPasswordRequestDto { Token = token, NewPassword = NewPassword + "x", ConfirmPassword = NewPassword + "x" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        var body = await Read<ResetPasswordResultDto>(again);
        Assert.True(body.Data!.LinkInvalid);
        Assert.Equal(PasswordResetService.InvalidLinkMessage, body.ErrorMessage);
        Assert.False((await Read<ResetPasswordCheckDto>(await client.PostAsJsonAsync("/api/v1/Auth/password/reset/check", new ResetPasswordCheckRequestDto { Token = token }))).Data!.Valid);

        // 新密碼可以登入。
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login/web", new WebLoginRequestDto { Account = user.Account, Password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ---------- 變更密碼 ----------

    [Fact]
    public async Task ChangePassword_ShouldIssueNewTokensForThisDevice_AndRevokeTheOldOnes()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var old = await WebLoginAsync(client, user.Account, rememberMe: true);
        using var oldClient = BearerClient(old.AccessToken);
        await using (var context = await CreateContextAsync())
        {
            Assert.Single(await context.RefreshSession.Where(x => x.MyUserId == user.Id).ToListAsync());
        }

        var wrong = await oldClient.PostAsJsonAsync("/api/v1/Auth/password/change", new ChangePasswordRequestDto { CurrentPassword = "wrong", NewPassword = NewPassword, ConfirmPassword = NewPassword });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("目前密碼不正確。", (await Read<SessionRenewalDto>(wrong)).ErrorMessage);

        var response = await oldClient.PostAsJsonAsync("/api/v1/Auth/password/change", new ChangePasswordRequestDto { CurrentPassword = Password, NewPassword = NewPassword, ConfirmPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renewal = (await Read<SessionRenewalDto>(response)).Data!;
        var fresh = renewal.Tokens!;
        Assert.True(fresh.IsPersistent);
        Assert.True(await AuditExistsAsync(AuditActions.Password.Changed, user.Id));

        // 舊的存取 Token 與刷新 Token 都不能再用；新的可以。
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("/api/v1/Auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, old.RefreshToken)).StatusCode);
        using var freshClient = BearerClient(fresh.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await freshClient.GetAsync("/api/v1/Auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, fresh.RefreshToken)).StatusCode);

        // 新的一串沿用登入識別、「記住我」與整串上限。
        await using (var context = await CreateContextAsync())
        {
            var rows = await context.RefreshSession.Where(x => x.MyUserId == user.Id).OrderBy(x => x.Id).ToListAsync();
            var first = rows.First();
            var last = rows.Last();
            Assert.NotEqual(first.FamilyId, last.FamilyId);
            Assert.Equal(first.LoginSessionId, last.LoginSessionId);
            Assert.Equal(first.AbsoluteExpiresAtUtc, last.AbsoluteExpiresAtUtc);
            Assert.Equal(RefreshClientKinds.Web, last.ClientKind);
            Assert.All(rows.Where(x => x.FamilyId == first.FamilyId), x => Assert.NotNull(x.RevokedAtUtc));
        }
    }

    [Fact]
    public async Task ChangePassword_WhileForced_ShouldBeAllowed_AndLiftTheGate()
    {
        var user = await AddUserAsync(mustChangePassword: true);
        using var client = factory.CreateClient();
        var tokens = await WebLoginAsync(client, user.Account, rememberMe: false);
        using var forced = BearerClient(tokens.AccessToken);

        // 強制改密碼期間直接呼叫業務 API（不經畫面）一律 403＋錯誤代碼。
        foreach (var blocked in new[]
        {
            await forced.GetAsync("/api/v1/profile"),
            await forced.PostAsJsonAsync("/api/v1/Category/search", new { PageIndex = 1, PageSize = 10 }),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal(ApiErrorCodes.PasswordChangeRequired, (await Read<object>(blocked)).ErrorCode);
        }

        var status = (await Read<ChangePasswordStatusDto>(await forced.GetAsync("/api/v1/Auth/password/change"))).Data!;
        Assert.Equal(user.Account, status.Account);
        Assert.Equal("系統管理員要求你先變更密碼，才能繼續使用系統。", status.RequiredReason);
        Assert.False(status.IsSupportAccount);

        var changed = (await Read<SessionRenewalDto>(await forced.PostAsJsonAsync("/api/v1/Auth/password/change", new ChangePasswordRequestDto { CurrentPassword = Password, NewPassword = NewPassword, ConfirmPassword = NewPassword }))).Data!;
        Assert.False(changed.Tokens!.IsPersistent);

        using var after = BearerClient(changed.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await after.GetAsync("/api/v1/profile")).StatusCode);

        // 舊的存取 Token 直接呼叫業務 API 也繞不過：改密碼前一樣被擋（強制改密碼）、改密碼後已失效。
        Assert.Equal(HttpStatusCode.Unauthorized, (await forced.GetAsync("/api/v1/profile")).StatusCode);
    }

    // ---------- 兩步驟驗證設定 ----------

    [Fact]
    public async Task TwoFactorSetup_ShouldKeepTheSecretOutOfTheDatabaseUntilConfirmed_ThenEnable_Regenerate_AndDisable()
    {
        var user = await AddUserAsync();
        using var client = factory.CreateClient();
        var tokens = await WebLoginAsync(client, user.Account, rememberMe: true);
        using var authed = BearerClient(tokens.AccessToken);

        var status = (await Read<TwoFactorSetupStatusDto>(await authed.GetAsync("/api/v1/Auth/two-factor/setup"))).Data!;
        Assert.False(status.IsEnabled);
        Assert.True(status.HasLocalPassword);

        var start = (await Read<TwoFactorSetupStartDto>(await authed.PostAsync("/api/v1/Auth/two-factor/setup/start", null))).Data!;
        Assert.StartsWith("data:image/png;base64,", start.QrDataUri);
        Assert.False(string.IsNullOrEmpty(start.Secret));
        await using (var context = await CreateContextAsync())
        {
            var row = await context.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
            Assert.False(row.TwoFactorEnabled);
            Assert.True(string.IsNullOrEmpty(row.TwoFactorSecret));
        }

        var wrong = await authed.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/enable", new TwoFactorEnableRequestDto { SetupTicket = start.SetupTicket, Code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var code = new TotpService().ComputeCode(start.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var enabled = (await Read<TwoFactorBackupCodesDto>(await authed.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/enable", new TwoFactorEnableRequestDto { SetupTicket = start.SetupTicket, Code = code }))).Data!;
        Assert.Equal(10, enabled.BackupCodes.Count);
        Assert.True(enabled.Tokens!.IsPersistent);
        Assert.True(await AuditExistsAsync(AuditActions.User.TwoFactorEnable, user.Id));

        // 版本換了：舊的存取 Token 失效；設定票證只能用一次。
        Assert.Equal(HttpStatusCode.Unauthorized, (await authed.GetAsync("/api/v1/Auth/me")).StatusCode);
        using var renewed = BearerClient(enabled.Tokens.AccessToken);
        var replay = await renewed.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/enable", new TwoFactorEnableRequestDto { SetupTicket = start.SetupTicket, Code = code });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        var badRegenerate = await renewed.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/backup-codes", new TwoFactorCodeRequestDto { Code = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, badRegenerate.StatusCode);
        var regenerated = (await Read<TwoFactorBackupCodesDto>(await renewed.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/backup-codes", new TwoFactorCodeRequestDto { Code = enabled.BackupCodes[0] }))).Data!;
        Assert.Equal(10, regenerated.BackupCodes.Count);
        Assert.Null(regenerated.Tokens);
        Assert.True(await AuditExistsAsync(AuditActions.User.TwoFactorBackupCodesRegenerate, user.Id));

        var disabled = await renewed.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/disable", new TwoFactorCodeRequestDto { Code = regenerated.BackupCodes[0] });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var afterDisable = (await Read<SessionRenewalDto>(disabled)).Data!;
        Assert.NotNull(afterDisable.Tokens);
        Assert.True(await AuditExistsAsync(AuditActions.User.TwoFactorDisable, user.Id));
        using var finalClient = BearerClient(afterDisable.Tokens!.AccessToken);
        Assert.False((await Read<TwoFactorSetupStatusDto>(await finalClient.GetAsync("/api/v1/Auth/two-factor/setup"))).Data!.IsEnabled);
    }

    [Fact]
    public async Task TwoFactorSetupTicket_ShouldBeBoundToItsUser()
    {
        var alice = await AddUserAsync();
        var bob = await AddUserAsync();
        using var client = factory.CreateClient();
        using var aliceClient = BearerClient((await WebLoginAsync(client, alice.Account, false)).AccessToken);
        using var bobClient = BearerClient((await WebLoginAsync(client, bob.Account, false)).AccessToken);
        var start = (await Read<TwoFactorSetupStartDto>(await aliceClient.PostAsync("/api/v1/Auth/two-factor/setup/start", null))).Data!;
        var code = new TotpService().ComputeCode(start.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var stolen = await bobClient.PostAsJsonAsync("/api/v1/Auth/two-factor/setup/enable", new TwoFactorEnableRequestDto { SetupTicket = start.SetupTicket, Code = code });

        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Equal("設定已逾時，請重新開始設定。", (await Read<TwoFactorBackupCodesDto>(stolen)).ErrorMessage);
    }

    // ---------- 限流 ----------

    /// <summary>
    /// 新的匿名／敏感端點都算進較嚴格的登入配額；登入頁設定與一般 API 不算。網頁登入（login/web）也不算（0.10.8 獨立審查）：
    /// 照 Server 版「畫面登入不受登入配額、靠帳號鎖定」，只受一般 API 配額，同一個出口 IP 的公司登入尖峰才不會被擋。
    /// </summary>
    [Theory]
    [InlineData("/api/v1/Auth/login", true)]
    [InlineData("/api/Auth/login", true)]
    [InlineData("/api/v1/Auth/login/web", false)]
    [InlineData("/api/v1/Auth/two-factor", true)]
    [InlineData("/api/v1/Auth/two-factor/setup/disable", true)]
    [InlineData("/api/v1/Auth/google/exchange", true)]
    [InlineData("/api/v1/Auth/password/forgot", true)]
    [InlineData("/api/v1/Auth/password/reset/check", true)]
    [InlineData("/api/v1/Auth/password/change", true)]
    [InlineData("/api/v1/Auth/session/fork", true)]
    [InlineData("/api/v1/Auth/login-settings", false)]
    [InlineData("/api/v1/Auth/refresh", false)]
    [InlineData("/api/v1/Auth/me", false)]
    [InlineData("/api/v1/profile", false)]
    public void SensitiveAuthEndpoints_ShouldUseTheLoginQuota(string path, bool expected)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(expected, MyProject.Web.Extensions.ServiceCollectionExtensions.IsLoginRequest(context));
    }

    // ---------- 輔助 ----------

    private HttpClient BearerClient(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<ApiResult<T>> Read<T>(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ApiResult<T>>())!;

    private static async Task<TokenResponseDto> WebLoginAsync(HttpClient client, string account, bool rememberMe)
    {
        var response = await client.PostAsJsonAsync("/api/v1/Auth/login/web", new WebLoginRequestDto { Account = account, Password = Password, RememberMe = rememberMe });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Read<WebLoginResultDto>(response)).Data!.Tokens!;
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
        => client.PostAsJsonAsync("/api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = refreshToken });

    private async Task<MyUser> AddUserAsync(bool mustChangePassword = false)
    {
        await using var context = await CreateContextAsync();
        var role = new RoleView { Name = $"self-{Guid.NewGuid():N}", TabViewJson = "[]" };
        context.RoleView.Add(role);
        await context.SaveChangesAsync();

        var account = $"self-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Email = $"{account}@example.com",
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            RoleViewId = role.Id,
            MustChangePassword = mustChangePassword,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    /// <summary>直接放一筆重設連結（資料庫只存雜湊，與 PasswordResetService 相同的算法），回傳原始 Token。</summary>
    private async Task<string> AddResetTokenAsync(int userId)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await using var context = await CreateContextAsync();
        context.PasswordResetToken.Add(new PasswordResetToken
        {
            MyUserId = userId,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(30),
        });
        await context.SaveChangesAsync();
        return raw;
    }

    private async Task<bool> AuditExistsAsync(string action, int userId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.AnyAsync(x => x.Action == action && x.ActorUserId == userId);
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
