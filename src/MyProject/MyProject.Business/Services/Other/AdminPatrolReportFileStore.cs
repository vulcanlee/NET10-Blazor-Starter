using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 定時巡檢報告的內容檔存放（0.9.161 起）。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>寫入、讀取與刪除都必須經過這裡。作法同 <see cref="AiAgentConversationFileStore"/>（檢查根目錄，
/// 不讓 <c>../</c> 這類值讀刪到根目錄以外的檔案）。
///
/// 根目錄是 <c>{AiAgentConversationPath}/AdminPatrol</c>：沿用 Agent 對話的路徑，免去新增外部路徑的十幾處同步；
/// Agent 對話的清理只收根目錄下名稱為 <c>yyyyMM</c> 的空目錄，不會碰到這裡。代價是備份跟著 Agent 對話的開關走。
/// 相對路徑為 <c>{yyyyMM(產出時間)}/{ReportId:N}.md</c>。全部方法<b>絕不拋出</b>；日誌只記路徑，絕不記內容。
/// </summary>
public class AdminPatrolReportFileStore
{
    /// <summary>Agent 對話目錄底下的子目錄名稱。</summary>
    public const string FolderName = "AdminPatrol";

    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly ILogger<AdminPatrolReportFileStore> logger;

    public AdminPatrolReportFileStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<AdminPatrolReportFileStore> logger)
    {
        this.systemSettingsOptions = systemSettingsOptions;
        this.logger = logger;
    }

    /// <summary>巡檢報告的根目錄；Agent 對話路徑未設定時為空字串。資料保留頁量目錄大小也用它。</summary>
    public static string RootOf(ExternalFileSystem paths)
        => string.IsNullOrWhiteSpace(paths.AiAgentConversationPath) ? string.Empty : Path.Combine(paths.AiAgentConversationPath, FolderName);

    private string RootPath => RootOf(systemSettingsOptions.Value.ExternalFileSystem);

    /// <summary>寫入內容檔。</summary>
    /// <returns>成功時回傳相對路徑；失敗回 null。</returns>
    public async Task<string?> WriteAsync(DateTime createdAt, Guid reportId, string markdown)
    {
        var relativePath = $"{createdAt:yyyyMM}/{reportId:N}.md";
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null || string.IsNullOrEmpty(markdown))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, markdown);
            return relativePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write admin patrol report file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔。檔案不存在、路徑不合法或讀取失敗一律回 null。</summary>
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
            logger.LogWarning(ex, "Failed to read admin patrol report file. Path={RelativePath}", relativePath);
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
            logger.LogWarning(ex, "Failed to delete admin patrol report file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>刪掉空的 <c>{yyyyMM}</c> 目錄（清理的收尾）。</summary>
    public void DeleteEmptyMonthFolders()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || Directory.Exists(RootPath) == false)
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                if (DateTime.TryParseExact(Path.GetFileName(directory), "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    && Directory.EnumerateFileSystemEntries(directory).Any() == false)
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove empty admin patrol report folders.");
        }
    }

    /// <summary>相對路徑 → 完整路徑；不在根目錄底下（<c>../</c>、絕對路徑）或根目錄未設定時回 null。</summary>
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
