namespace MyProject.Models.Systems;

/// <summary>
/// 稽核紀錄的記錄範圍（<c>AuditLogSettings</c> 區段，0.9.174 起）。兩個開關預設都關，可在「系統參數」頁即時切換。
///
/// 只影響「看」與「改了什麼」：登入、權限拒絕、新增、修改、刪除是誰在何時做的，這類安全與異動事件永遠照記。
/// 判斷只在 <c>AuditLogService.WriteAsync</c> 一處（速查表 §6.30）；放在 Models 是因為讀它的是 Business 層。
/// </summary>
public class AuditLogSettings
{
    public const string SectionName = "AuditLogSettings";

    /// <summary>
    /// 是否記錄「修改前後差異」（改了哪些欄位、舊值與新值，寫成詳細內容檔）。
    /// 關閉時修改照樣留下一筆「誰、何時、哪一筆」，只是沒有差異。只作用在 <c>*.Update</c> 與 <c>User.ProfileUpdate</c>。
    /// </summary>
    public bool RecordFieldChanges { get; set; }

    /// <summary>
    /// 是否記錄瀏覽類事件：換頁（Page.View）、按搜尋（*.Search）、開單筆來看（*.View），包含「誰看過稽核紀錄」。
    /// 這類事件筆數最多，是稽核表膨脹的主因；保留天數另見 <see cref="LogRetentionSettings.AuditBrowsingLogDays"/>。
    /// </summary>
    public bool RecordBrowsing { get; set; }
}
