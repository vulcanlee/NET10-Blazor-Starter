using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 安全戳記一換，快取就要立刻失效（0.10.0 起）。以前改戳記的地方都直接寫資料庫、沒有清快取，
/// 被強制登出、停用或改密碼的人，最多還能再用 <c>ValidationIntervalMinutes</c>（預設 5 分鐘）。
/// 這裡用真的 <see cref="SecurityStampService"/> 與快取，先讓快取記住舊戳記，再改戳記，
/// 不前進時間就必須馬上判定舊戳記無效。
/// </summary>
public sealed class SecurityStampInvalidationTests : IDisposable
{
    private static readonly TimeSpan LongCache = TimeSpan.FromMinutes(5);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly SecurityStampService stamps;
    private readonly List<IDisposable> disposables = [];

    public SecurityStampInvalidationTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        var cache = new SecurityStampCache();
        factory = new TestDbContextFactory(connection, new SecurityStampInvalidationInterceptor(cache, NullLogger<SecurityStampInvalidationInterceptor>.Instance));
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        stamps = new SecurityStampService(factory, cache, TimeProvider.System, NullLogger<SecurityStampService>.Instance);
    }

    [Fact]
    public async Task ForceLogout_ShouldInvalidateTheCachedStampImmediately()
    {
        var id = AddUser("alice");
        var stamp = await PrimeAsync(id);

        Assert.True((await UserService().ForceLogoutAsync(id)).Success);

        Assert.False(await stamps.IsValidAsync(id, stamp, LongCache));
    }

    [Fact]
    public async Task Disabling_ShouldInvalidateTheCachedStampImmediately()
    {
        var id = AddUser("bob");
        var stamp = await PrimeAsync(id);

        Assert.True((await UserService().SetStatusAsync(id, false)).Success);

        Assert.False(await stamps.IsValidAsync(id, stamp, LongCache));
    }

    [Fact]
    public async Task AdminEditInsideATransaction_ShouldInvalidateTheCachedStampImmediately()
    {
        var service = UserService();
        Assert.True((await service.AddAsync(new MyUserAdapterModel { Account = "carol", Name = "carol", Password = "Passw0rd-1", Status = true, RoleViewId = AddRole("A") })).Success);
        var id = await IdOfAsync("carol");
        var stamp = await PrimeAsync(id);

        var model = (await service.GetAsync(id)).Clone();
        var (additional, teams) = await service.GetUserAssignmentsAsync(id);
        model.AdditionalRoleIds = additional;
        model.TeamNames = teams;
        model.IsAdmin = true;
        Assert.True((await service.UpdateAsync(model)).Success);

        Assert.False(await stamps.IsValidAsync(id, stamp, LongCache));
    }

    [Fact]
    public async Task NewPasswordSavedWithoutAnExplicitTransaction_ShouldInvalidateTheCachedStampImmediately()
    {
        var id = AddUser("dave");
        var stamp = await PrimeAsync(id);

        await using (var context = factory.CreateDbContext())
        {
            var user = await context.MyUser.SingleAsync(x => x.Id == id);
            await PasswordTestDefaults.Policy().ApplyAsync(context, user, "Passw0rd-9", false);
            await context.SaveChangesAsync();
        }

        Assert.False(await stamps.IsValidAsync(id, stamp, LongCache));
    }

    [Fact]
    public async Task DeletingTheUser_ShouldInvalidateTheCachedStampImmediately()
    {
        var id = AddUser("erin");
        var stamp = await PrimeAsync(id);

        await using (var context = factory.CreateDbContext())
        {
            await context.MyUser.IgnoreQueryFilters().Where(x => x.Id == id).ExecuteDeleteAsync();
        }

        Assert.False(await stamps.IsValidAsync(id, stamp, LongCache));
    }

    /// <summary>
    /// 查詢途中版本剛好被換掉：攔截器已經清過快取，這次查詢才把讀到的舊版本寫回去，舊登入就又能用到快取過期。
    /// 快取以世代計數防這件事：開始查詢前記下世代，寫回時世代變了就不寫。
    /// </summary>
    [Fact]
    public void Cache_ShouldIgnoreAWriteThatStartedBeforeAClear()
    {
        var cache = new SecurityStampCache();
        var now = DateTimeOffset.UtcNow;

        var staleGeneration = cache.Generation;
        cache.Clear();
        Assert.False(cache.TrySet(1, new UserSessionState("old", true), now, staleGeneration));
        Assert.False(cache.TryGet(1, out _, out _));

        Assert.True(cache.TrySet(1, new UserSessionState("new", true), now, cache.Generation));
        Assert.True(cache.TryGet(1, out var state, out _));
        Assert.Equal("new", state.SecurityStamp);

        var beforeRemove = cache.Generation;
        cache.Remove(1);
        Assert.False(cache.TrySet(1, new UserSessionState("old", true), now, beforeRemove));
    }

    [Theory]
    [InlineData("UPDATE \"MyUser\" SET \"SecurityStamp\" = @p0 WHERE \"Id\" = @p1", true)]
    [InlineData("UPDATE \"MyUser\" AS \"m\"\nSET \"Status\" = @p, \"SecurityStamp\" = @p1\nWHERE \"m\".\"Id\" = @userId", true)]
    [InlineData("DELETE FROM \"MyUser\" WHERE \"Id\" = @p0", true)]
    [InlineData("UPDATE \"MyUser\" SET \"AccessFailedCount\" = @p0 WHERE \"Id\" = @p1", false)]
    [InlineData("INSERT INTO \"MyUser\" (\"Account\", \"SecurityStamp\") VALUES (@p0, @p1)", false)]
    [InlineData("SELECT \"m\".\"SecurityStamp\" FROM \"MyUser\" AS \"m\"", false)]
    [InlineData("UPDATE \"MyUserTeam\" SET \"TeamId\" = @p0", false)]
    public void ChangesSessionState_ShouldOnlyMatchUpdatesOrDeletesThatAffectTheStamp(string sql, bool expected)
        => Assert.Equal(expected, SecurityStampInvalidationInterceptor.ChangesSessionState(sql));

    private async Task<string> PrimeAsync(int id)
    {
        var stamp = await stamps.EnsureAsync(id);
        Assert.True(await stamps.IsValidAsync(id, stamp, LongCache));
        return stamp;
    }

    private MyUserService UserService()
    {
        var rbacContext = factory.CreateDbContext();
        disposables.Add(rbacContext);
        return new MyUserService(factory, NullLogger<MyUserService>.Instance,
            new RbacWriteService(rbacContext, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 999_999, Account = "admin" } },
            Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());
    }

    private int AddUser(string account)
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser { Account = account, Name = account, Status = true, Salt = "salt", Password = SecurePasswordHasher.HashPassword("Passw0rd-1") };
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
