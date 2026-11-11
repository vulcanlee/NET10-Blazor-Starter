using System.Text.Json;

namespace MyProject.Models.Systems;

/// <summary>
/// Agent 對話內容檔（0.9.143 起）：<c>{AiAgentConversationPath}/{yyyyMM}/{ConversationId:N}.json</c>，每次儲存整份覆寫。
///
/// <para>
/// 0.9.144 起「Agent 工作流程」的執行紀錄也存在這裡（<c>AgentName</c> 是流程名稱）：只用 <see cref="Transcript"/>，
/// <see cref="Session"/> 為 JSON null、沒有待核准（工作流程不能接續）。
///
/// 對話的三樣東西缺一不可：<see cref="Session"/> 讓 AI 記得前文，<see cref="Transcript"/> 讓畫面還原，
/// <see cref="PendingApprovals"/> 讓停在核准的對話回來時重新跳出核准窗。
/// 改結構要遞增 <see cref="SchemaVersion"/>，讀取端遇到不認得的版本就當作讀不到（不要猜）。
/// </para>
/// </summary>
public sealed class AiAgentConversationContent
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>MAF <c>AIAgent.SerializeSessionAsync</c> 的結果（對話歷史，含工具呼叫與結果）。</summary>
    public JsonElement Session { get; set; }

    /// <summary>畫面上的對話紀錄（使用者訊息、AI 回覆 Markdown、工具活動、說明與錯誤）。</summary>
    public List<AiAgentTranscriptEntry> Transcript { get; set; } = [];

    /// <summary>停下來等使用者核准的工具；沒有時為空。</summary>
    public List<AiAgentPendingApprovalRecord> PendingApprovals { get; set; } = [];

    /// <summary>
    /// 這段對話選的推論等級（0.9.170 起，Agent 測試台）；null＝系統預設。接續時沿用。
    /// 只加不改，所以 <see cref="SchemaVersion"/> 不變（舊檔讀出來是 null）。
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// 最後一輪的規劃結果（0.10.7 起，系統管理助理：分派到的領域代碼，例如 <c>accounts,ops</c>；空字串＝沒有分出領域）；
    /// 沒有每輪規劃器、或舊檔，為 null。WASM 版每個請求都重新接續對話，停在核准的對話靠它沿用提出核准的那一輪（工具與提示詞），
    /// 而不是開對話用的那一輪。只加不改，<see cref="SchemaVersion"/> 不變。
    /// </summary>
    public string? TurnKey { get; set; }
}

/// <summary>畫面上的一則。<see cref="Kind"/> 見 <see cref="AiAgentTranscriptKinds"/>。</summary>
public sealed class AiAgentTranscriptEntry
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>使用者訊息原文、AI 回覆的 Markdown，或說明文字。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 工作流程紀錄才有（0.9.144 起）：這一則屬於哪個 agent（或審閱關卡、產出標題）的顯示名稱。
    /// Agent 測試台的對話為 null。只加不改，所以 <see cref="AiAgentConversationContent.SchemaVersion"/> 不變。
    /// </summary>
    public string? Agent { get; set; }
}

public static class AiAgentTranscriptKinds
{
    public const string User = "User";
    public const string Assistant = "Assistant";

    /// <summary>Agent 呼叫了哪個工具（不含參數與結果，那些在 AI 對話紀錄）。</summary>
    public const string Tool = "Tool";

    /// <summary>核准／拒絕、停止等狀態說明。</summary>
    public const string Notice = "Notice";
    public const string Error = "Error";

    /// <summary>工作流程的產出（0.9.144 起，例如定稿的報告），<see cref="AiAgentTranscriptEntry.Agent"/> 是產出標題。</summary>
    public const string Output = "Output";
}

/// <summary>
/// 一筆待決定的核准。<see cref="Content"/> 是 Microsoft.Extensions.AI 的 <c>AIContent</c> JSON
/// （等使用者決定的是核准請求，因為沒有權限而自動拒絕的是核准回覆），以 <c>AIJsonUtilities.DefaultOptions</c> 序列化。
/// </summary>
public sealed class AiAgentPendingApprovalRecord
{
    public string ToolName { get; set; } = string.Empty;

    public bool AutoRejected { get; set; }

    public JsonElement Content { get; set; }

    /// <summary>
    /// 提出這次核准的那一輪標籤（0.9.158 起，系統管理助理）：需要核准的工具不會留下工具活動紀錄，
    /// 接續時靠它還原「由誰回答」。只加不改，<see cref="AiAgentConversationContent.SchemaVersion"/> 不變。
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// 核准窗的「目前狀態」（0.9.160 起，例如系統參數的目前值）：提出時算一次，接續時照樣顯示。
    /// 只加不改，<see cref="AiAgentConversationContent.SchemaVersion"/> 不變。
    /// </summary>
    public string? Preview { get; set; }
}

/// <summary>「我的對話」清單的一列。</summary>
public sealed record AiAgentConversationSummary(
    Guid ConversationId,
    string AgentName,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool HasPendingApproval);

/// <summary>讀回一段自己的對話：中繼資料與內容（內容檔不存在或讀不懂時 <see cref="Content"/> 為 null）。</summary>
public sealed record AiAgentConversationLoaded(AiAgentConversationSummary Summary, AiAgentConversationContent? Content);

/// <summary>
/// 儲存一段對話。<see cref="Title"/> 只在第一次儲存時使用（之後使用者可能改過名，不覆蓋）。
/// </summary>
public sealed record AiAgentConversationSnapshot(
    Guid ConversationId,
    string AgentName,
    string Title,
    bool HasPendingApproval,
    AiAgentConversationContent Content);
