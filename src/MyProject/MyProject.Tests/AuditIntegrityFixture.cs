using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 稽核雜湊鏈測試的共用環境（0.9.175 起）：in-memory SQLite、暫存的內容檔與金鑰目錄、固定金鑰，
/// 以及真的 <see cref="AuditLogService"/>（兩個記錄開關都打開）、<see cref="AuditLogQueryService"/>、
/// <see cref="AuditIntegrityVerifier"/>。竄改一律用原生 SQL，模擬「拿到資料庫、沒拿到金鑰」的人。
/// </summary>
public sealed class AuditIntegrityFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });

    private AuditIntegrityFixture(SqliteConnection connection)
    {
        this.connection = connection;
        Root = Path.Combine(Path.GetTempPath(), "audit-integrity-" + Guid.NewGuid().ToString("N"));
        Settings = new SystemSettings();
        Settings.ExternalFileSystem.AuditContentPath = Path.Combine(Root, "AuditContent");
        Settings.ExternalFileSystem.AuditKeyPath = Path.Combine(Root, "AuditKey");
        Factory = new TestDbContextFactory(connection);
        ContentStore = new AuditContentFileStore(Options.Create(Settings), NullLogger<AuditContentFileStore>.Instance);
        Checkpoints = new AuditChainCheckpointStore(Options.Create(Settings), NullLogger<AuditChainCheckpointStore>.Instance);
        Chain = TestAuditChain.Create(Factory, KeyStore, Checkpoints);
    }

    public string Root { get; }
    public SystemSettings Settings { get; }
    public TestDbContextFactory Factory { get; }
    public FixedAuditKeyStore KeyStore { get; } = new();
    public AuditChainStore Chain { get; }
    public AuditContentFileStore ContentStore { get; }
    public AuditChainCheckpointStore Checkpoints { get; }

    public static async Task<AuditIntegrityFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var fixture = new AuditIntegrityFixture(connection);
        await using var context = fixture.Factory.CreateDbContext();
        await context.Database.EnsureCreatedAsync();
        return fixture;
    }

    public BackendDBContext NewContext() => Factory.CreateDbContext();

    /// <summary>內容檔相對路徑 → 完整路徑。</summary>
    public string ContentPath(string? relativePath) => Path.Combine(Settings.ExternalFileSystem.AuditContentPath, relativePath!);

    public AuditLogService CreateAuditService()
        => new(Chain, NullLogger<AuditLogService>.Instance, ContentStore,
            settings: new StaticOptionsMonitor<AuditLogSettings>(new AuditLogSettings { RecordBrowsing = true, RecordFieldChanges = true }));

    public AuditLogQueryService CreateQueryService()
    {
        return new AuditLogQueryService(Factory, NullLogger<AuditLogQueryService>.Instance, ContentStore, Chain);
    }

    public AuditIntegrityVerifier CreateVerifier()
        => new(Factory, KeyStore, Checkpoints, ContentStore, NullLogger<AuditIntegrityVerifier>.Instance);

    /// <summary>寫 <paramref name="count"/> 筆異動（Category.Update，帶內容檔）。</summary>
    public async Task WriteChangesAsync(int count, string? content = "Name: A → B")
    {
        var audit = CreateAuditService();
        for (var i = 0; i < count; i++)
        {
            await audit.WriteAsync("Category.Update", targetType: "Category", targetId: i.ToString(), detail: $"第 {i} 筆", content: content);
        }
    }

    /// <summary>
    /// 以指定時間簽章寫入一筆（模擬很久以前就寫好的紀錄）。用 SQL 事後改時間會讓簽章不對（那是竄改），所以要從這裡寫。
    /// </summary>
    public async Task AppendAtAsync(DateTime occurredAtUtc, string action = "Category.Update", string? content = null)
    {
        var entry = new AuditLog { OccurredAt = occurredAtUtc, Action = action, Success = true, Detail = $"{occurredAtUtc:O}" };
        if (content is not null && await ContentStore.WriteAsync(occurredAtUtc, content) is { } written)
        {
            entry.ContentFile = written.RelativePath;
            entry.ContentHash = written.Sha256;
        }

        await Chain.AppendAsync(entry);
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        await using var context = NewContext();
        return await context.Database.ExecuteSqlRawAsync(sql);
    }

    public async Task<List<AuditLog>> RowsAsync(int chain = AuditChains.Change)
    {
        await using var context = NewContext();
        return await context.AuditLog.AsNoTracking().Where(x => x.Chain == chain).OrderBy(x => x.Seq).ToListAsync();
    }

    public async Task<AuditChainHead> HeadAsync(int chain = AuditChains.Change)
    {
        await using var context = NewContext();
        return await context.AuditChainHead.AsNoTracking().SingleAsync(x => x.Chain == chain);
    }

    public async ValueTask DisposeAsync()
    {
        loggerFactory.Dispose();
        await connection.DisposeAsync();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
