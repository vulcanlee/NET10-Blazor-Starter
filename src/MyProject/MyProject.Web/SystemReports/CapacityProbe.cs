using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.Models.Systems;
using MyProject.Web.DataRetention;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.SystemReports;

/// <summary>一個要量測的資料目錄：固定代碼（設定名稱）＋用途名稱（報告顯示用，不顯示路徑）。</summary>
public sealed record CapacityFolderTarget(string Key, string Label, string? Path);

/// <summary>一個資料目錄的量測結果。</summary>
public sealed record CapacityFolderUsage(string Key, string Label, string? VolumeRoot, long? Bytes, long? Files, string Status);

/// <summary>一顆磁碟的量測結果；<paramref name="Purposes"/> 是放在這顆磁碟上的用途。</summary>
public sealed record CapacityVolumeUsage(string Root, long TotalBytes, long FreeBytes, IReadOnlyList<string> Purposes);

/// <summary>資料庫檔案。</summary>
public sealed record CapacityDatabaseUsage(string? VolumeRoot, long MainBytes, long WalBytes, long FreePageBytes);

/// <summary>一次容量量測。</summary>
public sealed class CapacityProbeResult
{
    public required IReadOnlyList<CapacityVolumeUsage> Volumes { get; init; }

    public required IReadOnlyList<CapacityFolderUsage> Folders { get; init; }

    public CapacityDatabaseUsage? Database { get; init; }

    /// <summary>量測時遇到的限制（固定用語，不含路徑）。</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>轉成容量快照的資料列（一天一份）。</summary>
    public IReadOnlyList<CapacitySnapshotRow> ToSnapshotRows(DateTime snapshotDate)
    {
        var rows = new List<CapacitySnapshotRow>();
        rows.AddRange(Volumes.Select(v => new CapacitySnapshotRow(snapshotDate, CapacitySnapshotKinds.Volume, v.Root,
            v.TotalBytes - v.FreeBytes, v.TotalBytes, v.FreeBytes, null, CapacitySnapshotKinds.StatusOk)));
        rows.AddRange(Folders.Select(f => new CapacitySnapshotRow(snapshotDate, CapacitySnapshotKinds.Folder, f.Key,
            f.Bytes, null, null, f.Files, f.Status)));
        if (Database is { } db)
        {
            rows.Add(new CapacitySnapshotRow(snapshotDate, CapacitySnapshotKinds.Database, CapacityProbe.DatabaseMainKey, db.MainBytes, null, null, null, CapacitySnapshotKinds.StatusOk));
            rows.Add(new CapacitySnapshotRow(snapshotDate, CapacitySnapshotKinds.Database, CapacityProbe.DatabaseWalKey, db.WalBytes, null, null, null, CapacitySnapshotKinds.StatusOk));
        }

        return rows;
    }
}

/// <summary>
/// 量測磁碟、資料目錄與資料庫檔（0.10.39 起，容量快照與系統巡檢報告共用）。
///
/// 目錄大小用 <see cref="DirectorySizeProbe"/>，全部目錄共用一個時間預算（<see cref="FolderBudget"/>），
/// 逾時的目錄標成 Partial（數字是下限）。資料庫檔從 EF 實際開啟的連線取得路徑（同「資料庫用量」頁），不讀設定值。
/// 結果只有固定代碼、用途名稱與磁碟根目錄，不含完整路徑。
/// </summary>
public sealed class CapacityProbe
{
    public const string DatabaseMainKey = "Database.Main";
    public const string DatabaseWalKey = "Database.Wal";

    /// <summary>全部目錄加總的量測時間上限。</summary>
    public static readonly TimeSpan FolderBudget = TimeSpan.FromSeconds(60);

    private readonly IOptions<SystemSettings> systemSettings;
    private readonly INLogFilePathResolver nlogPathResolver;
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ILogger<CapacityProbe> logger;

    public CapacityProbe(
        IOptions<SystemSettings> systemSettings,
        INLogFilePathResolver nlogPathResolver,
        IDbContextFactory<BackendDBContext> contextFactory,
        ILogger<CapacityProbe> logger)
    {
        this.systemSettings = systemSettings;
        this.nlogPathResolver = nlogPathResolver;
        this.contextFactory = contextFactory;
        this.logger = logger;
    }

    /// <summary>要量測的資料目錄（順序就是報告的順序）。</summary>
    public IReadOnlyList<CapacityFolderTarget> Targets()
    {
        var paths = systemSettings.Value.ExternalFileSystem;
        return
        [
            new("BackupPath", "系統備份", paths.BackupPath),
            new("NLog", "日誌檔", nlogPathResolver.GetLogDirectory()),
            new(nameof(paths.ProjectFilePath), "專案附件", paths.ProjectFilePath),
            new(nameof(paths.UploadPath), "上傳暫存", paths.UploadPath),
            new(nameof(paths.DownloadPath), "下載暫存", paths.DownloadPath),
            new(nameof(paths.ExceptionPath), "例外堆疊檔", paths.ExceptionPath),
            new(nameof(paths.TokenUsagePath), "Token 用量原始檔", paths.TokenUsagePath),
            new(nameof(paths.AiCallLogPath), "AI 對話紀錄", paths.AiCallLogPath),
            new(nameof(paths.AiAgentConversationPath), "Agent 對話與巡檢報告", paths.AiAgentConversationPath),
            new(nameof(paths.AuditContentPath), "稽核內容檔", paths.AuditContentPath),
        ];
    }

    public async Task<CapacityProbeResult> MeasureAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var volumes = new Dictionary<string, (VolumeInfo Info, List<string> Purposes)>(StringComparer.OrdinalIgnoreCase);

        void AddVolume(string? path, string purpose)
        {
            if (VolumeResolver.Resolve(path) is not { } volume)
            {
                return;
            }

            if (!volumes.TryGetValue(volume.Root, out var entry))
            {
                entry = (volume, []);
                volumes[volume.Root] = entry;
            }

            if (!entry.Purposes.Contains(purpose))
            {
                entry.Purposes.Add(purpose);
            }
        }

        AddVolume(AppContext.BaseDirectory, "程式");
        var database = await MeasureDatabaseAsync(notes, cancellationToken);
        if (database.Path is not null)
        {
            AddVolume(database.Path, "資料庫");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(FolderBudget);
        var folders = new List<CapacityFolderUsage>();
        foreach (var target in Targets())
        {
            if (string.IsNullOrWhiteSpace(target.Path))
            {
                folders.Add(new CapacityFolderUsage(target.Key, target.Label, null, null, null, CapacitySnapshotKinds.StatusMissing));
                continue;
            }

            AddVolume(target.Path, target.Label);
            var usage = await Task.Run(() => DirectorySizeProbe.Measure(target.Path, budget.Token), CancellationToken.None);
            var status = usage.Status switch
            {
                FolderUsageStatus.Ok => CapacitySnapshotKinds.StatusOk,
                FolderUsageStatus.Partial => CapacitySnapshotKinds.StatusPartial,
                _ => CapacitySnapshotKinds.StatusMissing,
            };
            folders.Add(new CapacityFolderUsage(target.Key, target.Label, VolumeResolver.Resolve(target.Path)?.Root,
                status == CapacitySnapshotKinds.StatusMissing ? null : usage.Bytes,
                status == CapacitySnapshotKinds.StatusMissing ? null : usage.Files,
                status));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (folders.Any(x => x.Status == CapacitySnapshotKinds.StatusPartial))
        {
            notes.Add($"部分資料目錄的檔案太多，{FolderBudget.TotalSeconds:0} 秒內量不完，報告上的大小是「至少」。");
        }

        if (volumes.Count == 0)
        {
            notes.Add("讀不到任何磁碟的容量（權限不足或磁碟離線）。");
        }

        return new CapacityProbeResult
        {
            Volumes = volumes.Values
                .Select(x => new CapacityVolumeUsage(x.Info.Root, x.Info.TotalBytes, x.Info.FreeBytes, x.Purposes))
                .OrderBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Folders = folders,
            Database = database.Usage,
            Notes = notes,
        };
    }

    private async Task<(string? Path, CapacityDatabaseUsage? Usage)> MeasureDatabaseAsync(List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var connection = context.Database.GetDbConnection();
                var mainPath = connection.DataSource;
                if (string.IsNullOrWhiteSpace(mainPath) || mainPath.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null);
                }

                var pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
                var freePages = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken);
                var main = new FileInfo(mainPath);
                var wal = new FileInfo(mainPath + "-wal");
                return (mainPath, new CapacityDatabaseUsage(
                    VolumeResolver.Resolve(mainPath)?.Root,
                    main.Exists ? main.Length : 0,
                    wal.Exists ? wal.Length : 0,
                    pageSize * freePages));
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to measure the database files for the system report.");
            notes.Add("讀不到資料庫檔案的大小。");
            return (null, null);
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
