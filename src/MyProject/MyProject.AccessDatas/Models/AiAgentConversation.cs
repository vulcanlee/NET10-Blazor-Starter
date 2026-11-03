namespace MyProject.AccessDatas.Models;

/// <summary>
/// Agent 對話（0.9.143 起）：使用者在 Agent 畫面上的一段對話，存下來之後可以接著聊。
///
/// 本表只放清單與清理要用的中繼資料；對話內容（MAF session、畫面上的對話紀錄、待核准的工具）
/// 寫在檔案系統（見 <see cref="ContentFile"/>，經由 AiAgentConversationFileStore）。
/// <see cref="ConversationId"/> 與 AiCallLog.ConversationId 相同，管理員可以從「AI 對話紀錄」看到每一次模型呼叫。
///
/// ⚠️ 只有本人看得到、刪得掉。時間為本地時間；自動過期依 <see cref="UpdatedAt"/>（最後一次對話），不是建立時間。
/// </summary>
public class AiAgentConversation
{
    public int Id { get; set; }

    /// <summary>對話識別碼，也是內容檔的檔名。</summary>
    public Guid ConversationId { get; set; }

    public int UserId { get; set; }

    /// <summary>建立時的帳號（資料保留頁、日誌好認）；刻意不記姓名／Email。</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>哪一個 Agent（AiAgentDefinition.Name）。</summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>標題：預設取第一則訊息的前 40 字，使用者可以改。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>建立時間（本地時間），也決定內容檔放在哪個月份目錄。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>最後一次對話的時間（本地時間）；清單排序與自動過期都依它。改標題不會更新它。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>停在等使用者核准工具（清單上標示「待核准」，接續時重新跳出核准窗）。</summary>
    public bool HasPendingApproval { get; set; }

    /// <summary>內容檔的相對路徑，例如 202611/ab12….json。寫檔失敗時為 null（這段對話接不下去）。</summary>
    public string? ContentFile { get; set; }
}
