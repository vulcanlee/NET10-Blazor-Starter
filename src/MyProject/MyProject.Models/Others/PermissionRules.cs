using MyProject.Models.Others;
using MyProject.Share.Helpers;

namespace MyProject.Business.Helpers;

/// <summary>
/// 畫面上依動作顯示按鈕的判斷（0.9.110 起抽成純函式，<c>&lt;RequirePermission&gt;</c> 元件與伺服器的選單過濾共用，才能直接寫單元測試）。
/// 0.10.2 起放在 MyProject.Models（WASM 遷移 M3），WASM 頁面與伺服器共用同一份規則；命名空間維持不變。
/// </summary>
public static class PermissionRules
{
    /// <summary>
    /// 每個登入的人都能開的頁面（0.10.17 起，使用者 2026/11/14 決定）：首頁。不在角色矩陣裡、不能被授予或拿掉。
    /// 首頁裡的小工具仍依各自的權限顯示。
    /// </summary>
    public static readonly IReadOnlySet<string> OpenToEveryone = new HashSet<string>(StringComparer.Ordinal) { MagicObjectHelper.角色_首頁 };

    /// <summary>
    /// 能否進入頁面（也用於側邊選單過濾）：管理員、每個人都能開的頁面（<see cref="OpenToEveryone"/>）、擁有裸頁面鍵、或擁有該頁的 <c>view</c> 動作鍵。
    /// ⚠️ 最後一項不可省略 —— 角色矩陣只勾「檢視」時產生的是「頁面:view」，不含裸鍵；少了它唯讀角色連頁面都打不開。
    /// </summary>
    public static bool CanAccessPage(CurrentUser user, string page)
    {
        if (user.IsAdmin || OpenToEveryone.Contains(page))
        {
            return true;
        }

        var keys = user.RoleList;
        return keys.Contains(page) || keys.Contains(PermissionKey.For(page, PermissionActions.View));
    }

    /// <summary>
    /// 管理員一律通過；擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。
    /// 只有「頁面:view」不代表可以新增、修改或刪除。
    /// </summary>
    public static bool CanAccessAction(CurrentUser user, string page, string action)
    {
        if (user.IsAdmin)
        {
            return true;
        }

        var keys = user.RoleList;
        return keys.Contains(PermissionKey.For(page, action)) || keys.Contains(page);
    }
}
