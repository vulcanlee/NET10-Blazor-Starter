using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>
/// 定時巡檢的設定（<c>AdminPatrolSettings</c> 區段，0.9.161 起）。
///
/// 放在 Models：讀它的是 Business 層的 <c>AdminPatrolReportService</c>（同 <see cref="AiAgentConversationSettings"/>）。
/// 以 <c>ValidateOnStart</c> 驗證範圍，寫壞就啟動失敗。巡檢的執行時間是排程作業的 cron（<c>ScheduledJobSettings:Jobs:AdminPatrol:Cron</c>），不在這裡。
/// </summary>
public class AdminPatrolSettings
{
    public const string SectionName = "AdminPatrolSettings";

    /// <summary>
    /// 巡檢報告的保留天數：產出早於這麼多天前的報告（連同內容檔）由排程作業自動刪除（預設每天 03:00）。
    /// 沒有「不清除」（同 Agent 對話）。
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 90;
}
