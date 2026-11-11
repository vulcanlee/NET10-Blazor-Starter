using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 目前使用者的載入規則（<see cref="CurrentUserLoader"/>，Web API 每個請求經 <c>ApiActorMiddleware</c> 使用）。
/// 0.10.9 起（WASM 遷移 M10）由原本的 AuthenticationStateHelperTests 改寫：Server 版的換頁檢查已刪除，
/// 導頁的部分不再存在，帳號狀態、改密碼、兩步驟驗證、工作階段版本與權限的規則直接對載入器驗證。
/// </summary>
public sealed class CurrentUserLoaderTests
{
    [Fact]
    public async Task Load_WithMissingUser_ShouldReturnNotFound()
    {
        await using var fixture = await LoaderFixture.CreateAsync();

        var result = await fixture.CreateLoader().LoadAsync(999, "any-stamp");

        Assert.Equal(CurrentUserLoadStatus.NotFound, result.Status);
        Assert.Null(result.CurrentUser);
    }

    [Fact]
    public async Task Load_WithDisabledUser_ShouldReturnDisabled()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(status: false);

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Equal(CurrentUserLoadStatus.Disabled, result.Status);
        Assert.Null(result.CurrentUser);
    }

    /// <summary>0.9.95 起使用者是軟刪除：被刪除後全域過濾器讓他「找不到」，下一個請求就失效。</summary>
    [Fact]
    public async Task Load_WithSoftDeletedUser_ShouldReturnNotFound()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsDeleted, true));

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Equal(CurrentUserLoadStatus.NotFound, result.Status);
    }

    /// <summary>沒有主要角色只回報，由呼叫端決定（網頁的 Token 一律 403 NO_PRIMARY_ROLE）。</summary>
    [Fact]
    public async Task Load_WithMissingRoleView_ShouldReportNoPrimaryRole()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserWithoutRoleAsync();

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Equal(CurrentUserLoadStatus.Ok, result.Status);
        Assert.False(result.HasPrimaryRole);
    }

    /// <summary>權限資料讀不出來時不給目前使用者（呼叫端視為無效帳號）。</summary>
    [Fact]
    public async Task Load_WithInvalidRoleJson_ShouldNotBuildTheCurrentUser()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(roleJson: "not-json");

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Equal(CurrentUserLoadStatus.Ok, result.Status);
        Assert.Null(result.CurrentUser);
    }

    /// <summary>0.9.103 起：工作階段版本與資料庫不符（改密碼、停用、強制登出之後的舊登入），或根本沒帶版本。</summary>
    [Theory]
    [InlineData("old-stamp")]
    [InlineData(null)]
    public async Task Load_WithAStaleOrMissingSecurityStamp_ShouldReturnSessionRevoked(string? stamp)
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync();

        var result = await fixture.CreateLoader().LoadAsync(user.Id, stamp);

        Assert.Equal(CurrentUserLoadStatus.SessionRevoked, result.Status);
        Assert.Null(result.CurrentUser);
    }

    [Fact]
    public async Task Load_WhenPasswordChangeIsRequired_ShouldSaySo()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(mustChangePassword: true);

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Equal(CurrentUserLoadStatus.Ok, result.Status);
        Assert.True(result.MustChangePassword);
    }

    /// <summary>0.9.101 起只看旗標與到期：密碼是 123456 本身不再觸發（登入時才補旗標，見 MyUserServiceLoginTests）。</summary>
    [Fact]
    public async Task Load_WithLegacyDefaultPasswordButNoFlag_ShouldNotRequireAChange()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.False(result.MustChangePassword);
    }

    [Fact]
    public async Task Load_WhenPasswordExpired_ShouldRequireAChange()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(passwordChangedAtUtc: DateTime.UtcNow.AddDays(-31));
        var policy = PasswordTestDefaults.Policy(new PasswordPolicySettings { ExpiryDays = 30 });

        var result = await fixture.CreateLoader(policy).LoadAsync(user.Id, user.SecurityStamp);

        Assert.True(result.MustChangePassword);
    }

    /// <summary>0.9.104 起：必須使用兩步驟驗證卻還沒設定；設定好之後就不再要求。</summary>
    [Fact]
    public async Task Load_WhenTwoFactorIsRequired_ShouldAskForSetupUntilItIsEnabled()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync();
        await fixture.RequireTwoFactorForRoleOfAsync(user);

        Assert.True((await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp)).RequiresTwoFactorSetup);

        await fixture.Context.MyUser.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.TwoFactorEnabled, true).SetProperty(x => x.TwoFactorSecret, "protected"));
        Assert.False((await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp)).RequiresTwoFactorSetup);
    }

    /// <summary>同時要改密碼與設定兩步驟驗證：先改密碼（兩頁互不導向，否則來回跳）。</summary>
    [Fact]
    public async Task Load_WhenPasswordChangeIsAlsoRequired_ShouldAskForThePasswordFirst()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(mustChangePassword: true);
        await fixture.RequireTwoFactorForRoleOfAsync(user);

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.True(result.MustChangePassword);
        Assert.False(result.RequiresTwoFactorSetup);
    }

    [Fact]
    public async Task Load_WithValidUser_ShouldBuildTheCurrentUser_AndApplyToKeepTheRoleList()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddUserAsync(permissionName: "PermissionA");

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Equal(CurrentUserLoadStatus.Ok, result.Status);
        Assert.True(result.HasPrimaryRole);
        Assert.False(result.MustChangePassword);
        Assert.False(result.RequiresTwoFactorSetup);

        var target = new CurrentUserService();
        CurrentUserLoader.ApplyTo(target, result.CurrentUser!);
        Assert.Equal(user.Id, target.CurrentUser.Id);
        Assert.True(target.CurrentUser.IsAuthenticated);
        Assert.Contains("PermissionA", target.CurrentUser.RoleList);
    }

    [Fact]
    public async Task Load_WithMultipleRoles_ShouldUseTheUnionOfPermissions()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddMultiRoleUserAsync(primaryKey: "PermissionA", additionalKey: "PermissionB");

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Contains("PermissionA", result.CurrentUser!.RoleList);
        Assert.Contains("PermissionB", result.CurrentUser.RoleList);
    }

    /// <summary>額外角色的關聯（UserRole）刻意保留，權限判斷必須經 RoleView 過濾，否則已刪除的角色會繼續給權限。</summary>
    [Fact]
    public async Task Load_WithSoftDeletedAdditionalRole_ShouldDropItsPermissions()
    {
        await using var fixture = await LoaderFixture.CreateAsync();
        var user = await fixture.AddMultiRoleUserAsync(primaryKey: "PermissionA", additionalKey: "PermissionB");
        var additionalRoleId = await fixture.Context.UserRole
            .Where(x => x.MyUserId == user.Id && x.RoleViewId != user.RoleViewId)
            .Select(x => x.RoleViewId)
            .SingleAsync();
        await fixture.Context.RoleView.Where(x => x.Id == additionalRoleId).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsDeleted, true));

        var result = await fixture.CreateLoader().LoadAsync(user.Id, user.SecurityStamp);

        Assert.Contains("PermissionA", result.CurrentUser!.RoleList);
        Assert.DoesNotContain("PermissionB", result.CurrentUser.RoleList);
    }

    /// <summary>
    /// 角色矩陣只勾「檢視」時產生的是「頁面:view」而不含裸鍵。若頁面權限只認裸鍵，
    /// 唯讀角色會連頁面都打不開、選單也不顯示 —— 「可看不可改」就只剩 API 端生效。
    /// </summary>
    [Fact]
    public void CanAccessPage_WithOnlyViewActionKey_ShouldAllow()
    {
        var user = new CurrentUser { RoleList = [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View)] };

        Assert.True(PermissionRules.CanAccessPage(user, MagicObjectHelper.角色_分類清單));
    }

    /// <summary>放寬的邊界只到 view：只有 create 的角色仍然不得進入頁面，否則「頁面鍵」會退化成「任何動作鍵都能進」。</summary>
    [Fact]
    public void CanAccessPage_WithOnlyNonViewActionKey_ShouldDeny()
    {
        var user = new CurrentUser { RoleList = [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.Create)] };

        Assert.False(PermissionRules.CanAccessPage(user, MagicObjectHelper.角色_分類清單));
    }

    private sealed class LoaderFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private LoaderFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            mapper = new MapperConfiguration(configuration => configuration.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<LoaderFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            return new LoaderFixture(connection, context);
        }

        public CurrentUserLoader CreateLoader(IPasswordPolicy? passwordPolicy = null)
        {
            var currentUserService = new CurrentUserService();
            var myUserService = new MyUserService(
                new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance),
                new AuditLogService(TestAuditChain.Create(connection), loggerFactory.CreateLogger<AuditLogService>()),
                currentUserService, Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());

            return new CurrentUserLoader(
                loggerFactory.CreateLogger<CurrentUserLoader>(),
                mapper,
                myUserService,
                new RolePermissionService(),
                new EffectiveTeamResolver(Context, new ContextTeamTreeCache(Context), NullLogger<EffectiveTeamResolver>.Instance),
                new PermissionChecker(Context, NullLogger<PermissionChecker>.Instance),
                passwordPolicy ?? PasswordTestDefaults.Policy(),
                new SecurityStampService(new TestDbContextFactory(connection), new SecurityStampCache(), TimeProvider.System, NullLogger<SecurityStampService>.Instance),
                TwoFactorTestDefaults.Service(new TestDbContextFactory(connection), currentUser: currentUserService, settings: new TwoFactorSettings()));
        }

        public async Task<MyUser> AddUserAsync(
            bool status = true,
            string password = "secure-password",
            string roleJson = """["PermissionA"]""",
            string permissionName = "PermissionA",
            bool mustChangePassword = false,
            DateTime? passwordChangedAtUtc = null)
        {
            var roleView = new RoleView
            {
                Name = $"role-{Guid.NewGuid():N}",
                TabViewJson = roleJson == """["PermissionA"]""" ? $"""["{permissionName}"]""" : roleJson,
            };

            Context.RoleView.Add(roleView);
            await Context.SaveChangesAsync();

            // 權限來源是 RBAC 表（IPermissionChecker），種子資料要雙寫 RolePermissionMap。
            await new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance).SyncRolePermissionsAsync(roleView.Id, new[] { permissionName });

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = status,
                RoleViewId = roleView.Id,
                MustChangePassword = mustChangePassword,
                PasswordChangedAtUtc = passwordChangedAtUtc,
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, password);

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task RequireTwoFactorForRoleOfAsync(MyUser user)
            => await Context.RoleView.Where(x => x.Id == user.RoleViewId).ExecuteUpdateAsync(s => s.SetProperty(x => x.RequireTwoFactor, true));

        public async Task<MyUser> AddMultiRoleUserAsync(string primaryKey, string additionalKey)
        {
            var writer = new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance);

            var primaryRole = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = $"""["{primaryKey}"]""" };
            var additionalRole = new RoleView { Name = $"role-{Guid.NewGuid():N}", TabViewJson = $"""["{additionalKey}"]""" };
            Context.RoleView.AddRange(primaryRole, additionalRole);
            await Context.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(primaryRole.Id, new[] { primaryKey });
            await writer.SyncRolePermissionsAsync(additionalRole.Id, new[] { additionalKey });

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true,
                RoleViewId = primaryRole.Id,
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { primaryRole.Id, additionalRole.Id });
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task<MyUser> AddUserWithoutRoleAsync()
        {
            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true,
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
