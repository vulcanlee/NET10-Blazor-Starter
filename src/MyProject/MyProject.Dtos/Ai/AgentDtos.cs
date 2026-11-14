namespace MyProject.Dtos.Ai;

/// <summary>
/// Agent 對話（0.10.22 起只有系統管理助理）的共用上限（0.10.7 起，WASM 遷移 M8）：伺服器驗證與畫面輸入框用同一份。
/// </summary>
public static class AgentConversationLimits
{
    /// <summary>單則訊息的字數上限（Server 版只在畫面擋，0.10.7 起伺服器也擋，超過回 400）。</summary>
    public const int MaxMessageLength = 2000;

    /// <summary>對話標題的字數上限（改名時檢查）。</summary>
    public const int MaxTitleLength = 100;

    /// <summary>不可復原的動作，核准時要輸入的文字（前後空白不計）。</summary>
    public const string ConfirmText = "確認";
}

/// <summary>
/// 開頁要的資訊（<c>GET api/v1/admin-assistant/info</c>）：AI 是否可用、對話保存天數、
/// 推論等級選項（只有開放「依對話選推論等級」的 Agent 頁才有；0.10.22 起 Agent 測試台移除後目前沒有）。
/// </summary>
public class AgentPageInfoDto
{
    /// <summary>頁面上方的 Agent 名稱（例如「專案小幫手（範例）」）。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>AI 設定不完整時的中文原因；可用時為 null。</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>最後一次對話超過幾天自動刪除。</summary>
    public int RetentionDays { get; set; }

    /// <summary>推論等級可選的值（小寫，依設定檔順序）；系統管理助理為空。</summary>
    public List<string> ReasoningEffortValues { get; set; } = [];

    /// <summary>推論等級下拉第一項的文字（「系統預設（目前：low）」或「系統預設（不指定，由模型決定）」）。</summary>
    public string SystemDefaultReasoningLabel { get; set; } = string.Empty;
}

/// <summary>「我的對話」清單的一列。時間是伺服器本地時間，以不帶時差送出。</summary>
public class AgentConversationSummaryDto
{
    public Guid ConversationId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public bool HasPendingApproval { get; set; }
}

/// <summary>打開一段對話（<c>GET …/conversations/{id}</c>）：完整的畫面紀錄與待核准，重新整理後也能接著核准。</summary>
public class AgentConversationDto
{
    public Guid ConversationId { get; set; }

    /// <summary>接續時解析出的模型（目前的系統參數）。</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>這段對話存的推論等級；null＝系統預設。</summary>
    public string? ReasoningEffort { get; set; }

    public List<AgentTranscriptEntryDto> Transcript { get; set; } = [];

    /// <summary>等使用者決定的工具呼叫；沒有時為空。</summary>
    public List<AgentApprovalRequestDto> PendingApprovals { get; set; } = [];
}

/// <summary>畫面紀錄的一則（使用者訊息、AI 回覆 Markdown、工具活動、說明、錯誤）。</summary>
public class AgentTranscriptEntryDto
{
    /// <summary><c>User</c>、<c>Assistant</c>、<c>Tool</c>、<c>Notice</c>、<c>Error</c>。</summary>
    public string Kind { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    /// <summary>「由誰回答」（系統管理助理的 AI 回覆與工具活動）；沒有時為 null。</summary>
    public string? Agent { get; set; }
}

/// <summary>一筆待核准的工具呼叫。</summary>
public class AgentApprovalRequestDto
{
    public string RequestId { get; set; } = string.Empty;

    public string ToolName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>參數（名稱＝值，值已轉成文字）。</summary>
    public List<AgentApprovalArgumentDto> Arguments { get; set; } = [];

    /// <summary>工具的核准說明；沒有時為 null。</summary>
    public string? Note { get; set; }

    /// <summary>提出這次核准的專家（系統管理助理）；沒有時為 null。</summary>
    public string? Label { get; set; }

    /// <summary>不可復原的動作：核准時要輸入「確認」。</summary>
    public bool IsIrreversible { get; set; }

    /// <summary>核准窗的「目前狀態」；沒有時為 null。</summary>
    public string? Preview { get; set; }
}

public class AgentApprovalArgumentDto
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// 送出一則訊息（<c>POST …/messages</c>，回 NDJSON 串流）。<see cref="ConversationId"/> 為 null 時開新對話（ID 由伺服器產生，放在 start 那一行）。
/// </summary>
public class AgentMessageRequestDto
{
    public Guid? ConversationId { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>推論等級（只有開放「依對話選推論等級」的 Agent 頁才送，0.10.22 起目前沒有）；null 或空字串＝系統預設。已有對話時改了就從這則訊息起生效。</summary>
    public string? ReasoningEffort { get; set; }
}

/// <summary>
/// 回覆待核准的工具（<c>POST …/conversations/{id}/approvals</c>，回 NDJSON 串流）：<see cref="ApprovedRequestIds"/> 核准，其餘一律拒絕。
/// 送來的編號都必須在目前的待核准裡（已經決定過的回 409），所以同一個核准送兩次只會執行一次。
/// </summary>
public class AgentApprovalResponseRequestDto
{
    public List<string> ApprovedRequestIds { get; set; } = [];

    public List<string> RejectedRequestIds { get; set; } = [];

    /// <summary>核准不可復原的動作時輸入的文字（必須是「確認」）。</summary>
    public string? ConfirmText { get; set; }
}

/// <summary>重新命名對話。</summary>
public class AgentRenameRequestDto
{
    public string Title { get; set; } = string.Empty;
}
