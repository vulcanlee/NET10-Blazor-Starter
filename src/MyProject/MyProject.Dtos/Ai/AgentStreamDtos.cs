namespace MyProject.Dtos.Ai;

/// <summary>
/// Agent 對話串流的一行（0.10.7 起，WASM 遷移 M8；<c>POST …/messages</c> 與 <c>POST …/conversations/{id}/approvals</c>）。
/// 回應是 NDJSON：第一行是 <see cref="AgentStreamFrameKinds.Start"/>（對話 ID、模型），之後依序是新增的紀錄、文字片段、工具活動、
/// 「由誰回答」、待核准清單，最後一行是 <see cref="AgentStreamFrameKinds.Result"/>（成功、失敗、取消都是）或
/// <see cref="AgentStreamFrameKinds.Approvals"/>（停下來等核准，之後不會再有 result）。閒置時插入 <see cref="AgentStreamFrameKinds.Ping"/>，畫面忽略即可。
/// 使用者中斷連線時伺服器取消這次執行，已收到的內容照樣存檔（與 Server 版相同）。
/// </summary>
public class AgentStreamFrameDto
{
    /// <summary><see cref="AgentStreamFrameKinds"/> 的其中一個。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary><see cref="AgentStreamFrameKinds.Start"/>：對話 ID（開新對話時由伺服器產生）。</summary>
    public Guid? ConversationId { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Start"/>：這次使用的模型。</summary>
    public string? ModelName { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Start"/>：這段對話的推論等級（null＝系統預設）。</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Entry"/>：新增到畫面紀錄的一則。</summary>
    public AgentTranscriptEntryDto? Entry { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Delta"/>：這一段新收到的文字。</summary>
    public string? Text { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Tool"/>：工具開始或結束執行。</summary>
    public AgentToolActivityDto? Tool { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Approvals"/>：等使用者決定的工具呼叫。</summary>
    public List<AgentApprovalRequestDto>? Approvals { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Label"/>：這一輪「由誰回答」（系統管理助理分派完成時）。</summary>
    public string? Label { get; set; }

    /// <summary><see cref="AgentStreamFrameKinds.Result"/>：這次執行的結果。</summary>
    public AiResultDto? Result { get; set; }
}

public static class AgentStreamFrameKinds
{
    public const string Start = "start";
    public const string Entry = "entry";
    public const string Delta = "delta";
    public const string Tool = "tool";
    public const string Approvals = "approvals";
    public const string Label = "label";
    public const string Result = "result";
    public const string Ping = "ping";
}

/// <summary>工具開始（<see cref="Completed"/> 為 false）或結束執行。</summary>
public class AgentToolActivityDto
{
    public string ToolName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool Completed { get; set; }
}
