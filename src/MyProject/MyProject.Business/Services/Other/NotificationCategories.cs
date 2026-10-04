namespace MyProject.Business.Services.Other;

/// <summary>內建的通知分類（0.9.100 起）。衍生專案的業務通知可自行定義新的分類字串（最多 32 字）。</summary>
public static class NotificationCategories
{
    /// <summary>排程作業失敗 → 管理員＋手動觸發者（同時寄信）。</summary>
    public const string JobFailed = "JobFailed";

    /// <summary>Google 第一次登入建立了待開通的帳號 → 管理員。</summary>
    public const string AccountPending = "AccountPending";

    /// <summary>帳號因連續登入失敗被鎖定 → 管理員（同時寄信；0.9.101 起）。</summary>
    public const string AccountLocked = "AccountLocked";

    /// <summary>密碼即將到期 → 本人（0.9.101 起，啟用密碼到期時）。</summary>
    public const string PasswordExpiring = "PasswordExpiring";
}
