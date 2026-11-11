using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Ai;

/// <summary>Agent 工作流程頁的資訊（0.10.7 起，WASM 遷移 M8）：可選的工作流程、AI 是否可用、紀錄保留天數。</summary>
public class AgentWorkflowPageInfoDto
{
    /// <summary>AI 設定不完整時的中文原因；可用時為 null。</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>最後一次執行超過幾天的紀錄會自動刪除。</summary>
    public int RetentionDays { get; set; }

    public List<AgentWorkflowDefinitionDto> Workflows { get; set; } = [];
}

/// <summary>畫面上可以選的一條工作流程。</summary>
public class AgentWorkflowDefinitionDto
{
    /// <summary>程式內的識別名稱，也是「我的執行紀錄」的分類。</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string InputPlaceholder { get; set; } = string.Empty;

    /// <summary>輸入框的列數（要貼整份文件的流程比較高）。</summary>
    public int InputRows { get; set; }
}

/// <summary>「我的執行紀錄」的一列。<see cref="UpdatedAt"/> 是伺服器的本地時間（不帶時差）。</summary>
public class AgentWorkflowRecordDto
{
    public Guid ConversationId { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>一筆之前的執行紀錄（唯讀回顧）。</summary>
public class AgentWorkflowRecordDetailDto
{
    public Guid ConversationId { get; set; }

    public List<AgentWorkflowEntryDto> Transcript { get; set; } = [];
}

/// <summary>重新命名一筆紀錄（只給本人看，不寫稽核）。</summary>
public class AgentWorkflowRenameRequestDto
{
    /// <summary>標題長度上限（同伺服器 <c>AiAgentConversationService.MaxTitleLength</c>；超過由服務回 400）。</summary>
    public const int MaxTitleLength = 100;

    public string Title { get; set; } = string.Empty;
}

/// <summary>開始一次執行。</summary>
public class AgentWorkflowStartRequestDto
{
    /// <summary>輸入的字數上限（多觀點審查要貼整份文件）。</summary>
    public const int MaxInputLength = 20000;

    /// <summary>工作流程的識別名稱（<see cref="AgentWorkflowDefinitionDto.Name"/>）。</summary>
    [Required(ErrorMessage = "請選擇工作流程。")]
    public string Workflow { get; set; } = string.Empty;

    [MaxLength(MaxInputLength, ErrorMessage = "輸入最多 20000 個字。")]
    public string Input { get; set; } = string.Empty;
}

/// <summary>
/// 回覆一筆等待中的請求，依請求種類只看對應的欄位：工具核准看 <see cref="ApprovedRequestIds"/>（其餘一律拒絕）、
/// 提問看 <see cref="Answer"/>（不可空白）、審閱看 <see cref="Decision"/> 與 <see cref="Comment"/>（退回要附意見，且還有退回次數）。
/// </summary>
public class AgentWorkflowRespondRequestDto
{
    /// <summary>回答與審閱意見的字數上限。</summary>
    public const int MaxTextLength = 2000;

    [Required(ErrorMessage = "缺少請求編號。")]
    public string RequestId { get; set; } = string.Empty;

    public List<string> ApprovedRequestIds { get; set; } = [];

    [MaxLength(MaxTextLength, ErrorMessage = "回答最多 2000 個字。")]
    public string? Answer { get; set; }

    /// <summary><see cref="AgentWorkflowReviewDecisions"/> 的其中一個（審閱關卡才需要）。</summary>
    public string? Decision { get; set; }

    [MaxLength(MaxTextLength, ErrorMessage = "意見最多 2000 個字。")]
    public string? Comment { get; set; }
}

/// <summary>審閱關卡的決定（伺服器 <c>AiReviewDecision</c> 的名稱）。</summary>
public static class AgentWorkflowReviewDecisions
{
    public const string Approve = "Approve";
    public const string Reject = "Reject";
    public const string End = "End";
}

/// <summary>
/// 自己正在等回覆的那一次執行（重新整理頁面後接回來用）：執行紀錄與等待中的請求。
/// 執行只存在伺服器這個行程的記憶體裡，閒置太久、網站重新啟動或 IIS 回收就沒了（紀錄仍在「我的執行紀錄」，狀態是中斷）。
/// </summary>
public class AgentWorkflowRunDto
{
    public Guid RunId { get; set; }

    /// <summary>工作流程的識別名稱。</summary>
    public string Workflow { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public List<AgentWorkflowEntryDto> Transcript { get; set; } = [];

    public List<AgentWorkflowPendingRequestDto> PendingRequests { get; set; } = [];
}
