namespace MyProject.AccessDatas.Models;

/// <summary>
/// 定時巡檢報告（0.9.161 起）：排程作業「系統管理助理巡檢」每次產出的一份報告。
///
/// 本表只放清單、通知與清理要用的中繼資料，而且只存固定用語（狀態、固定的段落名稱）；
/// 報告內文是模型回應，只寫在內容檔（見 <see cref="ContentFile"/>，經由 AdminPatrolReportFileStore），不得寫進本表（速查表 §6.7）。
/// <see cref="ReportId"/> 與 AiCallLog.ConversationId 相同，可以從「AI 對話紀錄」看到這次巡檢的每一次模型呼叫。
///
/// 全體管理員都看得到（不分使用者）。時間為本地時間；自動過期依 <see cref="CreatedAt"/>。
/// </summary>
public class AdminPatrolReport
{
    public int Id { get; set; }

    /// <summary>報告識別碼，也是內容檔的檔名與通知連結的參數。</summary>
    public Guid ReportId { get; set; }

    /// <summary>產出時間（本地時間），也決定內容檔放在哪個月份目錄。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>整體狀態：正常／注意／異常／無法判讀（AdminPatrolStatuses）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>需要注意的段落：固定的段落名稱（AdminPatrolSections），以「、」串接；全部正常時為空字串。</summary>
    public string AttentionSections { get; set; } = string.Empty;

    /// <summary>觸發方式（JobRunTriggers：Schedule／CatchUp／Manual）。</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>手動執行的管理員帳號；排程與補跑為 null。</summary>
    public string? TriggeredByAccount { get; set; }

    /// <summary>對應的排程執行紀錄（JobRun.Id）。</summary>
    public int JobRunId { get; set; }

    /// <summary>實際回應的模型名稱。</summary>
    public string? ModelName { get; set; }

    /// <summary>AI 回應達長度上限被截斷（報告可能不完整）。</summary>
    public bool IsTruncated { get; set; }

    /// <summary>內容檔的相對路徑（相對於 AdminPatrol 目錄），例如 202611/ab12….md。</summary>
    public string? ContentFile { get; set; }
}
