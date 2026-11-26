using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 定時巡檢報告的內容檔存放（0.9.161 起；0.10.39 起不再產生舊版巡檢報告，0.10.43 起拿掉它的寫入方法，只剩讀取與清理）。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>寫入、讀取與刪除都必須經過這裡。檔案操作共用 <see cref="RootedFiles"/>（0.10.44 起），
/// 含根目錄檢查：不讓 <c>../</c> 這類值讀刪到根目錄以外的檔案。
///
/// 根目錄是 <c>{AiAgentConversationPath}/AdminPatrol</c>：沿用 Agent 對話的路徑，免去新增外部路徑的十幾處同步；
/// Agent 對話的清理只收根目錄下名稱為 <c>yyyyMM</c> 的空目錄，不會碰到這裡。代價是備份跟著 Agent 對話的開關走。
/// 相對路徑為 <c>{yyyyMM(產出時間)}/{ReportId:N}.md</c>。全部方法<b>絕不拋出</b>；日誌只記路徑，絕不記內容。
///
/// 0.10.39 起系統巡檢報告（<c>SystemReport</c>）也存在這裡：內容是 <c>{ReportId:N}.json</c>（見 <see cref="WriteContentAsync"/>），
/// 和舊版巡檢報告（.md）同一個根目錄與月份目錄，清理、備份與資料保留頁的目錄量測都不用另外處理。
/// </summary>
public class AdminPatrolReportFileStore
{
    /// <summary>Agent 對話目錄底下的子目錄名稱。</summary>
    public const string FolderName = "AdminPatrol";

    private readonly RootedFiles files;
    private readonly ILogger<AdminPatrolReportFileStore> logger;

    public AdminPatrolReportFileStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<AdminPatrolReportFileStore> logger)
    {
        files = new RootedFiles(() => RootOf(systemSettingsOptions.Value.ExternalFileSystem));
        this.logger = logger;
    }

    /// <summary>巡檢報告的根目錄；Agent 對話路徑未設定時為空字串。資料保留頁量目錄大小也用它。</summary>
    public static string RootOf(ExternalFileSystem paths)
        => string.IsNullOrWhiteSpace(paths.AiAgentConversationPath) ? string.Empty : Path.Combine(paths.AiAgentConversationPath, FolderName);

    /// <summary>寫入系統巡檢報告的內容檔（0.10.39 起）。</summary>
    /// <param name="extension">副檔名（不含點），只能是英文小寫字母，例如 <c>json</c>。</param>
    /// <returns>成功時回傳相對路徑；失敗回 null。</returns>
    public async Task<string?> WriteContentAsync(DateTime createdAt, Guid reportId, string extension, string content)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(extension) || extension.Any(c => c is < 'a' or > 'z'))
        {
            return null;
        }

        var relativePath = $"{createdAt:yyyyMM}/{reportId:N}.{extension}";

        try
        {
            return await files.WriteTextAsync(relativePath, content) ? relativePath : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write system report file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔。檔案不存在、路徑不合法或讀取失敗一律回 null。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        try
        {
            return await files.ReadTextAsync(relativePath);
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
        try
        {
            files.Delete(relativePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete admin patrol report file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>刪掉空的 <c>{yyyyMM}</c> 目錄（清理的收尾）。</summary>
    public void DeleteEmptyMonthFolders()
    {
        try
        {
            files.DeleteEmptyMonthFolders();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove empty admin patrol report folders.");
        }
    }
}
