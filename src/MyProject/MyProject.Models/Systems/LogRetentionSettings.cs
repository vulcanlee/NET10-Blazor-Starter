using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>
/// 系統例外紀錄與稽核紀錄的保存期限（<c>LogRetentionSettings</c> 區段，0.9.78 起，LOG-13）。
///
/// 背景作業 <c>LogRetentionWorker</c> 啟動時與每日各執行一次；設成 <c>0</c> 代表不自動清理。
/// 頁面上的「清除 N 天前」按鈕也讀這裡（停用自動清理時退回預設天數）。
/// AI 對話紀錄有自己的 <see cref="AiCallLogSettings"/>，日誌檔由 nlog.config 的 maxArchiveDays 管。
/// </summary>
public class LogRetentionSettings
{
    public const string SectionName = "LogRetentionSettings";

    public const int DefaultExceptionLogDays = 90;
    public const int DefaultAuditLogDays = 365;

    /// <summary>例外紀錄「最後發生」超過幾天就刪除（連同堆疊檔）。0＝不自動清理。</summary>
    [Range(0, 36500)]
    public int ExceptionLogDays { get; set; } = DefaultExceptionLogDays;

    /// <summary>
    /// 稽核紀錄超過幾天就刪除。0＝不自動清理。
    /// 稽核是責任證據，若有法規或內控的保存要求，請依要求調大或設 0。
    /// </summary>
    [Range(0, 36500)]
    public int AuditLogDays { get; set; } = DefaultAuditLogDays;

    /// <summary>頁面手動清除用的天數：停用自動清理時仍需要一個合理的門檻。</summary>
    public int ManualExceptionLogDays => ExceptionLogDays > 0 ? ExceptionLogDays : DefaultExceptionLogDays;

    /// <summary>頁面手動清除用的天數：停用自動清理時仍需要一個合理的門檻。</summary>
    public int ManualAuditLogDays => AuditLogDays > 0 ? AuditLogDays : DefaultAuditLogDays;
}
