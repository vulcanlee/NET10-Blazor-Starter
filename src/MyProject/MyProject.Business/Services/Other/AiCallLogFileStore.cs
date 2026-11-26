using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// AI 對話紀錄的內容檔存放（0.9.72 起）。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>刪單筆、清除此日之前、清空全部與自動過期都必須經過這裡，
/// 不可有人自己去 <c>File.Delete</c>。檔案操作共用 <see cref="RootedFiles"/>（0.10.44 起，含根目錄檢查）。
///
/// 相對路徑為 <c>{yyyyMM}/{CallId:N}.json</c>。全部方法<b>絕不拋出</b>：
/// 記錄對話失敗不可以影響 AI 呼叫本身。日誌只記路徑，絕不記內容。
/// </summary>
public class AiCallLogFileStore
{
    private readonly RootedFiles files;
    private readonly ILogger<AiCallLogFileStore> logger;

    public AiCallLogFileStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<AiCallLogFileStore> logger)
    {
        files = new RootedFiles(() => systemSettingsOptions.Value.ExternalFileSystem.AiCallLogPath);
        this.logger = logger;
    }

    /// <summary>寫入內容檔。</summary>
    /// <returns>成功時回傳相對路徑；失敗回 null（<b>不得</b>讓資料列因此建不起來）。</returns>
    public async Task<string?> WriteAsync(DateTime occurredAt, Guid callId, string content)
    {
        if (files.HasRoot == false || string.IsNullOrEmpty(content))
        {
            return null;
        }

        var relativePath = $"{occurredAt:yyyyMM}/{callId:N}.json";

        try
        {
            return await files.WriteTextAsync(relativePath, content) ? relativePath : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write AI call log file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔。檔案不存在、路徑不合法或讀取失敗一律回 null，由呼叫端顯示友善訊息。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        try
        {
            return await files.ReadTextAsync(relativePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read AI call log file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>刪除單一檔案。檔案不存在視為成功；路徑不在根目錄底下時不刪。</summary>
    public void Delete(string? relativePath)
    {
        try
        {
            files.Delete(relativePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete AI call log file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>清空整個目錄（供「清空全部」使用；根目錄本身留著）。</summary>
    public void DeleteAll()
    {
        try
        {
            files.DeleteAll();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear the AI call log directory.");
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="threshold"/> 所在月份的整個 <c>{yyyyMM}</c> 目錄（自動過期的收尾）。
    /// 門檻所在月份本身不動 —— 那個月可能還有未過期的紀錄。
    /// </summary>
    public void DeleteMonthsBefore(DateTime threshold)
    {
        try
        {
            files.DeleteMonthsBefore(threshold);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove expired AI call log folders.");
        }
    }
}
