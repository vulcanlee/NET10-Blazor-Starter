using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Business.Startup;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// 工作階段失效（0.9.103 起）：哪些操作會換工作階段版本、版本快取、ticket 的一次性與時效。
/// </summary>
public sealed class SessionTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero));
    private readonly RecordingAuditLogService audit = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 999_999, Account = "admin" } };
    private readonly List<IDisposable> disposables = [];

    public SessionTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    // ---------- 哪些操作會換版本 ----------

    [Fact]
    public async Task ApplyingAPassword_ShouldRotate_ButTheLoginRehashShouldNot()
    {
        var legacy = AddUser("legacy", legacyPassword: "Legacy-pass1");
        var before = await StampOfAsync(legacy);

        Assert.NotNull((await Login().LoginAsync("legacy", "Legacy-pass1")).User);
        Assert.Equal(before, await StampOfAsync(legacy));

        await using var context = factory.CreateDbContext();
        var user = await context.MyUser.SingleAsync(x => x.Id == legacy);
        await PasswordTestDefaults.Policy().ApplyAsync(context, user, "Passw0rd-9", false);
        await context.SaveChangesAsync();
        Assert.NotEqual(before, await StampOfAsync(legacy));
    }

    [Fact]
    public async Task AdminEdit_ShouldRotateOnlyWhenStatusAdminFlagOrRolesChange()
    {
        var roleA = AddRole("A");
        var roleB = AddRole("B");
        var service = UserService();
        Assert.True((await service.AddAsync(new MyUserAdapterModel { Account = "bob", Name = "bob", Password = "Passw0rd-1", Status = true, RoleViewId = roleA })).Success);
        var id = await IdOfAsync("bob");

        async Task<bool> RotatesAsync(Action<MyUserAdapterModel> change)
        {
            var before = await StampOfAsync(id);
            var model = (await service.GetAsync(id)).Clone();
            var (additional, teams) = await service.GetUserAssignmentsAsync(id);
            model.AdditionalRoleIds = additional;
            model.TeamNames = teams;
            change(model);
            Assert.True((await service.UpdateAsync(model)).Success);
            return before != await StampOfAsync(id);
        }

        Assert.False(await RotatesAsync(m => m.Name = "Bob Lee"));
        Assert.True(await RotatesAsync(m => m.IsAdmin = true));
        Assert.True(await RotatesAsync(m => m.AdditionalRoleIds = [roleB]));
        Assert.False(await RotatesAsync(m => m.Email = "bob@example.com"));
        Assert.True(await RotatesAsync(m => m.RoleViewId = roleB));
        Assert.True(await RotatesAsync(m => m.Password = "Passw0rd-2"));
        Assert.True(await RotatesAsync(m => m.Status = false));
    }

    [Fact]
    public async Task DeleteAndForceLogout_ShouldRotate_ForceLogoutKeepsTheConcurrencyStamp()
    {
        var service = UserService();
        var deleted = AddUser("gone");
        var deletedBefore = await StampOfAsync(deleted);
        Assert.True((await service.DeleteAsync(deleted)).Success);
        Assert.NotEqual(deletedBefore, await StampOfAsync(deleted));

        var id = AddUser("alice");
        var before = await LoadAsync(id);
        Assert.True((await service.ForceLogoutAsync(id)).Success);
        var after = await LoadAsync(id);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.User.ForceLogout);
        Assert.False((await service.ForceLogoutAsync(424242)).Success);
    }

    [Fact]
    public async Task SupportSeeder_ShouldRotateOnlyWhenTheConfiguredPasswordChanges()
    {
        await using (var context = factory.CreateDbContext())
        {
            context.RoleView.Add(new RoleView { Name = MagicObjectHelper.預設角色, TabViewJson = "[]" });
            await context.SaveChangesAsync();
        }

        await SeedSupportAsync("first-password");
        var id = await IdOfAsync("support");
        var first = await StampOfAsync(id);

        await SeedSupportAsync("first-password");
        Assert.Equal(first, await StampOfAsync(id));

        await SeedSupportAsync("second-password");
        Assert.NotEqual(first, await StampOfAsync(id));
    }

    [Fact]
    public async Task Login_ShouldFillAnEmptyStamp()
    {
        var id = AddUser("old", password: "Passw0rd-1");
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, string.Empty));
        }

        var (_, user) = await Login().LoginAsync("old", "Passw0rd-1");

        Assert.NotNull(user);
        Assert.False(string.IsNullOrEmpty(await StampOfAsync(id)));
        Assert.Equal(await StampOfAsync(id), user.SecurityStamp);
    }

    // ---------- 版本快取 ----------

    [Fact]
    public async Task StampService_ShouldCacheWithinMaxAge_AndRotateShouldInvalidateImmediately()
    {
        var id = AddUser("alice");
        var service = StampService();
        var first = await service.GetStateAsync(id, TimeSpan.FromMinutes(5));
        Assert.True(first.IsActive);

        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "changed-elsewhere"));
        }

        Assert.Equal(first.SecurityStamp, (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp);
        Assert.Equal("changed-elsewhere", (await service.GetStateAsync(id, TimeSpan.Zero)).SecurityStamp);

        // 快取過了存活時間就重查（別的行程換掉的版本最晚在這時看到）。
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "changed-again"));
        }

        Assert.Equal("changed-elsewhere", (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp);
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("changed-again", (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp);

        await service.RotateAsync(id);
        var rotated = await service.GetStateAsync(id, TimeSpan.FromMinutes(5));
        Assert.NotEqual("changed-again", rotated.SecurityStamp);
        Assert.False((await service.GetStateAsync(424242, TimeSpan.Zero)).IsActive);
    }

    /// <summary>
    /// ⭐ 實機發現的錯誤：版本在別處換過（停用又啟用、改密碼）之後立刻重新登入，快取還是舊版本，
    /// 新登入的 Cookie 被誤判失效、被踢回登入頁長達 5 分鐘。快取不符時必須再查一次資料庫。
    /// </summary>
    [Fact]
    public async Task IsValid_ShouldRecheckTheDatabaseBeforeRejectingAStaleCache()
    {
        var id = AddUser("alice");
        var service = StampService();
        var old = (await service.GetStateAsync(id, TimeSpan.FromMinutes(5))).SecurityStamp;
        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "fresh-login-stamp"));
        }

        Assert.True(await service.IsValidAsync(id, "fresh-login-stamp", TimeSpan.FromMinutes(5)));
        Assert.False(await service.IsValidAsync(id, old, TimeSpan.FromMinutes(5)));
        Assert.False(await service.IsValidAsync(id, string.Empty, TimeSpan.FromMinutes(5)));
        Assert.False(await service.IsValidAsync(424242, "x", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task StampService_EnsureShouldOnlyFillEmptyStamps()
    {
        var id = AddUser("alice");
        var service = StampService();
        var current = await StampOfAsync(id);
        Assert.Equal(current, await service.EnsureAsync(id));

        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, string.Empty));
        }

        var filled = await service.EnsureAsync(id);
        Assert.False(string.IsNullOrEmpty(filled));
        Assert.Equal(filled, await StampOfAsync(id));
    }

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData("a", "b", false)]
    [InlineData("", "", false)]
    [InlineData(null, "a", false)]
    [InlineData("a", "", false)]
    public void Matches_ShouldTreatEmptyAsNotSet(string? claim, string current, bool expected)
        => Assert.Equal(expected, SecurityStamps.Matches(claim, current));

    // ---------- 共用 ----------

    private SecurityStampService StampService() => new(factory, new SecurityStampCache(), time, NullLogger<SecurityStampService>.Instance);

    private MyUserService UserService()
    {
        var rbacContext = factory.CreateDbContext();
        disposables.Add(rbacContext);
        return new MyUserService(factory, NullLogger<MyUserService>.Instance,
            new RbacWriteService(rbacContext, NullLogger<RbacWriteService>.Instance), audit, currentUser,
            Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());
    }

    private MyUserServiceLogin Login()
    {
        var context = factory.CreateDbContext();
        disposables.Add(context);
        return new MyUserServiceLogin(context, new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
            new RolePermissionService(), audit, PasswordTestDefaults.Lockout(), new RecordingNotificationSender(), TimeProvider.System,
            TwoFactorTestDefaults.Service(factory, audit));
    }

    private async Task SeedSupportAsync(string password)
    {
        await using var context = factory.CreateDbContext();
        await new SupportUserSeeder(context, Options.Create(new BootstrapSettings { SupportPassword = password }), NullLogger<SupportUserSeeder>.Instance)
            .SeedAsync(CancellationToken.None);
    }

    private int AddUser(string account, string password = "Passw0rd-1", string? legacyPassword = null)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser { Account = account, Name = account, Status = true, Salt = "salt" };
        user.Password = legacyPassword is null ? SecurePasswordHasher.HashPassword(password) : PasswordHelper.GetPasswordSHA("salt", legacyPassword);
        context.MyUser.Add(user);
        context.SaveChanges();
        return user.Id;
    }

    private int AddRole(string name)
    {
        using var context = factory.CreateDbContext();
        var role = new RoleView { Name = name, TabViewJson = "[]" };
        context.RoleView.Add(role);
        context.SaveChanges();
        return role.Id;
    }

    private async Task<string> StampOfAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.SecurityStamp).SingleAsync();
    }

    private async Task<MyUser> LoadAsync(int id)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<int> IdOfAsync(string account)
    {
        await using var context = factory.CreateDbContext();
        return await context.MyUser.Where(x => x.Account == account).Select(x => x.Id).SingleAsync();
    }

    public void Dispose()
    {
        foreach (var item in disposables)
        {
            item.Dispose();
        }

        connection.Dispose();
    }
}

/// <summary>工作階段失效的端到端行為（真正的主機）：只收 access token、換版本後刷新被拒；0.10.8 起不再發也不再認登入 Cookie。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SessionIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public SessionIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>
    /// 0.10.8 起（WASM 遷移 M9b）純 Token 登入：登入不發 Cookie，帶著以前的登入 Cookie 也不算登入（登入 Cookie 的驗證機制已移除）。
    /// </summary>
    [Fact]
    public async Task Login_ShouldNotIssueACookie_AndAnOldCookieShouldNotSignAnyoneIn()
    {
        var user = await AddUserAsync(isAdmin: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

        var login = await client.PostAsJsonAsync("/api/v1/Auth/login/web", new WebLoginRequestDto { Account = user.Account, Password = "Passw0rd-1", RememberMe = true });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(login.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(x => x.StartsWith(".MyProject.Auth=", StringComparison.Ordinal)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/me");
        request.Headers.Add("Cookie", ".MyProject.Auth=CfDJ8-anything-from-an-older-version");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldFailAfterRotation_AndRefreshTokensShouldNotWorkAsBearer()
    {
        var user = await AddUserAsync(isAdmin: false, password: "Passw0rd-1");
        using var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = user.Account, Password = "Passw0rd-1" }))
            .Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>();
        var tokens = login!.Data!;

        using (var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/me"))
        {
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.RefreshToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bearer)).StatusCode);
        }

        using (var bearer = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/me"))
        {
            bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
        }

        var refreshed = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        // 0.10.0 起刷新 Token 每次使用就輪替：接下來要用的是剛換到的新 Token（再用舊的會被判定為重用，見 TokenApiIntegrationTests）。
        var current = (await refreshed.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!.RefreshToken;
        await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(user.Id);
        var rejected = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto { RefreshToken = current });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.True(await db.AuditLog.AnyAsync(x => x.Action == AuditActions.Token.RefreshFailed && x.TargetId == user.Id.ToString() && x.Detail == "reason=SessionRevoked"));
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin, string password = "Passw0rd-1")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var account = $"session-{Guid.NewGuid():N}";
        var user = new MyUser { Account = account, Name = account, Password = SecurePasswordHasher.HashPassword(password), Status = true, IsAdmin = isAdmin };
        db.MyUser.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
