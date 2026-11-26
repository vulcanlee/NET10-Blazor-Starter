using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// Token 用量紀錄的原始 usage JSON 存放。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>三條刪除路徑（刪單筆、清除此日之前、清空全部）
/// 都必須經過這裡，不可有人自己去 <c>File.Delete</c>。理由同 <see cref="ExceptionStackFileStore"/>：專案附件曾因
/// 「資料表紀錄由 EF Cascade 處理、實體檔案卻要 Service 層自己記得刪」而留下孤兒檔。
/// 檔案操作共用 <see cref="RootedFiles"/>（0.10.44 起，含根目錄檢查）。
///
/// 相對路徑為 <c>{yyyyMM}/{GUID}.json</c>。每次呼叫都是獨立一筆、不像例外紀錄會合併，
/// 所以用 GUID 而非雜湊。
/// </summary>
public class TokenUsageRawStore
{
    private readonly RootedFiles files;
    private readonly ILogger<TokenUsageRawStore> logger;

    public TokenUsageRawStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<TokenUsageRawStore> logger)
    {
        files = new RootedFiles(() => systemSettingsOptions.Value.ExternalFileSystem.TokenUsagePath);
        this.logger = logger;
    }

    /// <summary>
    /// 寫入原始 usage JSON。
    /// </summary>
    /// <returns>成功時回傳相對路徑；失敗回 null（<b>不得</b>讓資料列因此建不起來）。</returns>
    public async Task<string?> WriteAsync(DateTime occurredAt, string? content)
    {
        if (files.HasRoot == false || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var relativePath = $"{occurredAt:yyyyMM}/{Guid.NewGuid():N}.json";

        try
        {
            return await files.WriteTextAsync(relativePath, content) ? relativePath : null;
        }
        catch (Exception ex)
        {
            // 寫檔失敗只是少了原始明細，資料列仍必須建立。
            logger.LogWarning(ex, "Failed to write usage payload file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取原始 JSON。檔案不存在、路徑不合法或讀取失敗一律回 null，由呼叫端顯示友善訊息。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        try
        {
            return await files.ReadTextAsync(relativePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read usage payload file. Path={RelativePath}", relativePath);
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
            // 刪不掉就留著，不影響資料列刪除。
            logger.LogWarning(ex, "Failed to delete usage payload file. Path={RelativePath}", relativePath);
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
            // 同上。
            logger.LogWarning(ex, "Failed to clear the usage payload directory.");
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="threshold"/> 所在月份的整個 <c>{yyyyMM}</c> 目錄（0.9.96 起，自動過期的收尾）。
    ///
    /// 檔案依紀錄的 <c>OccurredAt</c> 分月存放，所以門檻月份之前的目錄只剩：逐筆刪檔後的空目錄、
    /// 「檔已寫、資料列沒建起來」或刪除中途被中斷留下的孤兒檔。門檻所在月份本身不動 —— 那個月可能還有未過期的紀錄。
    /// 只能在該門檻之前的資料列都已刪除之後呼叫。
    /// </summary>
    public void DeleteMonthsBefore(DateTime threshold)
    {
        try
        {
            files.DeleteMonthsBefore(threshold);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove expired usage payload folders.");
        }
    }
}
