namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 一筆待核准的工具呼叫。0.10.7 起（WASM 遷移 M8）搬到 Client 與伺服器共用，命名空間不變：
/// 伺服器的對話物件產生它，WASM 的核准窗呈現它（<c>AgentDtoMapper</c> 與 API 的 DTO 互轉）。
/// </summary>
/// <param name="Note">工具的核准說明（伺服器 <c>AiToolDefinition.ApprovalNote</c>，0.9.158 起）；沒有時為 null。</param>
/// <param name="Label">提出這次核准的專家（每輪規劃的標籤，0.9.158 起）；沒有規劃器時為 null。</param>
/// <param name="IsIrreversible">不可復原的動作（伺服器 <c>AiToolDefinition.IsIrreversible</c>，0.9.159 起）：核准窗加紅字警告並要求輸入確認。</param>
/// <param name="Preview">核准窗的「目前狀態」（伺服器 <c>AiToolDefinition.ApprovalPreview</c>，0.9.160 起）；提出時算一次，接續時從紀錄還原。</param>
public sealed record AiToolApprovalRequest(
    string RequestId,
    string ToolName,
    string DisplayName,
    IReadOnlyList<KeyValuePair<string, string>> Arguments,
    string? Note = null,
    string? Label = null,
    bool IsIrreversible = false,
    string? Preview = null);
