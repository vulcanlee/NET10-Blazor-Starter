namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理核准窗的「核准」規則（0.9.159 起）：任一筆是不可復原的動作時，必須輸入「確認」（前後空白不計）才能核准；
/// 全部可以復原時直接核准。只管畫面，核准後的權限檢查與執行仍由工具外殼與工具本身負責。
/// </summary>
public static class AdminAssistantApproval
{
    public const string ConfirmText = "確認";

    public static bool RequiresConfirmation(IReadOnlyList<AiToolApprovalRequest> requests)
        => requests.Any(request => request.IsIrreversible);

    public static bool CanApprove(IReadOnlyList<AiToolApprovalRequest> requests, string? typed)
        => requests.Count > 0
            && (RequiresConfirmation(requests) == false || string.Equals(typed?.Trim(), ConfirmText, StringComparison.Ordinal));
}
