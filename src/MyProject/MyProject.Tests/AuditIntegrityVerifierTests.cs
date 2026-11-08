using Microsoft.EntityFrameworkCore;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 稽核紀錄完整性檢查（0.9.175 起）。每一種竄改都用原生 SQL 或直接改檔，模擬「拿到資料庫、沒拿到金鑰」的人；
/// 正常的清除、清空與升級前的舊紀錄都不可以誤報。
/// </summary>
public sealed class AuditIntegrityVerifierTests
{
    private static async Task<AuditIntegrityReport> VerifyAsync(AuditIntegrityFixture fixture, bool updateCheckpoint = false)
        => await fixture.CreateVerifier().VerifyAsync(updateCheckpoint);

    private static void AssertIssue(AuditIntegrityReport report, string code)
        => Assert.True(report.Issues.Any(x => x.Code == code), $"應回報 {code}，實際：{string.Join(" | ", report.Issues.Select(x => $"{x.Code}: {x.Message}"))}");

    [Fact]
    public async Task UntouchedChains_ShouldPass_AndCountLegacyRowsSeparately()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO AuditLog (OccurredAt, Action, Success) VALUES ('2026-01-01 00:00:00', 'Login.Success', 1)");
        await fixture.WriteChangesAsync(3);
        await fixture.CreateAuditService().WriteAsync("Page.View", targetId: "/");

        var report = await VerifyAsync(fixture);

        Assert.False(report.HasIssues, string.Join(" | ", report.Issues.Select(x => x.Message)));
        Assert.Equal(4, report.SignedRows);
        Assert.Equal(4, report.VerifiedRows);
        Assert.Equal(1, report.LegacyRows);
    }

    [Fact]
    public async Task ModifiedRow_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(3);
        await fixture.ExecuteAsync("UPDATE AuditLog SET Detail = '改過了' WHERE Chain = 0 AND Seq = 2");

        AssertIssue(await VerifyAsync(fixture), "BadHash");
    }

    [Fact]
    public async Task DeletedMiddleRow_ShouldBeReportedAsAGap()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Chain = 0 AND Seq = 2");

        AssertIssue(await VerifyAsync(fixture), "Gap");
    }

    /// <summary>刪掉最新幾筆：和鏈頭對不上；之後再寫一筆仍然持續回報（下一筆從鏈頭接，不從剩下的最後一筆接）。</summary>
    [Fact]
    public async Task DeletedNewestRows_ShouldStayReported_EvenAfterNewWrites()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Chain = 0 AND Seq >= 3");
        AssertIssue(await VerifyAsync(fixture), "TailMissing");

        await fixture.WriteChangesAsync(1);
        AssertIssue(await VerifyAsync(fixture), "Gap");
    }

    /// <summary>偷刪最舊的幾筆（沒經過正常清除，鏈頭起點沒動）。</summary>
    [Fact]
    public async Task DeletedOldestRows_WithoutAPurge_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Chain = 0 AND Seq <= 2");

        AssertIssue(await VerifyAsync(fixture), "Gap");
    }

    [Fact]
    public async Task TamperedHead_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(4);
        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Chain = 0 AND Seq <= 2");
        await fixture.ExecuteAsync("UPDATE AuditChainHead SET StartSeq = 3 WHERE Chain = 0");

        AssertIssue(await VerifyAsync(fixture), "HeadTampered");
    }

    [Fact]
    public async Task UnsignedRowInsertedAfterProtectionStarted_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(1);
        // 時間故意填得很舊：不能靠時間判斷是不是升級前的紀錄。
        await fixture.ExecuteAsync("INSERT INTO AuditLog (OccurredAt, Action, Success) VALUES ('2020-01-01 00:00:00', 'Login.Success', 1)");

        AssertIssue(await VerifyAsync(fixture), "Unsigned");
    }

    /// <summary>把被改過的那筆金鑰代號也改掉，想從「異常」降級成「金鑰不同」：目前金鑰起算之後的紀錄不可以。</summary>
    [Fact]
    public async Task ForgedKeyId_ShouldBeReported_NotDowngradedToAWarning()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(3);
        await fixture.ExecuteAsync("UPDATE AuditLog SET Detail = '改過了', KeyId = 'deadbeefdeadbeef' WHERE Chain = 0 AND Seq = 2");

        var report = await VerifyAsync(fixture);
        AssertIssue(report, "KeyIdForged");
        Assert.Equal(0, report.KeyMismatchRows);
    }

    [Fact]
    public async Task ModifiedOrMissingContentFile_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        var rows = await fixture.RowsAsync();
        var first = fixture.ContentPath(rows[0].ContentFile)!;
        var second = fixture.ContentPath(rows[1].ContentFile)!;

        await File.WriteAllTextAsync(first, "Name: A → C");
        File.Delete(second);

        var report = await VerifyAsync(fixture);
        AssertIssue(report, "ContentModified");
        AssertIssue(report, "ContentMissing");
    }

    /// <summary>
    /// 金鑰遺失後重新產生：之前的紀錄只能確認序號連續（金鑰不同，警告，不算問題）；
    /// 排程那一次報一次「金鑰變更」，之後以新狀態為基準。
    /// </summary>
    [Fact]
    public async Task KeyChange_ShouldDowngradeOlderRowsToWarnings_AndAlarmOnceInTheScheduledRun()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        Assert.False((await VerifyAsync(fixture, updateCheckpoint: true)).HasIssues);

        fixture.KeyStore.Use(Enumerable.Repeat((byte)7, 32).ToArray());
        await fixture.WriteChangesAsync(1);

        var first = await VerifyAsync(fixture, updateCheckpoint: true);
        AssertIssue(first, "KeyChanged");
        Assert.Equal(2, first.KeyMismatchRows);
        Assert.Equal(1, first.VerifiedRows);

        var second = await VerifyAsync(fixture, updateCheckpoint: true);
        Assert.False(second.HasIssues, string.Join(" | ", second.Issues.Select(x => x.Message)));
        Assert.Equal(2, second.KeyMismatchRows);
    }

    /// <summary>
    /// 資料庫倒回舊版（或刪掉最新幾筆再放回一份舊鏈頭）：資料庫內部看不出來，只有和檢查點比對才知道；
    /// 排程那一次報，手動驗證只比對不更新，所以手動再驗也還看得到。
    /// </summary>
    [Fact]
    public async Task RollbackBehindTheCheckpoint_ShouldBeReportedOnceByTheScheduledRun()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        AuditChainHeadSnapshot old;
        await using (var context = fixture.NewContext())
        {
            var head = await context.AuditChainHead.AsNoTracking().SingleAsync(x => x.Chain == 0);
            old = new AuditChainHeadSnapshot(head.LastSeq, head.LastHash, head.Mac);
        }

        await fixture.WriteChangesAsync(2);
        Assert.False((await VerifyAsync(fixture, updateCheckpoint: true)).HasIssues);

        await fixture.ExecuteAsync("DELETE FROM AuditLog WHERE Chain = 0 AND Seq >= 3");
        await fixture.ExecuteAsync($"UPDATE AuditChainHead SET LastSeq = {old.LastSeq}, LastHash = '{old.LastHash}', Mac = '{old.Mac}' WHERE Chain = 0");

        var manual = await VerifyAsync(fixture);
        AssertIssue(manual, "RolledBack");
        AssertIssue(await VerifyAsync(fixture, updateCheckpoint: true), "RolledBack");
        Assert.False((await VerifyAsync(fixture, updateCheckpoint: true)).HasIssues);
    }

    private sealed record AuditChainHeadSnapshot(long LastSeq, string LastHash, string Mac);

    [Fact]
    public async Task RebuiltHead_ShouldBeReportedOnceByTheScheduledRun()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        await VerifyAsync(fixture, updateCheckpoint: true);
        await fixture.ExecuteAsync("DELETE FROM AuditChainHead");
        await fixture.WriteChangesAsync(1);

        AssertIssue(await VerifyAsync(fixture, updateCheckpoint: true), "HeadRebuilt");
        Assert.False((await VerifyAsync(fixture, updateCheckpoint: true)).HasIssues);
    }

    [Fact]
    public async Task WholeChainAndHeadDeleted_ShouldBeCaughtByTheCheckpoint()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(2);
        await VerifyAsync(fixture, updateCheckpoint: true);
        await fixture.ExecuteAsync("DELETE FROM AuditLog");
        await fixture.ExecuteAsync("DELETE FROM AuditChainHead");

        AssertIssue(await VerifyAsync(fixture), "HeadMissing");
    }

    [Fact]
    public async Task KeyFileProblems_ShouldBeReported()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(1);

        fixture.KeyStore.FileStatus = AuditKeyFileStatus.Restored;
        AssertIssue(await VerifyAsync(fixture), "KeyFileRestored");
        fixture.KeyStore.FileStatus = AuditKeyFileStatus.Replaced;
        AssertIssue(await VerifyAsync(fixture), "KeyFileReplaced");
    }

    /// <summary>正常清除（含內容檔）與清空全部之後都要通過；內容檔已被正常清掉的紀錄不算遺失。</summary>
    [Fact]
    public async Task NormalPurgeAndClear_ShouldNotRaiseFalseAlarms()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.AppendAtAsync(DateTime.UtcNow.AddDays(-400), content: "Name: A → B");
        await fixture.AppendAtAsync(DateTime.UtcNow.AddDays(-400), content: "Name: B → C");
        await fixture.AppendAtAsync(DateTime.UtcNow.AddDays(-100), "Category.Search", content: "keyword=x");
        await fixture.WriteChangesAsync(2);

        var query = fixture.CreateQueryService();
        Assert.Equal(2, await query.PurgeBeforeAsync(DateTime.UtcNow.AddDays(-365), AuditLogKind.Change));
        Assert.Equal(1, await query.PurgeBeforeAsync(DateTime.UtcNow.AddDays(-90), AuditLogKind.Browsing));
        await query.DeleteContentMonthsBeforeAsync(DateTime.UtcNow.AddDays(-365));
        Assert.False((await VerifyAsync(fixture, updateCheckpoint: true)).HasIssues);

        await query.ClearAllAsync();
        await fixture.WriteChangesAsync(1);
        var report = await VerifyAsync(fixture, updateCheckpoint: true);
        Assert.False(report.HasIssues, string.Join(" | ", report.Issues.Select(x => x.Message)));
    }

    /// <summary>
    /// 月份目錄的邊界保護：為了不在鏈中間刪出缺口而少刪的舊紀錄，它的內容檔所在月份不可以被收掉。
    /// </summary>
    [Fact]
    public async Task MonthFolderCleanup_ShouldKeepFilesOfRowsThatAreStillThere()
    {
        await using var fixture = await AuditIntegrityFixture.CreateAsync();
        await fixture.WriteChangesAsync(1);
        var row = (await fixture.RowsAsync()).Single();
        var old = DateTime.UtcNow.AddDays(-400);
        var oldFolder = $"{old:yyyyMM}/moved.txt";
        var oldPath = fixture.ContentPath(oldFolder)!;
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.Move(fixture.ContentPath(row.ContentFile)!, oldPath);

        // 模擬「時間很舊但仍保留的紀錄」：直接改時間與路徑會讓簽章不對，所以只看檔案有沒有被收。
        await fixture.ExecuteAsync($"UPDATE AuditLog SET OccurredAt = '{old:yyyy-MM-dd HH:mm:ss.fffffff}', ContentFile = '{oldFolder}'");
        await fixture.CreateQueryService().DeleteContentMonthsBeforeAsync(DateTime.UtcNow.AddDays(-30));

        Assert.True(File.Exists(oldPath));
    }
}
