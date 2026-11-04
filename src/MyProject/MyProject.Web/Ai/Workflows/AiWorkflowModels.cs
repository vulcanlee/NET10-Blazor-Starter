using Microsoft.Agents.AI.Workflows;
using MyProject.Web.Ai.Agents;

namespace MyProject.Web.Ai.Workflows;

/// <summary>步驟之間傳遞的文字（使用者輸入、上一個 agent 的產出）。<see cref="From"/> 是送出它的 agent 顯示名稱。</summary>
public sealed record AiWorkflowMessage(string Text, string? From = null);

/// <summary>審閱關卡退回時送給撰寫者的意見。</summary>
public sealed record AiWorkflowRevision(string Comment);

/// <summary>工作流程的產出（例如定稿的報告、彙整後的意見）。</summary>
public sealed record AiWorkflowOutput(string Title, string Markdown);

/// <summary>停下來等使用者的三種情況。</summary>
public enum AiWorkflowRequestKind
{
    /// <summary>agent 要執行會改資料的工具，等使用者核准（同 Agent 測試台）。</summary>
    ToolApproval,

    /// <summary>agent 資訊不足，呼叫 <c>ask_user</c> 向使用者提問。</summary>
    Question,

    /// <summary>審閱關卡：使用者看草稿，決定通過、退回或結束。</summary>
    Review,
}

/// <summary>
/// 步驟送給使用者的請求（經 MAF 的 <see cref="RequestPort"/>，畫面以 <c>RequestInfoEvent</c> 收到）。
/// 依 <see cref="Kind"/> 只會填對應的欄位。
/// </summary>
public sealed record AiWorkflowHumanRequest
{
    public required AiWorkflowRequestKind Kind { get; init; }

    /// <summary>發出請求的 agent（或關卡）顯示名稱。</summary>
    public required string Agent { get; init; }

    public IReadOnlyList<AiToolApprovalRequest> Approvals { get; init; } = [];

    public string? Question { get; init; }

    public string? Draft { get; init; }

    /// <summary>審閱關卡還能退回幾次；0 表示只剩「通過」與「結束」。</summary>
    public int RejectionsLeft { get; init; }
}

/// <summary>審閱關卡的決定。</summary>
public enum AiReviewDecision
{
    Approve,
    Reject,
    End,
}

/// <summary>使用者對 <see cref="AiWorkflowHumanRequest"/> 的回覆，依請求種類只看對應的欄位。</summary>
public sealed record AiWorkflowHumanResponse
{
    /// <summary>工具核准：核准的 RequestId，其餘一律視為拒絕。</summary>
    public IReadOnlyCollection<string> ApprovedRequestIds { get; init; } = [];

    /// <summary>Agent 提問的回答。</summary>
    public string? Answer { get; init; }

    public AiReviewDecision Decision { get; init; }

    /// <summary>退回的意見（退回時必填）。</summary>
    public string? Comment { get; init; }
}

/// <summary>畫面上一筆等使用者回覆的請求。<see cref="RequestId"/> 是 MAF 的請求編號，回覆時帶回來。</summary>
public sealed record AiWorkflowPendingRequest(string RequestId, AiWorkflowHumanRequest Request);

/// <summary>整個工作流程的結果。</summary>
public enum AiWorkflowOutcome
{
    /// <summary>跑完。</summary>
    Completed,

    /// <summary>使用者在審閱關卡選「結束」，沒有定稿。</summary>
    Ended,

    Failed,

    /// <summary>使用者按「停止」。</summary>
    Canceled,

    /// <summary>執行中離開頁面。工作流程不能從中途接續。</summary>
    Interrupted,
}

public sealed record AiWorkflowResult(
    AiWorkflowOutcome Outcome,
    string? ErrorMessage,
    int ModelCalls,
    AiTokenUsage? Usage,
    TimeSpan Elapsed);

/// <summary>
/// 工作流程執行的一筆更新，一次只有一種：某個 agent 開始、文字片段、工具活動、某個 agent 完成、
/// 等使用者回覆、產出，或整個流程結束（<see cref="Result"/>）。
/// </summary>
public sealed record AiWorkflowUpdate
{
    /// <summary>這筆更新屬於哪個 agent（顯示名稱）；請求、產出與結果可能沒有。</summary>
    public string? Agent { get; init; }

    public bool AgentStarted { get; init; }

    public string TextDelta { get; init; } = string.Empty;

    public AiToolActivity? ToolActivity { get; init; }

    public bool AgentCompleted { get; init; }

    public AiWorkflowPendingRequest? Request { get; init; }

    public AiWorkflowOutput? Output { get; init; }

    public AiWorkflowResult? Result { get; init; }
}

/// <summary>步驟把即時更新（文字片段、工具活動）送出工作流程的事件。</summary>
public sealed class AiWorkflowStepEvent(AiWorkflowUpdate update) : WorkflowEvent(update)
{
    public AiWorkflowUpdate Update { get; } = update;
}
