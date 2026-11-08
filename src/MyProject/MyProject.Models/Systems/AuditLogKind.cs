namespace MyProject.Models.Systems;

/// <summary>
/// 清除稽核紀錄時的範圍（0.9.174 起）：瀏覽類（換頁、搜尋、查看）與其他（異動、安全事件）的保留天數不同。
/// 判斷規則見 <c>AuditActions.IsBrowsing</c>。
/// </summary>
public enum AuditLogKind
{
    /// <summary>全部（清空、資料保留頁的自訂清除）。</summary>
    All,

    /// <summary>瀏覽類以外：異動與安全事件，保留 <c>LogRetentionSettings.AuditLogDays</c> 天。</summary>
    Change,

    /// <summary>瀏覽類：換頁、搜尋、查看，保留 <c>LogRetentionSettings.AuditBrowsingLogDays</c> 天。</summary>
    Browsing,
}
