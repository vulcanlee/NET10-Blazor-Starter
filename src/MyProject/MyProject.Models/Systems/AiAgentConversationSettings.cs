using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>
/// Agent 對話的設定（<c>AiAgentConversationSettings</c> 區段，0.9.143 起）。
///
/// 放在 Models：讀它的是 Business 層的 <c>AiAgentConversationService</c>（同 <see cref="AiCallLogSettings"/>）。
/// 以 <c>ValidateOnStart</c> 驗證範圍，寫壞就啟動失敗。
/// </summary>
public class AiAgentConversationSettings
{
    public const string SectionName = "AiAgentConversationSettings";

    /// <summary>
    /// 保留天數：<b>最後一次對話</b>早於這麼多天前的對話（連同內容檔）由排程作業自動刪除（預設每天 03:00）。
    /// 沒有「不清除」：對話內容含業務資料，不宜無限期留著。
    /// </summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 90;
}
