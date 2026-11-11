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
    /// 偷到刷新 Token 的人先以 session/fork 開一串自己的，之後才被偵測到重用：同一次網頁登入的每一串（含偷開的那串）都要撤銷（0.10.8 獨立審查）。
    /// </summary>
    [Fact]
    public async Task Reuse_ShouldRevokeEveryFamilyOfTheSameWebLogin_IncludingForks()
    {
        var user = AddUser("mallory-victim");
        var issued = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-reuse", null, Lifetime);
        var forked = await store.ForkAsync(issued.Token, (_, _) => Lifetime);
        Assert.Equal(RefreshRotationStatus.Rotated, forked.Status);
        var rotated = await store.RotateAsync(issued.Token, Lifetime);
        time.Advance(RefreshTokenStore.ReuseGracePeriod + TimeSpan.FromSeconds(1));

        var replay = await store.RotateAsync(issued.Token, Lifetime);

        Assert.Equal(RefreshRotationStatus.Reused, replay.Status);
        Assert.NotEqual(RefreshRotationStatus.Rotated, (await store.RotateAsync(forked.Token!.Token, Lifetime)).Status);
        Assert.NotEqual(RefreshRotationStatus.Rotated, (await store.RotateAsync(rotated.Token!.Token, Lifetime)).Status);
    }

    /// <summary>網頁登入已登出（依登入撤銷）之後，不能再以那次登入的 Token 開新的一串。</summary>
    [Fact]
    public async Task Fork_AfterTheLoginWasRevoked_ShouldBeRejected()
    {
        var user = AddUser("fork-after-logout");
        var issued = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-logout", null, Lifetime);
        await store.RevokeByLoginSessionAsync("login-logout", "Logout");

        var forked = await store.ForkAsync(issued.Token, (_, _) => Lifetime);

        Assert.NotEqual(RefreshRotationStatus.Rotated, forked.Status);
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

    // ---------- 0.10.8 起（WASM 遷移 M9a）：網頁管道的效期、新分頁、換一串、這一串是否已撤銷 ----------

    [Fact]
    public async Task Rotate_WithALifetimePolicy_ShouldAskForTheLifetimeOfThisFamilysKind()
    {
        var user = AddUser("alice");
        var web = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-1", null, TimeSpan.FromMinutes(480));
        (string Kind, bool Persistent)? asked = null;

        var rotated = await store.RotateAsync(web.Token, (kind, persistent) =>
        {
            asked = (kind, persistent);
            return TimeSpan.FromMinutes(480);
        });

        Assert.Equal((RefreshClientKinds.Web, false), asked);
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddMinutes(480), rotated.Token!.ExpiresAtUtc);
        Assert.Equal(web.FamilyId, rotated.Token.FamilyId);
        Assert.Equal(RefreshClientKinds.Web, rotated.Token.ClientKind);
    }

    [Fact]
    public async Task Fork_ShouldStartANewFamily_ForTheSameWebLogin_AndLeaveTheSourceUsable()
    {
        var user = AddUser("alice");
        var cap = time.GetUtcNow().UtcDateTime.AddDays(30);
        var source = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Web, "login-1", "Chrome", TimeSpan.FromDays(30), cap);

        var fork = await store.ForkAsync(source.Token, (_, _) => TimeSpan.FromDays(30));

        Assert.Equal(RefreshRotationStatus.Rotated, fork.Status);
        Assert.True(fork.IsPersistent);
        Assert.NotEqual(source.FamilyId, fork.Token!.FamilyId);
        await using (var context = factory.CreateDbContext())
        {
            var forked = await context.RefreshSession.SingleAsync(x => x.FamilyId == fork.Token.FamilyId);
            Assert.Equal("login-1", forked.LoginSessionId);
            Assert.Equal(cap, forked.AbsoluteExpiresAtUtc);
            Assert.Equal(RefreshClientKinds.Web, forked.ClientKind);
        }

        Assert.Equal(RefreshRotationStatus.Rotated, (await store.RotateAsync(source.Token, Lifetime)).Status);
    }

    [Fact]
    public async Task Fork_ShouldRefuseReplacedRevokedOrNonWebTokens_AndStaleSessions()
    {
        var user = AddUser("alice");
        var web = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-1", null, Lifetime);
        var rotated = await store.RotateAsync(web.Token, Lifetime);
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.ForkAsync(web.Token, (_, _) => Lifetime)).Status);

        var api = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, null, null, Lifetime);
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.ForkAsync(api.Token, (_, _) => Lifetime)).Status);

        await UpdateUserAsync(user.Id, x => x.SecurityStamp = SecurityStamps.New());
        Assert.Equal(RefreshRotationStatus.SessionRevoked, (await store.ForkAsync(rotated.Token!.Token, (_, _) => Lifetime)).Status);

        Assert.Equal(RefreshRotationStatus.Invalid, (await store.ForkAsync("unknown", (_, _) => Lifetime)).Status);
    }

    [Fact]
    public async Task ReissueFamily_ShouldReplaceTheFamily_KeepingItsSettings_AndOnlyForItsOwner()
    {
        var user = AddUser("alice");
        var other = AddUser("bob");
        var cap = time.GetUtcNow().UtcDateTime.AddDays(30);
        var issued = await store.IssueAsync(user, isPersistent: true, RefreshClientKinds.WebBridge, "login-1", "Chrome", Lifetime, cap);
        await UpdateUserAsync(user.Id, x => x.SecurityStamp = SecurityStamps.New());
        await using (var read = factory.CreateDbContext())
        {
            user = await read.MyUser.AsNoTracking().SingleAsync(x => x.Id == user.Id);
            other = await read.MyUser.AsNoTracking().SingleAsync(x => x.Id == other.Id);
        }

        Assert.Equal(RefreshRotationStatus.Invalid, (await store.ReissueFamilyAsync(issued.FamilyId, other, (_, _) => Lifetime, "SelfUpdated")).Status);

        var renewed = await store.ReissueFamilyAsync(issued.FamilyId, user, (_, _) => Lifetime, "PasswordChanged");

        Assert.Equal(RefreshRotationStatus.Rotated, renewed.Status);
        Assert.True(renewed.IsPersistent);
        Assert.NotEqual(issued.FamilyId, renewed.Token!.FamilyId);
        Assert.Equal(RefreshClientKinds.WebBridge, renewed.Token.ClientKind);
        Assert.True(renewed.Token.ExpiresAtUtc <= cap);
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.RotateAsync(issued.Token, Lifetime)).Status);
        Assert.Equal(RefreshRotationStatus.Rotated, (await store.RotateAsync(renewed.Token.Token, Lifetime)).Status);
        await using var context = factory.CreateDbContext();
        Assert.Equal("PasswordChanged", (await context.RefreshSession.SingleAsync(x => x.FamilyId == issued.FamilyId)).RevokedReason);
        Assert.Equal("login-1", (await context.RefreshSession.FirstAsync(x => x.FamilyId == renewed.Token.FamilyId)).LoginSessionId);

        // 已經換掉的那一串不能再換一次。
        Assert.Equal(RefreshRotationStatus.Invalid, (await store.ReissueFamilyAsync(issued.FamilyId, user, (_, _) => Lifetime, "again")).Status);
    }

    /// <summary>
    /// 存取 Token 驗證時查的「這一串還在不在」：登出、依登入識別撤銷之後同一個行程立即看到（快取被清掉），
    /// 撤銷前的有效結果快取 <see cref="RefreshTokenStore.FamilyCheckWindow"/>；只是輪替不算撤銷。
    /// </summary>
    [Fact]
    public async Task IsFamilyActive_ShouldFollowLogoutImmediately_AndSurviveRotation()
    {
        var user = AddUser("alice");
        var first = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-1", null, Lifetime);
        var second = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-1", null, Lifetime);
        Assert.True(await store.IsFamilyActiveAsync(first.FamilyId));
        Assert.True(await store.IsFamilyActiveAsync(second.FamilyId));

        var rotated = await store.RotateAsync(first.Token, Lifetime);
        Assert.True(await store.IsFamilyActiveAsync(first.FamilyId));

        await store.RevokeFamilyAsync(rotated.Token!.Token, "Logout");
        Assert.False(await store.IsFamilyActiveAsync(first.FamilyId));

        await store.RevokeByLoginSessionAsync("login-1", "WebLogout");
        Assert.False(await store.IsFamilyActiveAsync(second.FamilyId));

        Assert.False(await store.IsFamilyActiveAsync(string.Empty));
        Assert.False(await store.IsFamilyActiveAsync("unknown"));
    }

    [Fact]
    public async Task IsFamilyActive_ShouldStopOnceTheLastTokenExpires()
    {
        var user = AddUser("alice");
        var issued = await store.IssueAsync(user, isPersistent: false, RefreshClientKinds.Web, "login-1", null, TimeSpan.FromMinutes(10));
        Assert.True(await store.IsFamilyActiveAsync(issued.FamilyId));

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await store.IsFamilyActiveAsync(issued.FamilyId));
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
