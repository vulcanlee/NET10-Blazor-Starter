using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;

namespace MyProject.Tests;

/// <summary>
/// 刷新 Token 的伺服器端紀錄（0.10.0 起）：只存雜湊、每次使用就輪替、舊 Token 被重用整串撤銷、
/// 工作階段版本換掉或帳號停用就失效。
/// </summary>
public sealed class RefreshTokenStoreTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero));
    private readonly RefreshTokenStore store;

    public RefreshTokenStoreTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        store = new RefreshTokenStore(factory, time, NullLogger<RefreshTokenStore>.Instance);
    }

    [Fact]
    public async Task Issue_ShouldStoreOnlyTheHash_WithTheCurrentStampAndClientDetails()
    {
        var user = AddUser("alice");

        var issued = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.WebBridge, "login-1", "Chrome", Lifetime, absoluteExpiresAtUtc: null);

        var row = await SingleSessionAsync();
        Assert.False(string.IsNullOrWhiteSpace(issued.Token));
        Assert.DoesNotContain(issued.Token, row.TokenHash, StringComparison.Ordinal);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.Equal(user.SecurityStamp, row.SecurityStamp);
        Assert.True(row.IsPersistent);
        Assert.Equal(RefreshClientKinds.WebBridge, row.ClientKind);
        Assert.Equal("login-1", row.LoginSessionId);
        Assert.Equal("Chrome", row.DeviceLabel);
        Assert.Equal(time.GetUtcNow().UtcDateTime + Lifetime, row.ExpiresAtUtc);
        Assert.Equal(row.ExpiresAtUtc, issued.ExpiresAtUtc);
    }

    [Fact]
    public async Task Rotate_ShouldIssueANewTokenInTheSameFamily_AndRetireTheOldOne()
    {
        var user = AddUser("alice");
        var first = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Api, null, null, Lifetime);
        time.Advance(TimeSpan.FromHours(1));

        var result = await store.RotateAsync(first.Token, Lifetime);

        Assert.Equal(RefreshRotationStatus.Rotated, result.Status);
        Assert.NotNull(result.Token);
        Assert.NotEqual(first.Token, result.Token!.Token);
        Assert.Equal(user.Id, result.User!.Id);
        Assert.False(result.IsPersistent);

        await using var context = factory.CreateDbContext();
        var rows = await context.RefreshSession.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].FamilyId, rows[1].FamilyId);
        Assert.Equal(rows[1].Id, rows[0].ReplacedById);
        Assert.Equal("Rotated", rows[0].RevokedReason);
        Assert.Null(rows[1].RevokedAtUtc);
        Assert.Equal(time.GetUtcNow().UtcDateTime + Lifetime, rows[1].ExpiresAtUtc);
    }

    [Fact]
    public async Task Rotate_WithAnAlreadyRotatedToken_AfterTheGracePeriod_ShouldRevokeTheWholeFamily()
    {
        var user = AddUser("alice");
        var first = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        var second = await store.RotateAsync(first.Token, Lifetime);
        time.Advance(RefreshTokenStore.ReuseGracePeriod + TimeSpan.FromSeconds(1));

        var replay = await store.RotateAsync(first.Token, Lifetime);

        Assert.Equal(RefreshRotationStatus.Reused, replay.Status);
        Assert.Equal(user.Id, replay.UserId);
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(second.Token!.Token, Lifetime)).Status);
        await using var context = factory.CreateDbContext();
        Assert.All(await context.RefreshSession.ToListAsync(), row => Assert.NotNull(row.RevokedAtUtc));
    }

    /// <summary>
    /// 剛被換掉的 Token 在寬限期內又被用：多半是兩個分頁同時刷新，或刷新的回應在網路上遺失後重送，不是外洩。
    /// 回「已被取代」（401）但不撤銷整串，贏的那一邊拿到的新 Token 仍然有效。
    /// </summary>
    [Fact]
    public async Task Rotate_WithAJustRotatedToken_WithinTheGracePeriod_ShouldBeSuperseded_WithoutRevokingTheLogin()
    {
        var user = AddUser("alice");
        var first = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        var second = await store.RotateAsync(first.Token, Lifetime);
        time.Advance(RefreshTokenStore.ReuseGracePeriod - TimeSpan.FromSeconds(1));

        var replay = await store.RotateAsync(first.Token, Lifetime);

        Assert.Equal(RefreshRotationStatus.Superseded, replay.Status);
        Assert.Equal(user.Id, replay.UserId);
        Assert.Equal(RefreshRotationStatus.Rotated, (await store.RotateAsync(second.Token!.Token, Lifetime)).Status);
    }

    [Fact]
    public async Task Issue_WithAnAbsoluteExpiry_ShouldNeverLetTheLoginOutliveIt()
    {
        var user = AddUser("alice");
        var cap = time.GetUtcNow().UtcDateTime.AddDays(1);

        var issued = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.WebBridge, "login-1", null, Lifetime, absoluteExpiresAtUtc: cap);
        Assert.Equal(cap, issued.ExpiresAtUtc);

        time.Advance(TimeSpan.FromHours(12));
        var rotated = await store.RotateAsync(issued.Token, Lifetime);
        Assert.Equal(RefreshRotationStatus.Rotated, rotated.Status);
        Assert.Equal(cap, rotated.Token!.ExpiresAtUtc);

        time.Advance(TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1));
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(rotated.Token.Token, Lifetime)).Status);
    }

    [Fact]
    public async Task Rotate_WithAnUnknownOrExpiredToken_ShouldBeInvalid()
    {
        var user = AddUser("alice");
        var issued = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);

        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync("not-a-real-token", Lifetime)).Status);
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(string.Empty, Lifetime)).Status);

        time.Advance(Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(issued.Token, Lifetime)).Status);
    }

    [Fact]
    public async Task Rotate_AfterTheSecurityStampChanged_ShouldRevokeTheFamily()
    {
        var user = AddUser("alice");
        var issued = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        await UpdateUserAsync(user.Id, u => u.SecurityStamp = SecurityStamps.New());

        var result = await store.RotateAsync(issued.Token, Lifetime);

        Assert.Equal(RefreshRotationStatus.SessionRevoked, result.Status);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal("SessionRevoked", (await SingleSessionAsync()).RevokedReason);
    }

    [Fact]
    public async Task Rotate_ForADisabledOrDeletedUser_ShouldBeUserInactive()
    {
        var disabled = AddUser("disabled");
        var deleted = AddUser("deleted");
        var disabledToken = await store.IssueAsync(disabled, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        var deletedToken = await store.IssueAsync(deleted, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        await UpdateUserAsync(disabled.Id, u => u.Status = false);
        await UpdateUserAsync(deleted.Id, u => u.IsDeleted = true);

        Assert.Equal(RefreshRotationStatus.UserInactive, (await store.RotateAsync(disabledToken.Token, Lifetime)).Status);
        Assert.Equal(RefreshRotationStatus.UserInactive, (await store.RotateAsync(deletedToken.Token, Lifetime)).Status);
    }

    [Fact]
    public async Task RevokeFamily_ShouldEndTheWholeLogin_AndReportTheOwner()
    {
        var user = AddUser("alice");
        var first = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        var second = await store.RotateAsync(first.Token, Lifetime);

        Assert.Equal(user.Id, await store.RevokeFamilyAsync(second.Token!.Token, "Logout"));

        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(second.Token.Token, Lifetime)).Status);
        Assert.Null(await store.RevokeFamilyAsync("not-a-real-token", "Logout"));
    }

    [Fact]
    public async Task RevokeByLoginSession_ShouldOnlyEndTokensFromThatWebLogin()
    {
        var user = AddUser("alice");
        var mine = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.WebBridge, "login-1", null, Lifetime);
        var other = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.WebBridge, "login-2", null, Lifetime);

        Assert.Equal(1, await store.RevokeByLoginSessionAsync("login-1", "Logout"));

        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(mine.Token, Lifetime)).Status);
        Assert.Equal(RefreshRotationStatus.Rotated, (await store.RotateAsync(other.Token, Lifetime)).Status);
    }

    [Fact]
    public async Task ConcurrentRotationsOfTheSameToken_ShouldLetOnlyOneSucceed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"refresh-{Guid.NewGuid():N}.db");
        try
        {
            var fileFactory = new FileDbContextFactory(path);
            await using (var context = fileFactory.CreateDbContext())
            {
                await context.Database.EnsureCreatedAsync();
            }

            var fileStore = new RefreshTokenStore(fileFactory, time, NullLogger<RefreshTokenStore>.Instance);
            MyUser user;
            await using (var context = fileFactory.CreateDbContext())
            {
                user = NewUser("alice");
                context.MyUser.Add(user);
                await context.SaveChangesAsync();
            }

            var issued = await fileStore.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => fileStore.RotateAsync(issued.Token, Lifetime))));

            // 同時刷新（例如兩個分頁）：只有一個換到新 Token，其他回「已被取代」，整串不撤銷、贏家的新 Token 仍然有效。
            var winner = Assert.Single(results, r => r.Status == RefreshRotationStatus.Rotated);
            Assert.All(results.Where(r => r.Status != RefreshRotationStatus.Rotated), r => Assert.Equal(RefreshRotationStatus.Superseded, r.Status));
            Assert.Equal(RefreshRotationStatus.Rotated, (await fileStore.RotateAsync(winner.Token!.Token, Lifetime)).Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PurgeExpired_ShouldDeleteOnlyRowsPastTheCutoff()
    {
        var user = AddUser("alice");
        await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, TimeSpan.FromDays(1));
        await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, TimeSpan.FromDays(10));

        var removed = await store.PurgeExpiredAsync(time.GetUtcNow().UtcDateTime.AddDays(2));

        Assert.Equal(1, removed);
        await using var context = factory.CreateDbContext();
        Assert.Equal(1, await context.RefreshSession.CountAsync());
    }

    private MyUser AddUser(string account)
    {
        using var context = factory.CreateDbContext();
        var user = NewUser(account);
        context.MyUser.Add(user);
        context.SaveChanges();
        return user;
    }

    private static MyUser NewUser(string account) => new()
    {
        Account = account,
        Name = account,
        Status = true,
        Salt = "salt",
        Password = "x",
        SecurityStamp = SecurityStamps.New(),
    };

    private async Task UpdateUserAsync(int id, Action<MyUser> change)
    {
        await using var context = factory.CreateDbContext();
        var user = await context.MyUser.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
        change(user);
        await context.SaveChangesAsync();
    }

    private async Task<RefreshSession> SingleSessionAsync()
    {
        await using var context = factory.CreateDbContext();
        return await context.RefreshSession.SingleAsync();
    }

    public void Dispose() => connection.Dispose();

    /// <summary>每個 context 各開一條連線到同一個檔案，才能真的平行執行。</summary>
    private sealed class FileDbContextFactory(string path) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite($"Data Source={path};Foreign Keys=True;Default Timeout=30").Options);
    }
}
