namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理核准窗的「核准」規則（0.9.159 起）：任一筆是不可復原的動作時，必須輸入「確認」（前後空白不計）才能核准；
/// 全部可以復原時直接核准。核准後的權限檢查與執行仍由工具外殼與工具本身負責。
/// 0.10.7 起（WASM 遷移 M8）搬到 Client、命名空間不變：畫面用它決定「核准」能不能按，伺服器的核准 API 用同一份規則再擋一次（沒輸入回 400）。
/// </summary>
public static class AdminAssistantApproval
{
    public const string ConfirmText = MyProject.Dtos.Ai.AgentConversationLimits.ConfirmText;

    public static bool RequiresConfirmation(IReadOnlyList<AiToolApprovalRequest> requests)
        => requests.Any(request => request.IsIrreversible);

    public static bool CanApprove(IReadOnlyList<AiToolApprovalRequest> requests, string? typed)
        => requests.Count > 0
            && (RequiresConfirmation(requests) == false || string.Equals(typed?.Trim(), ConfirmText, StringComparison.Ordinal));
}
