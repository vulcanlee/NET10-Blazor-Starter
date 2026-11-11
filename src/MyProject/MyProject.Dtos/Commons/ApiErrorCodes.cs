namespace MyProject.Dtos.Commons;

/// <summary><see cref="ApiResult{T}.ErrorCode"/> 的可能值（0.10.0 起）。</summary>
public static class ApiErrorCodes
{
    /// <summary>必須先變更密碼（被管理員要求、初始密碼或密碼過期）；完成前只能使用登入相關 API。</summary>
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";

    /// <summary>必須先設定兩步驟驗證；完成前只能使用登入相關 API。</summary>
    public const string TwoFactorSetupRequired = "TWO_FACTOR_SETUP_REQUIRED";

    /// <summary>
    /// 帳號沒有主要角色，不能使用網頁（0.10.8 起，與 Server 版相同）：只對網頁的 Token（瀏覽器裡的 WASM 前端）回 403，
    /// 前端收到就登出。以 API 登入的 Token（App、整合程式）不受影響，權限照 RBAC 判斷。
    /// </summary>
    public const string NoPrimaryRole = "NO_PRIMARY_ROLE";

    /// <summary>兩步驟驗證的待驗票證無效、過期或已用過（0.10.8 起）：前端回登入頁重新輸入帳號密碼。</summary>
    public const string TwoFactorTicketInvalid = "TWO_FACTOR_TICKET_INVALID";
}
