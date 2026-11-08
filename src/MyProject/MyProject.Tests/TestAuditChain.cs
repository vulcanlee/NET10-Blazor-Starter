using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 測試用的稽核雜湊鏈寫入端（0.9.175 起）：固定金鑰、不碰檔案系統。
/// <c>AuditLogService</c> 與 <c>AuditLogQueryService</c> 都必須注入 <see cref="AuditChainStore"/>（正式程式沒有不簽章的寫入路徑），
/// 測試一律從這裡建。
/// </summary>
public static class TestAuditChain
{
    public static AuditChainStore Create(SqliteConnection connection) => Create(new TestDbContextFactory(connection));

    public static AuditChainStore Create(
        IDbContextFactory<BackendDBContext> factory, IAuditIntegrityKeyStore? keyStore = null, AuditChainCheckpointStore? checkpoints = null)
        => new(factory, keyStore ?? new FixedAuditKeyStore(), checkpoints ?? NoCheckpoints(), NullLogger<AuditChainStore>.Instance);

    /// <summary>金鑰目錄未設定的檢查點存放：讀的時候一律「還沒有檢查點」。</summary>
    public static AuditChainCheckpointStore NoCheckpoints()
        => new(Options.Create(new SystemSettings()), NullLogger<AuditChainCheckpointStore>.Instance);
}

/// <summary>固定金鑰（1～32）；<see cref="CheckFile"/> 一律回 <see cref="AuditKeyFileStatus.Ok"/>。可換金鑰模擬金鑰遺失。</summary>
public sealed class FixedAuditKeyStore : IAuditIntegrityKeyStore
{
    public static readonly byte[] DefaultBytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public FixedAuditKeyStore(byte[]? bytes = null) => Use(bytes ?? DefaultBytes);

    public AuditIntegrityKey Key { get; private set; } = null!;

    public AuditKeyFileStatus FileStatus { get; set; } = AuditKeyFileStatus.Ok;

    /// <summary>模擬「金鑰是本行程剛產生的」（金鑰檔遺失後重新產生）。</summary>
    public bool CreatedNewKey { get; set; }

    public void Use(byte[] bytes) => Key = new AuditIntegrityKey(bytes, AuditHashV1.ComputeKeyId(bytes));

    public AuditIntegrityKey GetKey() => Key;

    public AuditKeyFileStatus CheckFile() => FileStatus;
}
