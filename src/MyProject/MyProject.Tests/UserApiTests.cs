using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 使用者的 Web API（0.10.4 起，WASM 遷移 M5）：只限管理員，走與畫面相同的 <c>MyUserService</c>／<c>TwoFactorService</c>
/// （support 與自己的保護、密碼原則、角色與團隊、並行衝突、軟刪除），稽核每個動作一筆。
/// 停用、強制登出、改角色會換對方的工作階段版本：對方下一個 API 呼叫就被登出；改到自己時回傳換發登入的網址。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class UserApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private const string BaseUrl = "/api/v1/users";
    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public UserApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task FullLifecycle_ShouldWork_AndAuditEachActionOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        await AddRoleAsync(Unique("額外角色"));
        var options = await SendAsync<UserFormOptionsDto>(client, HttpMethod.Get, $"{BaseUrl}/options");
        var team = await AddTeamAsync(Unique("隊"));
        var extraRole = options.Roles.First(x => x.Id != options.DefaultRoleId);
        var account = Unique("new-");

        var created = await SendAsync<UserDto>(client, HttpMethod.Post, BaseUrl, new UserDto
        {
            Account = account,
            Name = "新同仁",
            Password = "Abcd!2345678",
            MustChangePassword = true,
            RoleViewId = options.DefaultRoleId,
            AdditionalRoleIds = [extraRole.Id],
            TeamNames = [team.Name],
        });
        var fetched = await SendAsync<UserDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}");
        Assert.Equal([extraRole.Id], fetched.AdditionalRoleIds);
        Assert.Equal([team.Name], fetched.TeamNames);
        Assert.Null(fetched.Password);

        fetched.Name = "改過的名字";
        var saved = await SendAsync<UserSaveResultDto>(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}", fetched);
        Assert.Null(saved.SessionRefreshUrl);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{BaseUrl}/{created.Id}", fetched)).StatusCode);

        await SendAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}/enabled", new SetEnabledRequestDto { Enabled = false });
        await SendAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}/enabled", new SetEnabledRequestDto { Enabled = true });
        await SendAsync<object>(client, HttpMethod.Post, $"{BaseUrl}/{created.Id}/force-logout");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}");
        var deleted = await SendAsync<PagedResult<UserDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new UserSearchRequestDto { Keyword = account, Deleted = true });
        Assert.True(Assert.Single(deleted.Items).IsDeleted);
        await SendAsync<object>(client, HttpMethod.Post, $"{BaseUrl}/{created.Id}/restore");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}/purge");

        Assert.Equal(
            [
                AuditActions.User.Create, AuditActions.User.View, AuditActions.User.Update, AuditActions.User.Disable, AuditActions.User.Enable,
                AuditActions.User.ForceLogout, AuditActions.User.Delete, AuditActions.User.Restore, AuditActions.User.Delete, AuditActions.User.Purge,
            ],
            await AuditActionsForAsync(created.Id, admin.Id));
    }

    [Fact]
    public async Task ChangingSomeoneElse_ShouldSignThemOutOnTheirNextCall()
    {
        using var adminClient = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        await AddRoleAsync(Unique("換過去的角色"));
        var options = await SendAsync<UserFormOptionsDto>(adminClient, HttpMethod.Get, $"{BaseUrl}/options");

        // 改角色：對方下一個呼叫 401。
        var target = await AddUserAsync(isAdmin: false);
        using var targetClient = await SignedInClientAsync(target);
        Assert.Equal(HttpStatusCode.OK, (await targetClient.GetAsync("/api/v1/profile")).StatusCode);
        var opened = await SendAsync<UserDto>(adminClient, HttpMethod.Get, $"{BaseUrl}/{target.Id}");
        opened.RoleViewId = options.Roles.First(x => x.Id != opened.RoleViewId).Id;
        await SendAsync<UserSaveResultDto>(adminClient, HttpMethod.Put, $"{BaseUrl}/{target.Id}", opened);
        Assert.Equal(HttpStatusCode.Unauthorized, (await targetClient.GetAsync("/api/v1/profile")).StatusCode);

        // 停用：對方的刷新也被拒。
        var disabled = await AddUserAsync(isAdmin: false);
        var (disabledClient, refreshToken) = await SignedInWithRefreshAsync(disabled);
        using (disabledClient)
        {
            await SendAsync<object>(adminClient, HttpMethod.Put, $"{BaseUrl}/{disabled.Id}/enabled", new SetEnabledRequestDto { Enabled = false });
            Assert.Equal(HttpStatusCode.Unauthorized, (await disabledClient.GetAsync("/api/v1/profile")).StatusCode);
            var refresh = await factory.CreateClient().PostAsJsonAsync("/api/v1/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = refreshToken });
            Assert.NotEqual(HttpStatusCode.OK, refresh.StatusCode);
        }

        // 強制登出、重設兩步驟驗證：同樣立即失效。
        var twoFactor = await AddUserAsync(isAdmin: false, twoFactorEnabled: true);
        await SendAsync<object>(adminClient, HttpMethod.Post, $"{BaseUrl}/{twoFactor.Id}/two-factor/reset");
        await using (var context = await CreateContextAsync())
        {
            Assert.False((await context.MyUser.SingleAsync(x => x.Id == twoFactor.Id)).TwoFactorEnabled);
        }
    }

    [Fact]
    public async Task EditingYourself_ShouldStaySignedIn_ThroughARefreshUrl_AndBeProtectedFromSelfHarm()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var me = await SendAsync<UserDto>(client, HttpMethod.Get, $"{BaseUrl}/{admin.Id}");
        me.Password = "NewPass!23456";
        var saved = await SendAsync<UserSaveResultDto>(client, HttpMethod.Put, $"{BaseUrl}/{admin.Id}", me);
        Assert.StartsWith("/Auths/RefreshSession?ticket=", saved.SessionRefreshUrl);
        Assert.EndsWith("returnUrl=%2Fmyusers", saved.SessionRefreshUrl);

        using var again = await SignedInClientAsync(admin, password: "NewPass!23456");
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PostAsync($"{BaseUrl}/{admin.Id}/force-logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PostAsync($"{BaseUrl}/{admin.Id}/two-factor/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PutAsJsonAsync($"{BaseUrl}/{admin.Id}/enabled", new SetEnabledRequestDto { Enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await again.DeleteAsync($"{BaseUrl}/{admin.Id}")).StatusCode);

        var row = (await SendAsync<PagedResult<UserDto>>(again, HttpMethod.Post, $"{BaseUrl}/search", new UserSearchRequestDto { Keyword = admin.Account })).Items.Single();
        Assert.False(row.CanForceLogout);
        Assert.False(row.CanDelete);
        Assert.False(row.CanToggleStatus);
    }

    [Fact]
    public async Task Copy_ShouldStartFromTheTemplate_AndRecordTheSource()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var team = await AddTeamAsync(Unique("複製隊"));
        var source = await AddUserAsync(isAdmin: false, teamId: team.Id, email: $"{Unique("src")}@e2e.test");
        int supportId;
        await using (var context = await CreateContextAsync())
        {
            supportId = (await context.MyUser.SingleAsync(x => x.Account == "support")).Id;
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BaseUrl}/{supportId}/copy-draft")).StatusCode);
        var draft = await SendAsync<UserCopyDraftDto>(client, HttpMethod.Get, $"{BaseUrl}/{source.Id}/copy-draft");
        Assert.Equal([team.Name], draft.Draft.TeamNames);
        Assert.Equal(string.Empty, draft.Draft.Account);

        var generated = await SendAsync<string>(client, HttpMethod.Get, $"{BaseUrl}/password/generate");
        Assert.True(generated.Length >= 8);
        var usedBy = await SendAsync<List<string>>(client, HttpMethod.Post, $"{BaseUrl}/accounts-using-email", new EmailLookupRequestDto { Email = source.Email! });
        Assert.Equal([source.Account], usedBy);

        draft.Draft.Account = Unique("copy-");
        draft.Draft.Name = "複製的人";
        draft.Draft.Password = generated;
        draft.Draft.CopiedFromAccount = source.Account;
        var created = await SendAsync<UserDto>(client, HttpMethod.Post, BaseUrl, draft.Draft);

        await using var check = await CreateContextAsync();
        var audit = await check.AuditLog.SingleAsync(x => x.Action == AuditActions.User.Create && x.TargetId == created.Id.ToString());
        Assert.Contains($"copiedFrom={source.Account}", audit.Detail);
    }

    [Fact]
    public async Task Search_ShouldFlagSupport_AndExportAnXlsx()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var support = (await SendAsync<PagedResult<UserDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new UserSearchRequestDto { Keyword = "support", RecordSearch = true })).Items.Single(x => x.Account == "support");
        Assert.False(support.CanDelete);
        Assert.False(support.CanCopy);
        Assert.Equal(1, await CountAuditAsync(AuditActions.User.Search, admin.Id));

        var export = await client.PostAsJsonAsync($"{BaseUrl}/export", new UserSearchRequestDto { Keyword = "support" });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var workbook = new XLWorkbook(await export.Content.ReadAsStreamAsync());
        Assert.Equal("帳號", workbook.Worksheet(1).Cell(1, 1).GetString());
        Assert.Equal(1, await CountAuditAsync(AuditActions.User.Export, admin.Id));
    }

    [Fact]
    public async Task Unlock_ShouldClearTheLockout()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var locked = await AddUserAsync(isAdmin: false, lockedUntilUtc: DateTime.UtcNow.AddHours(1));

        var row = (await SendAsync<PagedResult<UserDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new UserSearchRequestDto { Keyword = locked.Account })).Items.Single();
        Assert.StartsWith("鎖定至 ", row.LockedText);
        await SendAsync<object>(client, HttpMethod.Post, $"{BaseUrl}/{locked.Id}/unlock");

        var after = (await SendAsync<PagedResult<UserDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new UserSearchRequestDto { Keyword = locked.Account })).Items.Single();
        Assert.Null(after.LockedText);
    }

    /// <summary>
    /// 審查修正：support 不能被改帳號（改了名就繞過「不可停用、不可刪除」）；必填欄位由伺服器驗證；
    /// 產生的密碼不可被快取；「複製」建立前可先檢查帳號（與 Server 版相同的順序）；每頁筆數不合法回 400。
    /// </summary>
    [Fact]
    public async Task ServerSideRules_ShouldHoldWithoutTheScreen()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        int supportId;
        await using (var context = await CreateContextAsync())
        {
            supportId = (await context.MyUser.SingleAsync(x => x.Account == "support")).Id;
        }

        var support = await SendAsync<UserDto>(client, HttpMethod.Get, $"{BaseUrl}/{supportId}");
        support.Account = "not-support";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{BaseUrl}/{supportId}", support)).StatusCode);
        await using (var context = await CreateContextAsync())
        {
            Assert.True(await context.MyUser.AnyAsync(x => x.Id == supportId && x.Account == "support"));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(BaseUrl, new UserDto { Account = "", Name = "", Password = "Abcd!2345678x" })).StatusCode);

        var generated = await client.GetAsync($"{BaseUrl}/password/generate");
        Assert.True(generated.Headers.CacheControl?.NoStore, "產生的密碼不可被快取");

        var existing = await AddUserAsync(isAdmin: false);
        var check = await client.PostAsJsonAsync($"{BaseUrl}/check", new UserDto { Account = existing.Account, Name = "x", Password = "Abcd!2345678x" });
        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{BaseUrl}/search", new UserSearchRequestDto { PageSize = 0 })).StatusCode);
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbidden()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: false));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{BaseUrl}/search", new UserSearchRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{BaseUrl}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(BaseUrl, new UserDto { Account = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{BaseUrl}/1/force-logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{BaseUrl}/password/generate")).StatusCode);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private async Task<List<string>> AuditActionsForAsync(int targetUserId, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.Where(x => x.TargetType == "MyUser" && x.TargetId == targetUserId.ToString() && x.ActorUserId == actorId)
            .OrderBy(x => x.Id).Select(x => x.Action).ToListAsync();
    }

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user, string password = Password, bool skipLogin = false)
    {
        var client = factory.CreateClient();
        if (skipLogin)
        {
            return client;
        }

        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<(HttpClient Client, string RefreshToken)> SignedInWithRefreshAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, tokens.RefreshToken);
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin, int? teamId = null, string? email = null, DateTime? lockedUntilUtc = null, bool twoFactorEnabled = false)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = Unique("m5u-");
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Email = email,
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
            LockoutEndUtc = lockedUntilUtc,
            AccessFailedCount = lockedUntilUtc is null ? 0 : 5,
            TwoFactorEnabled = twoFactorEnabled,
            TwoFactorSecret = twoFactorEnabled ? "JBSWY3DPEHPK3PXP" : null,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);
        if (teamId is { } id)
        {
            context.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = id });
            await context.SaveChangesAsync();
        }

        return user;
    }

    private async Task AddRoleAsync(string name)
    {
        await using var context = await CreateContextAsync();
        context.RoleView.Add(new RoleView { Name = name, TabViewJson = "[]" });
        await context.SaveChangesAsync();
    }

    private async Task<Team> AddTeamAsync(string name)
    {
        await using var context = await CreateContextAsync();
        var team = new Team { Name = name, IsEnabled = true };
        context.Team.Add(team);
        await context.SaveChangesAsync();
        factory.Services.GetRequiredService<ITeamTreeCache>().Invalidate();
        return team;
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
