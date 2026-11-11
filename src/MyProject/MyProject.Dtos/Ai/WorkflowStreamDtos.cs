namespace MyProject.Dtos.Ai;

/// <summary>
/// Agent 工作流程串流的一行（0.10.7 起，WASM 遷移 M8）。回應是 NDJSON：第一行一定是 <see cref="AgentWorkflowFrameKinds.Start"/>，
/// 之後是各 agent 的開始、文字片段、工具活動、完成、等使用者回覆、產出與心跳，最後一行是 <see cref="AgentWorkflowFrameKinds.Result"/>
/// （停下來等使用者時沒有 result，串流在 request 之後結束）。
///
/// <para>
/// 每一行都可能帶 <see cref="Entries"/>：伺服器執行紀錄這一行之前新增的幾則（使用者輸入、完成的回覆、工具呼叫、核准結果、產出…）。
/// 畫面先把它們接到紀錄後面，再處理這一行本身 —— 紀錄與伺服器存檔的內容一模一樣。
/// </para>
/// </summary>
public class AgentWorkflowFrameDto
{
    /// <summary><see cref="AgentWorkflowFrameKinds"/> 的其中一個。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>這一行屬於哪個 agent（顯示名稱）；請求、產出、結果與心跳可能沒有。</summary>
    public string? Agent { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Start"/>：這次執行的識別碼（也是「我的執行紀錄」的識別碼）。</summary>
    public Guid? RunId { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Start"/>：使用的模型。</summary>
    public string? ModelName { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Delta"/>：這一段新收到的文字。</summary>
    public string? Text { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Tool"/>：工具開始或結束。</summary>
    public AgentWorkflowToolActivityDto? Tool { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Request"/>：停下來等使用者回覆的請求。</summary>
    public AgentWorkflowPendingRequestDto? Request { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Output"/>：工作流程的產出。</summary>
    public AgentWorkflowOutputDto? Output { get; set; }

    /// <summary><see cref="AgentWorkflowFrameKinds.Result"/>：整個流程的結果。</summary>
    public AgentWorkflowResultDto? Result { get; set; }

    /// <summary>這一行之前執行紀錄新增的幾則；沒有時為 null。</summary>
    public List<AgentWorkflowEntryDto>? Entries { get; set; }
}

public static class AgentWorkflowFrameKinds
{
    public const string Start = "start";
    public const string AgentStarted = "agentStarted";
    public const string Delta = "delta";
    public const string Tool = "tool";
    public const string AgentCompleted = "agentCompleted";
    public const string Request = "request";
    public const string Output = "output";
    public const string Result = "result";

    /// <summary>心跳：很久沒有資料時插入，讓中間的代理伺服器不把連線當成閒置切斷。畫面忽略。</summary>
    public const string Ping = "ping";
}

/// <summary>執行紀錄的一則（同伺服器存檔的 <c>AiAgentTranscriptEntry</c>）。</summary>
public class AgentWorkflowEntryDto
{
    /// <summary><c>AiAgentTranscriptKinds</c> 的其中一個（User、Assistant、Tool、Notice、Error、Output）。</summary>
    public string Kind { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string? Agent { get; set; }
}

public class AgentWorkflowToolActivityDto
{
    public string ToolName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Completed { get; set; }
}

/// <summary>一筆等使用者回覆的請求（工具核准、Agent 提問、審閱關卡三種，依 <see cref="Kind"/> 只填對應的欄位）。</summary>
public class AgentWorkflowPendingRequestDto
{
    /// <summary>回覆時帶回來。</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary><see cref="AgentWorkflowRequestKinds"/> 的其中一個。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>發出請求的 agent（或審閱關卡）顯示名稱。</summary>
    public string Agent { get; set; } = string.Empty;

    public List<AgentWorkflowApprovalDto> Approvals { get; set; } = [];

    public string? Question { get; set; }

    public string? Draft { get; set; }

    /// <summary>審閱關卡還能退回幾次；0 表示只剩「通過」與「結束」。</summary>
    public int RejectionsLeft { get; set; }
}

public static class AgentWorkflowRequestKinds
{
    public const string ToolApproval = "ToolApproval";
    public const string Question = "Question";
    public const string Review = "Review";
}

/// <summary>一筆待核准的工具呼叫。</summary>
public class AgentWorkflowApprovalDto
{
    public string RequestId { get; set; } = string.Empty;

    public string ToolName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<AgentWorkflowArgumentDto> Arguments { get; set; } = [];
}

public class AgentWorkflowArgumentDto
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public class AgentWorkflowOutputDto
{
    public string Title { get; set; } = string.Empty;

    public string Markdown { get; set; } = string.Empty;
}

/// <summary>整個流程的結果。</summary>
public class AgentWorkflowResultDto
{
    /// <summary><see cref="AgentWorkflowOutcomes"/> 的其中一個。</summary>
    public string Outcome { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public int ModelCalls { get; set; }

    public AiUsageDto? Usage { get; set; }

    public long ElapsedMilliseconds { get; set; }
}

/// <summary>伺服器 <c>AiWorkflowOutcome</c> 的名稱。</summary>
public static class AgentWorkflowOutcomes
{
    public const string Completed = "Completed";

    /// <summary>使用者在審閱關卡選「結束」，沒有定稿。</summary>
    public const string Ended = "Ended";

    public const string Failed = "Failed";

    /// <summary>使用者按「停止」。</summary>
    public const string Canceled = "Canceled";

    /// <summary>執行中離開頁面（或閒置太久）。</summary>
    public const string Interrupted = "Interrupted";
}
