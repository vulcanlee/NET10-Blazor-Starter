namespace MyProject.AccessDatas.Models;

/// <summary>
/// 系統巡檢報告（0.10.39 起，取代 <see cref="AdminPatrolReport"/>）：日報／週報／月報與手動產生的報告。
///
/// 燈號與數字由程式規則判定（不是模型），本表只放清單、篩選、通知與清理要用的中繼資料；
/// 完整內容（發現、容量表、AI 說明）寫在內容檔（見 <see cref="ContentFile"/>，經由 AdminPatrolReportFileStore），
/// AI 說明是模型回應，不得寫進本表（速查表 §6.7）。
///
/// 全體管理員都看得到（不分使用者）。時間為本地時間；自動過期依 <see cref="CreatedAt"/> 與各頻率的保留天數。
/// </summary>
public class SystemReport
{
    public int Id { get; set; }

    /// <summary>報告識別碼，也是內容檔的檔名、通知連結的參數與 AI 呼叫的 ConversationId。</summary>
    public Guid ReportId { get; set; }

    /// <summary>頻率：Daily／Weekly／Monthly／Custom（SystemReportFrequencies）。</summary>
    public string Frequency { get; set; } = string.Empty;

    /// <summary>涵蓋區間的開始（本地時間，含）。</summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>涵蓋區間的結束（本地時間，不含）。日報 2026/11/19 為 11/19 00:00～11/20 00:00。</summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>產出時間（本地時間），也決定內容檔放在哪個月份目錄。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>整體燈號：Red／Orange／Yellow／Green（SystemReportLevels）。</summary>
    public string Level { get; set; } = string.Empty;

    public int RedCount { get; set; }

    public int OrangeCount { get; set; }

    public int YellowCount { get; set; }

    /// <summary>觸發方式：Schedule／CatchUp／Manual／Assistant。</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>手動產生的管理員帳號；排程與補跑為 null。</summary>
    public string? TriggeredByAccount { get; set; }

    /// <summary>對應的排程執行紀錄（JobRun.Id）；助理對話產生的為 null。</summary>
    public int? JobRunId { get; set; }

    /// <summary>AI 說明的狀態：Written／Fallback／Skipped（SystemReportNarrativeStatuses）。</summary>
    public string NarrativeStatus { get; set; } = string.Empty;

    /// <summary>寫 AI 說明的模型名稱；沒有呼叫 AI 時為 null。</summary>
    public string? ModelName { get; set; }

    /// <summary>通知狀態：NotRequired／Sent／Failed（SystemReportDeliveryStatuses）。</summary>
    public string DeliveryStatus { get; set; } = string.Empty;

    /// <summary>內容檔的相對路徑（相對於 AdminPatrol 目錄），例如 202611/ab12….json。</summary>
    public string? ContentFile { get; set; }
}
