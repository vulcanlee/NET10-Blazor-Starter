using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 稽核雜湊鏈的寫入端（0.9.175 起）：序號連續、兩條鏈分開、只從最舊的一端清除、清空後序號接續、
/// 鏈頭被刪會重建，以及兩個連線同時寫入時序號不重複。
/// </summary>
public sealed class AuditChainStoreTests
{
    [Fact]
    public async Task Append_ShouldNumberEachChainContiguously_AndLinkToThePreviousRow()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        var audit = fixture.CreateAuditService();
        await audit.WriteAsync("Category.Create", targetType: "Category", targetId: "1");
        await audit.WriteAsync("Category.Search", detail: "keyword=a");
        await audit.WriteAsync("Category.Update", targetType: "Category", targetId: "1", content: "Name: A → B");
        await audit.WriteAsync("Page.View", targetId: "/categories");

        var changes = await fixture.RowsAsync(AuditChains.Change);
        var browsing = await fixture.RowsAsync(AuditChains.Browsing);
        Assert.Equal(new long?[] { 1, 2 }, changes.Select(x => x.Seq));
        Assert.Equal(new long?[] { 1, 2 }, browsing.Select(x => x.Seq));
        Assert.Equal(AuditHashV1.Genesis, changes[0].PrevHash);
        Assert.Equal(changes[0].Hash, changes[1].PrevHash);
        Assert.NotNull(changes[1].ContentHash);
        Assert.All(changes.Concat(browsing), x => Assert.Equal(AuditHashV1.ComputeRowHash(fixture.KeyStore.Key.Bytes, x), x.Hash));

        var head = await fixture.HeadAsync(AuditChains.Change);
        Assert.Equal(2, head.LastSeq);
        Assert.Equal(changes[1].Hash, head.LastHash);
        Assert.Equal(AuditHashV1.ComputeHeadMac(fixture.KeyStore.Key.Bytes, head), head.Mac);
    }

    /// <summary>升級前的舊紀錄：第一次寫入時記下它們的最大 Id，兩條鏈的鏈頭一起建立。</summary>
    [Fact]
    public async Task FirstAppend_ShouldCreateBothHeads_RememberingTheLegacyMaxId()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await using (var context = fixture.NewContext())
        {
            context.AuditLog.AddRange(
                new AuditLog { Action = "Login.Success", OccurredAt = DateTime.UtcNow.AddDays(-2) },
                new AuditLog { Action = "Page.View", OccurredAt = DateTime.UtcNow.AddDays(-1) });
            await context.SaveChangesAsync();
        }

        await fixture.WriteChangesAsync(1);

        var change = await fixture.HeadAsync(AuditChains.Change);
        var browsing = await fixture.HeadAsync(AuditChains.Browsing);
        Assert.Equal(2, change.LegacyMaxId);
        Assert.Equal(2, browsing.LegacyMaxId);
        Assert.Equal(0, browsing.LastSeq);
    }

    /// <summary>
    /// 清除只刪每條鏈最舊的一端：時間在進鎖前取，序號和時間偶爾交錯，寧可少刪也不在中間刪出缺口；
    /// 統計的「會清掉幾筆」等於實際刪除筆數；鏈頭起點跟著往前推，清完驗證仍通過。
    /// </summary>
    [Fact]
    public async Task Purge_ShouldOnlyRemoveTheOldestPrefix_EvenWhenTimesAreOutOfOrder()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        var now = DateTime.UtcNow;

        // #1、#2、#4 很舊，#3 是新的（時鐘倒退之類）：只能刪到 #2，#4 要留著。
        foreach (var days in new[] { -400, -390, -1, -380, -1 })
        {
            await fixture.AppendAtAsync(now.AddDays(days));
        }

        var query = fixture.CreateQueryService();
        var threshold = now.AddDays(-365);
        var expected = await query.CountExpiredAsync(threshold, AuditLogKind.Change);
        var removed = await query.PurgeBeforeAsync(threshold, AuditLogKind.Change);

        Assert.Equal(2, removed);
        Assert.Equal(expected, removed);
        Assert.Equal(new long?[] { 3, 4, 5 }, (await fixture.RowsAsync()).Select(x => x.Seq));
        var head = await fixture.HeadAsync();
        Assert.Equal(3, head.StartSeq);
        Assert.Equal((await fixture.RowsAsync())[0].PrevHash, head.StartPrevHash);
        Assert.False((await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false)).HasIssues);
    }

    /// <summary>整條鏈都過期：全部刪掉，起點推到下一筆；之後再寫一筆，驗證仍通過。</summary>
    [Fact]
    public async Task Purge_WhenTheWholeChainExpires_ShouldRestartFromTheNextRow()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await fixture.AppendAtAsync(DateTime.UtcNow.AddDays(-400), content: "Name: A → B");
        }

        var removed = await fixture.CreateQueryService().PurgeBeforeAsync(DateTime.UtcNow.AddDays(-365), AuditLogKind.All);
        Assert.Equal(3, removed);
        Assert.Equal(4, (await fixture.HeadAsync()).StartSeq);

        await fixture.WriteChangesAsync(1);
        Assert.Equal(new long?[] { 4 }, (await fixture.RowsAsync()).Select(x => x.Seq));
        Assert.False((await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false)).HasIssues);
    }

    /// <summary>清空全部：序號接續不歸零（檢查點才不會誤報倒回），驗證從下一筆重新起算。</summary>
    [Fact]
    public async Task ClearAll_ShouldKeepNumbering_AndPassVerificationAfterwards()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(3);

        var result = await fixture.CreateQueryService().ClearAllAsync();
        Assert.True(result.Success);
        await fixture.WriteChangesAsync(1);

        Assert.Equal(new long?[] { 4 }, (await fixture.RowsAsync()).Select(x => x.Seq));
        Assert.False((await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false)).HasIssues);
    }

    /// <summary>鏈頭被刪：下一次寫入依現有紀錄重建並標記重建時間，寫入不中斷。</summary>
    [Fact]
    public async Task Append_WhenTheHeadWasDeleted_ShouldRebuildIt_AndMarkRebuiltAt()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(3);
        await fixture.ExecuteAsync("DELETE FROM AuditChainHead");

        await fixture.WriteChangesAsync(1);

        var head = await fixture.HeadAsync();
        Assert.Equal(4, head.LastSeq);
        Assert.Equal(1, head.StartSeq);
        Assert.NotNull(head.RebuiltAt);
        Assert.Equal(new long?[] { 1, 2, 3, 4 }, (await fixture.RowsAsync()).Select(x => x.Seq));
    }

    /// <summary>鏈頭被改（簽章不對）：之後的寫入不替它重新簽章，以免把竄改洗成正常。</summary>
    [Fact]
    public async Task Append_WhenTheHeadWasTampered_ShouldNotLaunderItsSignature()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(3);
        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Seq = 3");
        var previous = (await fixture.RowsAsync())[1].Hash;
        await fixture.ExecuteAsync($"UPDATE AuditChainHead SET LastSeq = 2, LastHash = '{previous}' WHERE Chain = 0");

        await fixture.WriteChangesAsync(1);

        Assert.Equal(AuditChainStore.TamperedMac, (await fixture.HeadAsync()).Mac);
        var report = await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false);
        Assert.Contains(report.Issues, x => x.Code == "HeadTampered");
    }

    /// <summary>EF 的 BeginTransactionAsync 在 SQLite 是 BEGIN IMMEDIATE：交易一開始就拿寫鎖，跨行程也不會拿到同一個序號。</summary>
    [Fact]
    public async Task BeginTransaction_ShouldTakeTheWriteLockImmediately()
    {
        var path = Path.Combine(Path.GetTempPath(), "audit-lock-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new FileDbContextFactory(path);
            await using (var setup = factory.CreateDbContext())
            {
                await setup.Database.EnsureCreatedAsync();
            }

            await using var holder = factory.CreateDbContext();
            await using var transaction = await holder.Database.BeginTransactionAsync();

            await using var other = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=1");
            await other.OpenAsync();
            await using var command = other.CreateCommand();
            command.CommandText = "INSERT INTO AuditChainHead (Chain, StartSeq, StartPrevHash, LastSeq, LastHash, KeyId, KeySinceSeq, LegacyMaxId, CreatedAt, Mac) VALUES (9, 1, '', 0, '', '', 1, 0, '2026-01-01', '')";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(5, error.SqliteErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    /// <summary>兩個寫入端、各自的連線（模擬兩個行程）同時各寫 50 筆：序號 1～100 不重複、不缺號，驗證通過。</summary>
    [Fact]
    public async Task ConcurrentAppends_FromTwoStoresOnSeparateConnections_ShouldStayContiguous()
    {
        var path = Path.Combine(Path.GetTempPath(), "audit-concurrent-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new FileDbContextFactory(path);
            await using (var setup = factory.CreateDbContext())
            {
                await setup.Database.EnsureCreatedAsync();
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            }

            var keys = new FixedAuditKeyStore();
            var stores = new[] { TestAuditChain.Create(new FileDbContextFactory(path), keys), TestAuditChain.Create(new FileDbContextFactory(path), keys) };
            await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
                stores[i % 2].AppendAsync(new AuditLog { Action = "Category.Update", OccurredAt = DateTime.UtcNow, TargetId = i.ToString() }))));

            await using var context = factory.CreateDbContext();
            var seqs = await context.AuditLog.OrderBy(x => x.Seq).Select(x => x.Seq!.Value).ToListAsync();
            Assert.Equal(Enumerable.Range(1, 100).Select(x => (long)x), seqs);
            Assert.Equal(100, (await context.AuditChainHead.SingleAsync(x => x.Chain == AuditChains.Change)).LastSeq);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    /// <summary>檔案資料庫、每個 context 各開一條連線（不共用連線池），才測得到真正的鎖。</summary>
    private sealed class FileDbContextFactory(string path) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .ConfigureWarnings(w => w.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
                .Options);
    }
}
