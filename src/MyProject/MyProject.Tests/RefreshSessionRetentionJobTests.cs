using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Tests;

/// <summary>刷新 Token 清理（0.10.0 起）：只刪已到期的紀錄；撤銷但未到期的要留著，重用偵測需要。</summary>
public sealed class RefreshSessionRetentionJobTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero));
    private readonly RecordingAuditLogService audit = new();

    public RefreshSessionRetentionJobTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Execute_ShouldDeleteOnlyExpiredRows_KeepingRevokedOnesThatHaveNotExpired()
    {
        var store = new RefreshTokenStore(factory, time, NullLogger<RefreshTokenStore>.Instance);
        var user = AddUser();
        await store.IssueAsync(user, true, RefreshClientKinds.Api, null, null, TimeSpan.FromDays(1));
        var revoked = await store.IssueAsync(user, true, RefreshClientKinds.Api, null, null, TimeSpan.FromDays(10));
        await store.RevokeFamilyAsync(revoked.Token, "Logout");
        time.Advance(TimeSpan.FromDays(2));

        var job = new RefreshSessionRetentionJob(store, audit, time, NullLogger<RefreshSessionRetentionJob>.Instance);
        var result = await job.ExecuteAsync(new ScheduledJobContext(1, "manual", null, null), CancellationToken.None);

        Assert.True(result.Succeeded);
        await using var context = factory.CreateDbContext();
        var left = await context.RefreshSession.SingleAsync();
        Assert.NotNull(left.RevokedAtUtc);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.RefreshSession.AutoPurge && e.Detail == "rows=1; trigger=manual");
    }

    [Fact]
    public async Task Execute_WithNothingExpired_ShouldNotWriteAnAuditEntry()
    {
        var store = new RefreshTokenStore(factory, time, NullLogger<RefreshTokenStore>.Instance);
        await store.IssueAsync(AddUser(), true, RefreshClientKinds.Api, null, null, TimeSpan.FromDays(7));

        var job = new RefreshSessionRetentionJob(store, audit, time, NullLogger<RefreshSessionRetentionJob>.Instance);
        await job.ExecuteAsync(new ScheduledJobContext(1, "manual", null, null), CancellationToken.None);

        Assert.Empty(audit.Entries);
    }

    private MyUser AddUser()
    {
        using var context = factory.CreateDbContext();
        var user = new MyUser { Account = "alice", Name = "alice", Status = true, Salt = "s", Password = "x", SecurityStamp = SecurityStamps.New() };
        context.MyUser.Add(user);
        context.SaveChanges();
        return user;
    }

    public void Dispose() => connection.Dispose();
}
