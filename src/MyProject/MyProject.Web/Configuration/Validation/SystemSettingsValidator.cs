using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Web.Configuration.Validation;

/// <summary>
/// <c>SystemSettings</c>：外部目錄的路徑與系統名稱。
///
/// 路徑留空時沒有任何錯誤，而是靜默地用錯地方：<c>DatabasePath</c> 空白 → 資料庫建在工作目錄；
/// <c>ExceptionPath</c>／<c>TokenUsagePath</c>／<c>AiCallLogPath</c> 空白 → 檔案存放器直接跳過寫入。
/// 相對路徑則會隨工作目錄漂移（IIS 的工作目錄不是網站目錄），所以一律要求完整路徑。
/// </summary>
public sealed class SystemSettingsValidator : IValidateOptions<SystemSettings>
{
    public ValidateOptionsResult Validate(string? name, SystemSettings options)
    {
        var errors = new OptionsErrors();
        var paths = options.ExternalFileSystem;

        RequirePath(errors, nameof(paths.DatabasePath), paths.DatabasePath);
        RequirePath(errors, nameof(paths.DownloadPath), paths.DownloadPath);
        RequirePath(errors, nameof(paths.UploadPath), paths.UploadPath);
        RequirePath(errors, nameof(paths.ProjectFilePath), paths.ProjectFilePath);
        RequirePath(errors, nameof(paths.ExceptionPath), paths.ExceptionPath);
        RequirePath(errors, nameof(paths.TokenUsagePath), paths.TokenUsagePath);
        RequirePath(errors, nameof(paths.AiCallLogPath), paths.AiCallLogPath);
        RequirePath(errors, nameof(paths.DataProtectionKeyPath), paths.DataProtectionKeyPath);

        errors.RequireText(
            "SystemSettings:SystemInformation:SystemName",
            options.SystemInformation.SystemName,
            "它是瀏覽器分頁標題與側邊欄的系統名稱。");

        return errors.ToResult();
    }

    private static void RequirePath(OptionsErrors errors, string field, string value)
    {
        var key = $"SystemSettings:ExternalFileSystem:{field}";
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(key, "不可留空。請填寫完整路徑，例如 C:\\temp\\MyProject\\DB。");
        }
        else if (!Path.IsPathFullyQualified(value))
        {
            errors.Add(key, $"必須是完整路徑（含磁碟機代號或 UNC），目前是「{value}」。相對路徑會隨工作目錄改變，IIS 下尤其容易寫錯地方。");
        }
    }
}
