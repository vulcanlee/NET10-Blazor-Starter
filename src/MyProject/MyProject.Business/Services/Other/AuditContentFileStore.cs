using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 稽核紀錄的詳細內容檔（0.9.131 起）：修改前後差異、完整查詢條件這類長內容寫成檔案，
/// 稽核表只存相對路徑 <see cref="AccessDatas.Models.AuditLog.ContentFile"/>。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>寫入、清除此日之前、清空全部與自動過期都必須經過這裡。
/// 作法與 <see cref="AiCallLogFileStore"/> 一致，另外多了根目錄檢查：路徑來自資料庫，
/// 不讓 <c>../</c> 這類值讀刪到根目錄以外的檔案。
///
/// 相對路徑為 <c>{yyyyMM}/{Guid:N}.txt</c>（月份依 UTC，與保存期的 UTC 門檻一致）。
/// 全部方法<b>絕不拋出</b>：內容檔寫不進去不可以讓稽核本身失敗。日誌只記路徑，絕不記內容。
/// </summary>
public class AuditContentFileStore
{
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly ILogger<AuditContentFileStore> logger;

    public AuditContentFileStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<AuditContentFileStore> logger)
    {
        this.systemSettingsOptions = systemSettingsOptions;
        this.logger = logger;
    }

    private string RootPath => systemSettingsOptions.Value.ExternalFileSystem.AuditContentPath;

    /// <summary>寫入內容檔。</summary>
    /// <returns>
    /// 成功時回傳相對路徑與寫進磁碟那份位元組的 SHA-256（0.9.175 起，納入稽核簽章）；
    /// 失敗回 null（稽核資料列照樣寫，只是沒有詳細內容）。
    /// </returns>
    public async Task<AuditContentWriteResult?> WriteAsync(DateTime occurredAtUtc, string content)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrEmpty(content))
        {
            return null;
        }

        var relativePath = $"{occurredAtUtc:yyyyMM}/{Guid.NewGuid():N}.txt";

        try
        {
            var fullPath = TryResolveFullPath(relativePath)!;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // 雜湊與寫入用同一份位元組，不必寫完再讀回來；UTF-8 無 BOM，和之前的 WriteAllTextAsync 相同。
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(fullPath, bytes);
            return new AuditContentWriteResult(relativePath, AuditHashV1.Sha256Hex(bytes));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit content file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔。檔案不存在、路徑不合法或讀取失敗一律回 null，由呼叫端顯示友善訊息。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            return null;
        }

        try
        {
            return File.Exists(fullPath) == false ? null : await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read audit content file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔的原始位元組（0.9.175 起，完整性檢查比對雜湊用）。檔案不存在、路徑不合法或讀取失敗回 null。</summary>
    public async Task<byte[]?> ReadBytesAsync(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            return null;
        }

        try
        {
            return File.Exists(fullPath) == false ? null : await File.ReadAllBytesAsync(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read audit content file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>刪除單一檔案。檔案不存在視為成功。</summary>
    public void Delete(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete audit content file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="thresholdUtc"/> 所在月份的整個 <c>{yyyyMM}</c> 目錄：
    /// 逐筆刪檔之後剩下的空月份目錄，以及「檔已寫、資料列沒建起來」留下的孤兒檔。門檻所在月份本身不動。
    /// </summary>
    public void DeleteMonthsBefore(DateTime thresholdUtc)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || Directory.Exists(RootPath) == false)
        {
            return;
        }

        var boundary = new DateTime(thresholdUtc.Year, thresholdUtc.Month, 1);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                var name = Path.GetFileName(directory);
                if (DateTime.TryParseExact(name, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                    && month < boundary)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove expired audit content folders.");
        }
    }

    /// <summary>
    /// 相對路徑 → 完整路徑；結果不在根目錄底下（<c>../</c>、絕對路徑）或根目錄未設定時回 null。
    /// 根目錄未設定一律拒絕：<c>Path.Combine("", x)</c> 會退化成相對於工作目錄的路徑。
    /// </summary>
    internal string? TryResolveFullPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }
}

/// <summary>內容檔寫入結果：相對路徑與位元組的 SHA-256。</summary>
public sealed record AuditContentWriteResult(string RelativePath, string Sha256);
