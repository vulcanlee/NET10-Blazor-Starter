using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MyProject.Web.Backup;

/// <summary>備份驗證的結果：Passed＝可以還原；Failed＝備份有問題（還原不回來）；Unverifiable＝這次沒辦法驗證（例如空間不足）。</summary>
public static class BackupVerificationOutcomes
{
    public const string Passed = "Passed";

    public const string Failed = "Failed";

    public const string Unverifiable = "Unverifiable";
}

/// <summary>驗證的一個項目。<paramref name="Detail"/> 只放數字與固定用語（會寫進寄出系統外的報告）。</summary>
public sealed record BackupVerificationCheck(string Name, bool Passed, string Detail);

/// <summary>一份備份的驗證結果。<paramref name="FileName"/> 是備份檔名（不含路徑）。</summary>
public sealed record BackupVerificationResult(
    string FileName,
    DateTime BackupCreatedAtLocal,
    string Outcome,
    IReadOnlyList<BackupVerificationCheck> Checks,
    long DatabaseBytes,
    int FileCount,
    TimeSpan Elapsed);

/// <summary>
/// 備份可還原驗證（0.10.41 起，系統巡檢週報與月報）：拿最新的一份備份做還原腳本會做的事，證明「真的還原得回來」。
///
/// <list type="bullet">
/// <item>壓縮檔打得開、<c>manifest.json</c> 讀得到、格式版本認得。</item>
/// <item>每個檔案都解壓縮一遍，算出的 CRC 要和壓縮檔記錄的相同（壓縮檔損毀時會在這裡被發現）；各資料夾的檔案數要和 manifest 相同。</item>
/// <item>資料庫解到備份目錄的 <c>.work/</c>：SHA-256 要和 manifest 相同、<c>PRAGMA integrity_check</c> 是 ok、migration 紀錄和 manifest 相同。</item>
/// </list>
/// 不碰正在用的資料庫、不改備份檔；暫存檔一律在 <c>.work/</c>，結束就刪（被強制結束時由下一次備份清掉）。
/// 空間不夠解開資料庫時回 Unverifiable，不算備份壞掉。結果只有數字與固定用語，不含路徑。
/// </summary>
public sealed class BackupVerifier
{
    private const double DiskSpaceMargin = 1.1;

    private readonly BackupStore store;
    private readonly ILogger<BackupVerifier> logger;

    public BackupVerifier(BackupStore store, ILogger<BackupVerifier> logger)
    {
        this.store = store;
        this.logger = logger;
    }

    /// <summary>測試用：可用空間（預設讀磁碟；回 null 表示查不到，此時不檢查）。</summary>
    internal Func<string, long?> FreeSpaceProbe { get; set; } = DefaultFreeSpace;

    public async Task<BackupVerificationResult> VerifyAsync(BackupFileInfo backup, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var checks = new List<BackupVerificationCheck>();
        long databaseBytes = 0;
        var fileCount = 0;

        BackupVerificationResult Done(string outcome)
        {
            var result = new BackupVerificationResult(backup.FileName, backup.CreatedAtLocal, outcome, checks, databaseBytes, fileCount, watch.Elapsed);
            logger.LogInformation(
                "Backup verification finished. FileName={FileName}, Outcome={Outcome}, Failed={Failed}, ElapsedMs={ElapsedMs}",
                backup.FileName, outcome, string.Join(",", checks.Where(x => !x.Passed).Select(x => x.Name)), (long)watch.Elapsed.TotalMilliseconds);
            return result;
        }

        var path = store.TryResolveFullPath(backup.FileName);
        if (path is null || !File.Exists(path))
        {
            checks.Add(new("讀取備份檔", false, "備份檔在驗證前被刪除或搬走（可能剛好被清理），下一次再驗證。"));
            return Done(BackupVerificationOutcomes.Unverifiable);
        }

        var workDirectory = Path.Combine(store.Root, BackupStore.WorkDirectoryName, "verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            ZipArchive zip;
            try
            {
                zip = new ZipArchive(stream, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                checks.Add(new("壓縮檔可以開啟", false, "壓縮檔損毀，無法開啟。"));
                return Done(BackupVerificationOutcomes.Failed);
            }

            using (zip)
            {
                var manifest = store.ReadManifest(backup.FileName);
                if (manifest is null || manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
                {
                    checks.Add(new("壓縮檔可以開啟", false, manifest is null ? "讀不到備份說明檔（manifest.json）。" : $"備份格式版本 {manifest.FormatVersion} 比這個版本的系統新，無法用這個版本還原。"));
                    return Done(BackupVerificationOutcomes.Failed);
                }

                checks.Add(new("壓縮檔可以開啟", true, $"共 {zip.Entries.Count:N0} 個項目，備份時的系統版本 {manifest.SystemVersion}。"));

                if (!await CheckEntriesAsync(zip, manifest, checks, cancellationToken))
                {
                    return Done(BackupVerificationOutcomes.Failed);
                }

                fileCount = manifest.Folders.Sum(x => x.FileCount);
                var databaseEntry = zip.GetEntry(manifest.DatabaseEntry);
                if (databaseEntry is null)
                {
                    checks.Add(new("資料庫檔案", false, "壓縮檔裡沒有資料庫。"));
                    return Done(BackupVerificationOutcomes.Failed);
                }

                databaseBytes = databaseEntry.Length;
                if (FreeSpaceProbe(store.Root) is { } free && free < databaseBytes * DiskSpaceMargin)
                {
                    checks.Add(new("資料庫檔案", false, "備份磁碟的可用空間不夠暫時解開資料庫，這次沒有驗證資料庫。"));
                    return Done(BackupVerificationOutcomes.Unverifiable);
                }

                Directory.CreateDirectory(workDirectory);
                var database = Path.Combine(workDirectory, "BackendDB.db");
                await using (var source = databaseEntry.Open())
                await using (var target = new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                await CheckDatabaseAsync(database, manifest, checks, cancellationToken);
            }

            return Done(checks.All(x => x.Passed) ? BackupVerificationOutcomes.Passed : BackupVerificationOutcomes.Failed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 讀檔途中被刪（清理作業）、或備份磁碟離線：這次無法判斷，不算備份壞掉。
            logger.LogWarning(ex, "Backup verification could not read the backup. FileName={FileName}", backup.FileName);
            checks.Add(new("讀取備份檔", false, "讀取備份檔時發生錯誤（可能剛好被清理或磁碟忙碌），下一次再驗證。"));
            return Done(BackupVerificationOutcomes.Unverifiable);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    /// <summary>每個檔案解壓縮一遍、比對 CRC；各資料夾的檔案數要和 manifest 相同。壓縮資料壞掉時回 false。</summary>
    private static async Task<bool> CheckEntriesAsync(ZipArchive zip, BackupManifest manifest, List<BackupVerificationCheck> checks, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var bad = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = entry.Open();
                var crc = Crc32.Initial;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                }

                if (Crc32.Finish(crc) != entry.Crc32)
                {
                    bad++;
                }
            }
            catch (InvalidDataException)
            {
                bad++;
            }
        }

        checks.Add(new("每個檔案都能解壓縮", bad == 0, bad == 0 ? $"{zip.Entries.Count:N0} 個項目的校驗碼都正確。" : $"{bad:N0} 個項目解壓縮失敗或校驗碼不符。"));
        if (bad > 0)
        {
            return false;
        }

        var mismatched = manifest.Folders
            .Where(folder => zip.Entries.Count(x => x.FullName.StartsWith(folder.Entry + "/", StringComparison.Ordinal) && !x.FullName.EndsWith('/')) != folder.FileCount)
            .Select(folder => folder.Entry)
            .ToList();
        checks.Add(new("檔案數量相符", mismatched.Count == 0, mismatched.Count == 0
            ? $"{manifest.Folders.Count} 個資料夾、共 {manifest.Folders.Sum(x => x.FileCount):N0} 個檔案，與備份說明檔相同。"
            : $"{mismatched.Count} 個資料夾的檔案數與備份說明檔不同。"));
        return mismatched.Count == 0;
    }

    private static async Task CheckDatabaseAsync(string database, BackupManifest manifest, List<BackupVerificationCheck> checks, CancellationToken cancellationToken)
    {
        string hash;
        await using (var file = File.OpenRead(database))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        }

        var hashMatches = string.Equals(hash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase);
        checks.Add(new("資料庫雜湊相符", hashMatches, hashMatches ? "SHA-256 與備份時相同。" : "SHA-256 與備份時不同，資料庫檔在備份後被改過或損毀。"));
        if (!hashMatches)
        {
            return;
        }

        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check";
                command.CommandTimeout = 0;
                var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
                var ok = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
                checks.Add(new("資料庫完整性檢查", ok, ok ? "完整性檢查通過。" : "完整性檢查沒有通過，資料庫有損毀的頁面。"));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT MAX(MigrationId), COUNT(*) FROM __EFMigrationsHistory";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                var last = reader.IsDBNull(0) ? null : reader.GetString(0);
                var count = reader.GetInt32(1);
                var matches = count == manifest.MigrationCount && last == manifest.LastMigration;
                checks.Add(new("資料庫可以開啟並讀取", matches, matches ? $"資料庫結構更新紀錄 {count} 筆，與備份說明檔相同。" : "資料庫結構更新紀錄與備份說明檔不同。"));
            }
        }
        catch (SqliteException)
        {
            checks.Add(new("資料庫可以開啟並讀取", false, "資料庫無法開啟或讀取。"));
        }
    }

    private static long? DefaultFreeSpace(string path)
    {
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(drive) || drive.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 下一次備份開始時會清掉整個 .work。
            logger.LogWarning(ex, "Backup verification work directory could not be deleted; it will be removed by the next backup.");
        }
    }

    /// <summary>ZIP 用的 CRC-32（IEEE 802.3）。</summary>
    private static class Crc32
    {
        public const uint Initial = 0xFFFFFFFFu;

        private static readonly uint[] Table = BuildTable();

        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        public static uint Finish(uint crc) => ~crc;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }
    }
}
