using MyProject.Models.AdapterModel;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 稽核紀錄的顯示規則（0.10.2 起，WASM 遷移 M3 從 <c>AuditLogView</c> 抽出）：「稽核紀錄」頁與「異動歷程」對話窗
/// （Server 與 WASM 頁面共用）用同一份，顏色與操作者寫法不會走樣。
/// </summary>
public static class AuditLogDisplay
{
    /// <summary>操作者顯示文字；系統或匿名事件（例如帳號不存在的登入失敗）沒有 Id。</summary>
    public static string ActorText(AuditLogAdapterModel item)
    {
        var account = string.IsNullOrWhiteSpace(item.ActorAccount) ? "（系統／匿名）" : item.ActorAccount;
        return item.ActorUserId is null ? account : $"{account}（Id={item.ActorUserId}）";
    }

    /// <summary>
    /// 依動作的第一節上色，讓「登入」「帳號異動」「權限拒絕」三類一眼分得開。
    /// 用 AntDesign Tag 的具名顏色，不寫色碼字面值（ThemeConventionTests 守門）。
    /// </summary>
    public static string GetActionColor(string category) => category switch
    {
        "Login" => "blue",
        "User" => "green",
        "Role" => "purple",
        "Permission" => "red",
        "Audit" => "volcano",
        "Logout" => "blue",
        "Token" => "red",
        "Project" => "geekblue",
        "Category" => "lime",
        "Team" => "gold",
        "ExceptionLog" => "magenta",
        "EmailLog" => "magenta",
        "AiCallLog" => "magenta",
        "AiAgentConversation" => "magenta",
        "AdminPatrol" => "magenta",
        "TokenUsage" => "magenta",
        "LogLevel" => "cyan",
        "LogViewer" => "cyan",
        "Email" => "cyan",
        "Password" => "orange",
        "Job" => "cyan",
        "SystemParameter" => "cyan",
        "Backup" => "orange",
        "Announcement" => "cyan",
        "Notification" => "cyan",
        // 開頁（0.9.131 起）筆數最多，刻意用灰色，不搶增刪改的視線。
        "Page" => "default",
        _ => "default",
    };
}
