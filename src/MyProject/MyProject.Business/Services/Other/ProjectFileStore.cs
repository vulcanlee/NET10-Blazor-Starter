using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 專案附件實體檔的解析與刪除（0.9.97 起；比照 <see cref="TokenUsageRawStore"/>、<see cref="ExceptionStackFileStore"/>）。
///
/// ⚠️ <b>刪除附件實體檔的唯一入口。</b>手動永久刪除、編輯時移除附件、排程的已刪除資料清理都經過這裡。
/// 0.9.96 之前刪檔沒有根目錄檢查（只有下載有）：資料庫裡的 RelativePath 被改成絕對路徑或 <c>..\..</c> 時，
/// 會照著刪掉附件目錄以外的檔案 —— 無人值守的排程作業尤其不能這樣。
///
/// 相對路徑由 <c>ProjectService</c> 寫成「年/月/Guid.副檔名」；寫檔仍在 ProjectService（路徑是它自己產生的）。
/// 路徑解析共用 <see cref="RootedFiles"/>（0.10.44 起）。
/// </summary>
public class ProjectFileStore
{
    private readonly RootedFiles files;
    private readonly ILogger<ProjectFileStore> logger;

    public ProjectFileStore(IOptions<SystemSettings> systemSettingsOptions, ILogger<ProjectFileStore> logger)
    {
        files = new RootedFiles(() => systemSettingsOptions.Value.ExternalFileSystem.ProjectFilePath);
        this.logger = logger;
    }

    /// <summary>
    /// 把相對路徑解析成完整路徑；解析後**不在**附件根目錄之下（或根目錄未設定）時回 null。
    /// 根目錄未設定時一律拒絕：<c>Path.Combine("", x)</c> 會退化成相對於工作目錄的路徑。
    /// </summary>
    public string? TryResolveFullPath(string? relativePath) => files.TryResolve(relativePath);

    /// <summary>
    /// 刪除一個附件實體檔。檔案不存在視為成功；路徑跑出根目錄時拒絕並回 false；刪不掉也只記警告、回 false，不丟例外 ——
    /// 呼叫端都是在資料列刪除**之後**才刪檔，留下的只是孤兒檔，不影響資料正確性。
    /// </summary>
    public bool Delete(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                logger.LogWarning("Project attachment deletion refused because the path escapes the configured root. RelativePath={RelativePath}", relativePath);
            }

            return false;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete project attachment file. RelativePath={RelativePath}", relativePath);
            return false;
        }
    }
}
