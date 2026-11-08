using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 獨立審查找到的竄改手法（0.9.175）：每一種都先重現，再確認抓得到、而且不會被之後的正常寫入或清除洗成正常。
/// </summary>
public sealed class AuditIntegrityReviewFixTests
{
    private static void AssertIssue(AuditIntegrityReport report, string code)
        => Assert.True(report.Issues.Any(x => x.Code == code), $"應回報 {code}，實際：{string.Join(" | ", report.Issues.Select(x => $"{x.Code}: {x.Message}"))}");

    /// <summary>
    /// 改鏈頭的金鑰代號＋偷刪最舊的幾筆：金鑰其實沒換（檢查點記的就是這把），所以是竄改；
    /// 之後的正常寫入不可以把它當成「換了金鑰」而重新簽章。
    /// </summary>
    [Fact]
    public async Task ForgedHeadKeyId_ShouldBeReported_AndNotLaunderedByLaterWrites()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        Assert.False((await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: true)).HasIssues);

        var rows = await fixture.RowsAsync();
        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Chain = 0 AND Seq <= 2");
        await fixture.ExecuteAsync($"UPDATE AuditChainHead SET StartSeq = 3, StartPrevHash = '{rows[2].PrevHash}', KeyId = 'x' WHERE Chain = 0");
        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "HeadTampered");

        await fixture.WriteChangesAsync(1);

        Assert.Equal(AuditChainStore.TamperedMac, (await fixture.HeadAsync()).Mac);
        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "HeadTampered");
    }

    /// <summary>還沒有檢查點、金鑰也不是本行程產生的：鏈頭金鑰代號不同一樣視為竄改。</summary>
    [Fact]
    public async Task ForgedHeadKeyId_WithoutACheckpoint_ShouldStillMarkTheHead()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        await fixture.ExecuteAsync("UPDATE AuditChainHead SET KeyId = 'x' WHERE Chain = 0");

        await fixture.WriteChangesAsync(1);

        Assert.Equal(AuditChainStore.TamperedMac, (await fixture.HeadAsync()).Mac);
    }

    /// <summary>金鑰真的是本行程剛產生的（金鑰檔遺失）：接受換金鑰，鏈頭照常簽章。</summary>
    [Fact]
    public async Task GenuineNewKey_ShouldBeAccepted()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        fixture.KeyStore.Use(Enumerable.Repeat((byte)9, 32).ToArray());
        fixture.KeyStore.CreatedNewKey = true;

        await fixture.WriteChangesAsync(1);

        var head = await fixture.HeadAsync();
        Assert.NotEqual(AuditChainStore.TamperedMac, head.Mac);
        Assert.Equal(3, head.KeySinceSeq);
        var report = await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false);
        Assert.False(report.HasIssues, string.Join(" | ", report.Issues.Select(x => x.Message)));
        Assert.Equal(2, report.KeyMismatchRows);
    }

    /// <summary>把紀錄的時間改舊，想讓排程清除替他刪掉證據：清除前先驗簽章，驗不過就不刪。</summary>
    [Fact]
    public async Task BackdatedRows_ShouldNotBePurged()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        await fixture.ExecuteAsync("UPDATE AuditLog SET OccurredAt = '2000-01-01 00:00:00' WHERE Chain = 0 AND Seq <= 2");

        var removed = await fixture.CreateQueryService().PurgeBeforeAsync(DateTime.UtcNow.AddDays(-365), AuditLogKind.Change);

        Assert.Equal(0, removed);
        Assert.Equal(4, (await fixture.RowsAsync()).Count);
        Assert.Equal(1, (await fixture.HeadAsync()).StartSeq);
        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "BadHash");
    }

    /// <summary>把鏈頭的終點改小：之後的寫入不可以撞唯一索引而全部失敗，要從實際最後一筆接續，並標記鏈頭被改過。</summary>
    [Fact]
    public async Task HeadLastSeqLowered_ShouldNotStopAuditWrites()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        await fixture.ExecuteAsync("UPDATE AuditChainHead SET LastSeq = 1 WHERE Chain = 0");

        await fixture.WriteChangesAsync(1);

        Assert.Equal(new long?[] { 1, 2, 3, 4, 5 }, (await fixture.RowsAsync()).Select(x => x.Seq));
        Assert.Equal(AuditChainStore.TamperedMac, (await fixture.HeadAsync()).Mac);
        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "HeadTampered");
    }

    /// <summary>清空全部後舊紀錄的 Id 空出來：指定小 Id 塞進去的未簽章紀錄不可以冒充「升級前的紀錄」。</summary>
    [Fact]
    public async Task UnsignedRowWithAnOldId_AfterClearAll_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO AuditLog (OccurredAt, Action, Success) VALUES ('2026-01-01 00:00:00', 'Login.Success', 1)");
        await fixture.WriteChangesAsync(2);
        await fixture.CreateQueryService().ClearAllAsync();
        Assert.Equal(0, (await fixture.HeadAsync()).LegacyMaxId);

        await fixture.ExecuteAsync("INSERT INTO AuditLog (Id, OccurredAt, Action, Success) VALUES (1, '2026-01-01 00:00:00', 'Login.Success', 1)");

        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "Unsigned");
    }

    /// <summary>清除分批進行（每批一個交易、鏈頭起點逐批往前推），清完驗證通過。</summary>
    [Fact]
    public async Task Purge_InSmallBatches_ShouldRemoveEverythingExpired_AndStayVerifiable()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(5);
        var chain = new AuditChainStore(fixture.Factory, fixture.KeyStore, fixture.Checkpoints, NullLogger<AuditChainStore>.Instance) { PurgeBatchSize = 2 };

        var (removed, files) = await chain.PurgeAsync(DateTime.UtcNow.AddMinutes(1), AuditLogKind.Change);

        Assert.Equal(5, removed);
        Assert.Equal(5, files.Count);
        Assert.Equal(6, (await fixture.HeadAsync()).StartSeq);
        await fixture.WriteChangesAsync(1, content: null);
        var report = await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false);
        Assert.False(report.HasIssues, string.Join(" | ", report.Issues.Select(x => x.Message)));
    }

    /// <summary>移除唯一索引後塞入序號重複的紀錄：分批游標會跳過它，靠筆數比對抓出來。</summary>
    [Fact]
    public async Task DuplicateSeq_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2, content: null);
        await fixture.ExecuteAsync("DROP INDEX IX_AuditLog_Chain_Seq");
        await fixture.ExecuteAsync(
            "INSERT INTO AuditLog (OccurredAt, Action, Success, Chain, Seq, PrevHash, Hash, KeyId) "
            + "SELECT OccurredAt, Action, Success, Chain, Seq, PrevHash, Hash, KeyId FROM AuditLog WHERE Chain = 0 AND Seq = 2");

        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "DuplicateSeq");
    }

    /// <summary>檢查點檔被改壞：要回報，不可以悄悄當成第一次執行而跳過倒回比對。</summary>
    [Fact]
    public async Task UnreadableCheckpoint_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(1, content: null);
        await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Settings.ExternalFileSystem.AuditKeyPath, AuditChainCheckpointStore.FileName), "{ broken");

        AssertIssue(await fixture.CreateVerifier().VerifyAsync(updateCheckpoint: false), "CheckpointUnreadable");
    }
}
