using System.Text.RegularExpressions;

namespace MyProject.Share.Helpers;

/// <summary>
/// 帳號格式（0.10.17 起，使用者 2026/11/14 決定）：只能用英數字與 <c>- _ . @</c>。以前沒有規則，含空白與 HTML 字元也能建立。
/// 只在「新增」或「改帳號」時檢查：既有帳號（例如 Google 登入以 Email 建立、含 <c>+</c> 的）不改帳號時照樣能存。
/// 伺服器（<c>MyUserService</c>）與畫面（帳號欄位的即時檢查）共用。
/// </summary>
public static partial class AccountRules
{
    public const string FormatMessage = "帳號只能使用英文字母、數字與 - _ . @。";

    public static bool IsValidFormat(string? account)
        => !string.IsNullOrEmpty(account) && AllowedPattern().IsMatch(account);

    [GeneratedRegex(@"^[A-Za-z0-9._@-]+$")]
    private static partial Regex AllowedPattern();
}
