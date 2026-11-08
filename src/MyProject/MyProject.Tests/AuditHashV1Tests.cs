using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 稽核簽章格式第 1 版與金鑰檔（0.9.175 起）。
///
/// ⚠️ 固定答案的測試失敗代表簽章格式變了：已經上線的紀錄會全部驗不過。要改格式必須另寫 V2，而不是改這裡的答案。
/// </summary>
public sealed class AuditHashV1Tests
{
    private static AuditLog Sample() => new()
    {
        Chain = AuditChains.Change,
        Seq = 7,
        PrevHash = AuditHashV1.Genesis,
        OccurredAt = new DateTime(2026, 11, 8, 9, 51, 51, DateTimeKind.Utc).AddTicks(2690908),
        ActorUserId = 3,
        ActorAccount = "admin",
        Action = "Category.Update",
        TargetType = "Category",
        TargetId = "12",
        Detail = "修改分類",
        Success = true,
        ClientIp = "10.0.0.1",
        ContentFile = "202611/abc.txt",
        ContentHash = new string('a', 64),
        KeyId = "0123456789abcdef",
    };

    [Fact]
    public void RowHash_ShouldMatchTheFrozenV1Answer()
    {
        Assert.Equal(
            "f5376865c87d24d3d386940f51ac11393a10f12fadd16b6c0c55d0e9380aac59",
            AuditHashV1.ComputeRowHash(FixedAuditKeyStore.DefaultBytes, Sample()));
    }

    [Fact]
    public void HeadMac_AndKeyId_ShouldMatchTheFrozenV1Answers()
    {
        var head = new AuditChainHead
        {
            Chain = AuditChains.Browsing,
            StartSeq = 1,
            StartPrevHash = AuditHashV1.Genesis,
            LastSeq = 9,
            LastHash = new string('b', 64),
            KeyId = "0123456789abcdef",
            KeySinceSeq = 1,
            LegacyMaxId = 41,
            CreatedAt = new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc),
        };

        Assert.Equal("4710d546c4c855bcafafbb30a64ccf3b286f43da86673e5d3698d5933e615b77", AuditHashV1.ComputeHeadMac(FixedAuditKeyStore.DefaultBytes, head));
        Assert.Equal("0fed3407a1f57b8d", AuditHashV1.ComputeKeyId(FixedAuditKeyStore.DefaultBytes));
    }

    /// <summary>每個欄位都在簽章裡：改任何一個都要變。</summary>
    [Fact]
    public void RowHash_ShouldChange_WhenAnyFieldChanges()
    {
        var key = FixedAuditKeyStore.DefaultBytes;
        var original = AuditHashV1.ComputeRowHash(key, Sample());
        var mutations = new Action<AuditLog>[]
        {
            x => x.Chain = AuditChains.Browsing, x => x.Seq = 8, x => x.PrevHash = new string('1', 64),
            x => x.OccurredAt = x.OccurredAt.AddTicks(1), x => x.ActorUserId = 4, x => x.ActorAccount = "admim",
            x => x.Action = "Category.Delete", x => x.TargetType = "Project", x => x.TargetId = "13", x => x.Detail = "修改分類 ",
            x => x.Success = false, x => x.ClientIp = "10.0.0.2", x => x.ContentFile = "202611/abd.txt",
            x => x.ContentHash = new string('b', 64), x => x.KeyId = "0123456789abcdee",
        };

        foreach (var mutate in mutations)
        {
            var row = Sample();
            mutate(row);
            Assert.NotEqual(original, AuditHashV1.ComputeRowHash(key, row));
        }
    }

    /// <summary>null 和空字串、欄位邊界不可混淆（每個欄位都帶長度）。</summary>
    [Fact]
    public void RowHash_ShouldDistinguishNullFromEmpty_AndFieldBoundaries()
    {
        var key = FixedAuditKeyStore.DefaultBytes;
        var withNull = Sample();
        withNull.Detail = null;
        var withEmpty = Sample();
        withEmpty.Detail = string.Empty;
        Assert.NotEqual(AuditHashV1.ComputeRowHash(key, withNull), AuditHashV1.ComputeRowHash(key, withEmpty));

        var a = Sample();
        a.TargetType = "Cat";
        a.TargetId = "egory12";
        var b = Sample();
        b.TargetType = "Category";
        b.TargetId = "12";
        Assert.NotEqual(AuditHashV1.ComputeRowHash(key, a), AuditHashV1.ComputeRowHash(key, b));
    }

    /// <summary>
    /// 時間與孤立 surrogate 寫進 SQLite 再讀回來，簽章仍然相同。孤立 surrogate 讀回會變成 U+FFFD；
    /// 任何登入的人都能在搜尋字串塞這種字元，這裡誤報等於讓一般使用者能製造假警報。
    /// </summary>
    [Fact]
    public async Task RowHash_ShouldSurviveASqliteRoundTrip_IncludingTicksAndLoneSurrogates()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using (var context = factory.CreateDbContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        var row = Sample();
        row.Detail = "a\uD800b";
        row.OccurredAt = DateTime.UtcNow;
        var expected = AuditHashV1.ComputeRowHash(FixedAuditKeyStore.DefaultBytes, row);
        await using (var context = factory.CreateDbContext())
        {
            context.AuditLog.Add(row);
            await context.SaveChangesAsync();
        }

        await using (var context = factory.CreateDbContext())
        {
            var readBack = await context.AuditLog.AsNoTracking().SingleAsync();
            Assert.NotEqual("a\uD800b", readBack.Detail);
            Assert.Equal(row.OccurredAt.Ticks, readBack.OccurredAt.Ticks);
            Assert.Equal(expected, AuditHashV1.ComputeRowHash(FixedAuditKeyStore.DefaultBytes, readBack));
        }
    }

    // ---------- 金鑰檔 ----------

    private static AuditIntegrityKeyStore NewKeyStore(string root)
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AuditKeyPath = root;
        return new AuditIntegrityKeyStore(Options.Create(settings), NullLogger<AuditIntegrityKeyStore>.Instance);
    }

    [Fact]
    public void KeyStore_ShouldCreateTheKeyOnFirstUse_AndReuseItAfterwards()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = NewKeyStore(root);
            Assert.False(Directory.Exists(root), "建構時不可以產生金鑰（啟動與 dotnet ef 階段都不碰）。");

            var key = first.GetKey();
            Assert.Equal(32, key.Bytes.Length);
            Assert.True(File.Exists(Path.Combine(root, AuditIntegrityKeyStore.KeyFileName)));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));

            var again = NewKeyStore(root).GetKey();
            Assert.Equal(key.Bytes, again.Bytes);
            Assert.Equal(key.KeyId, again.KeyId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>兩個行程（IIS 重疊回收）同時第一次產生金鑰：結果必須是同一把。</summary>
    [Fact]
    public async Task KeyStore_ConcurrentFirstUse_ShouldEndWithOneKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => NewKeyStore(root).GetKey().KeyId)));
            Assert.Single(keys.Distinct());
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeyStore_CheckFile_ShouldRestoreAMissingFile_AndReportAReplacedOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "audit-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = NewKeyStore(root);
            var key = store.GetKey();
            var path = Path.Combine(root, AuditIntegrityKeyStore.KeyFileName);
            Assert.Equal(AuditKeyFileStatus.Ok, store.CheckFile());

            File.Delete(path);
            Assert.Equal(AuditKeyFileStatus.Restored, store.CheckFile());
            Assert.Equal(key.Bytes, NewKeyStore(root).GetKey().Bytes);

            File.WriteAllText(path, Convert.ToBase64String(new byte[32]));
            Assert.Equal(AuditKeyFileStatus.Replaced, store.CheckFile());
            Assert.Equal(key.KeyId, store.GetKey().KeyId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
