namespace MyProject.Dtos.Commons;

/// <summary><see cref="ApiResult{T}.ErrorCode"/> 的可能值（0.10.0 起）。</summary>
public static class ApiErrorCodes
{
    /// <summary>必須先變更密碼（被管理員要求、初始密碼或密碼過期）；完成前只能使用登入相關 API。</summary>
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";

    /// <summary>必須先設定兩步驟驗證；完成前只能使用登入相關 API。</summary>
    public const string TwoFactorSetupRequired = "TWO_FACTOR_SETUP_REQUIRED";
}
