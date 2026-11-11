using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Auths;

/// <summary>
/// 登入頁、忘記／重設密碼頁、啟動頁要顯示的設定（<c>GET api/v1/Auth/login-settings</c>，不需登入，0.10.8 起）。
/// 只有公開資訊：哪些功能有開、鎖定規則、密碼規則說明、系統名稱與簡介。
/// </summary>
public class LoginPageSettingsDto
{
    public string SystemName { get; set; } = string.Empty;

    public string SystemDescription { get; set; } = string.Empty;

    /// <summary>Google 登入已設定（登入頁顯示 Google 按鈕）。</summary>
    public bool GoogleEnabled { get; set; }

    /// <summary>寄信已啟用（Provider ≠ None）：才有忘記密碼可用。</summary>
    public bool EmailEnabled { get; set; }

    /// <summary>連續輸錯幾次密碼會暫時鎖定。</summary>
    public int MaxFailedAttempts { get; set; }

    /// <summary>鎖定幾分鐘。</summary>
    public int LockoutMinutes { get; set; }

    /// <summary>兩步驟驗證「記住這台裝置」幾天（0＝不提供）。</summary>
    public int RememberDeviceDays { get; set; }

    /// <summary>「記住我」維持登入幾天。</summary>
    public int RememberMeDays { get; set; }

    /// <summary>密碼規則的說明文字（與變更密碼頁相同）。</summary>
    public string PasswordRule { get; set; } = string.Empty;

    /// <summary>重設密碼連結的有效分鐘數。</summary>
    public int ResetLinkLifetimeMinutes { get; set; }
}

/// <summary>網頁登入（<c>POST api/v1/Auth/login/web</c>，0.10.8 起）。App 與整合程式繼續用 <c>POST api/v1/Auth/login</c>。</summary>
public class WebLoginRequestDto
{
    [Required(ErrorMessage = "帳號 不可為空白")]
    [MaxLength(200)]
    public string Account { get; set; } = string.Empty;

    [Required(ErrorMessage = "密碼 不可為空白")]
    [MaxLength(500)]
    public string Password { get; set; } = string.Empty;

    /// <summary>勾了「記住我」：Token 存 localStorage、整個登入最長 <c>SessionSettings:RememberMeDays</c> 天；沒勾存 sessionStorage、閒置逾時登出。</summary>
    public bool RememberMe { get; set; }

    /// <summary>上次兩步驟驗證勾「記住這台裝置」拿到的 Token（瀏覽器自己保存）；有效時不必再輸入驗證碼。</summary>
    [MaxLength(2000)]
    public string? RememberDeviceToken { get; set; }

    /// <summary>登入後要回到的站內網址（伺服器會檢查，不是站內網址就回 <c>/App</c>）。</summary>
    [MaxLength(2000)]
    public string? ReturnUrl { get; set; }
}

/// <summary>
/// 網頁登入、兩步驟驗證、Google 兌換的結果（0.10.8 起）：成功時 <see cref="Tokens"/> 有值；
/// 還要第二步時 <see cref="RequiresTwoFactor"/> 為 true，前端帶 <see cref="TwoFactorTicket"/> 到兩步驟驗證頁。
/// </summary>
public class WebLoginResultDto
{
    public TokenResponseDto? Tokens { get; set; }

    public bool RequiresTwoFactor { get; set; }

    /// <summary>待驗票證（5 分鐘、加密、綁定這個人與工作階段版本；成功一次後作廢）。</summary>
    public string? TwoFactorTicket { get; set; }

    /// <summary>兩步驟驗證頁「記住這台裝置 N 天」的 N（0＝不顯示）。</summary>
    public int RememberDeviceDays { get; set; }

    /// <summary>兩步驟驗證勾了「記住這台裝置」時發的 Token（瀏覽器存在 localStorage；換工作階段版本即失效）。</summary>
    public string? RememberDeviceToken { get; set; }

    /// <summary>登入後要去的站內網址（已檢查）。</summary>
    public string ReturnUrl { get; set; } = "/App";
}

/// <summary>登入第二步（<c>POST api/v1/Auth/two-factor</c>，0.10.8 起）。</summary>
public class TwoFactorLoginRequestDto
{
    [Required(ErrorMessage = "登入已逾時，請重新登入。")]
    [MaxLength(4000)]
    public string Ticket { get; set; } = string.Empty;

    /// <summary>6 位數驗證碼或一組備用碼。</summary>
    [MaxLength(100)]
    public string? Code { get; set; }

    public bool RememberDevice { get; set; }
}

/// <summary>Google 登入回呼產生的一次性代碼換 Token（<c>POST api/v1/Auth/google/exchange</c>，0.10.8 起）。</summary>
public class GoogleExchangeRequestDto
{
    [Required(ErrorMessage = "Google 登入已逾時，請重新登入。")]
    [MaxLength(200)]
    public string Code { get; set; } = string.Empty;

    /// <summary>發起 Google 登入的瀏覽器自己保存的驗證值（雜湊在發起時帶給伺服器），證明是同一個瀏覽器。</summary>
    [Required(ErrorMessage = "Google 登入已逾時，請重新登入。")]
    [MaxLength(200)]
    public string Verifier { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? RememberDeviceToken { get; set; }
}

/// <summary>忘記密碼（<c>POST api/v1/Auth/password/forgot</c>，0.10.8 起）。</summary>
public class ForgotPasswordRequestDto
{
    [Required(ErrorMessage = "請輸入帳號或 Email")]
    [MaxLength(256)]
    public string Identifier { get; set; } = string.Empty;
}

/// <summary>重設密碼連結（<c>POST api/v1/Auth/password/reset/check</c>，0.10.8 起；Token 放本文，不進網址與日誌）。</summary>
public class ResetPasswordCheckRequestDto
{
    [MaxLength(200)]
    public string? Token { get; set; }
}

public class ResetPasswordCheckDto
{
    public bool Valid { get; set; }

    /// <summary>連結有效時，要重設的帳號。</summary>
    public string? Account { get; set; }

    public string PasswordRule { get; set; } = string.Empty;
}

/// <summary>以重設連結設定新密碼（<c>POST api/v1/Auth/password/reset</c>，0.10.8 起）。</summary>
public class ResetPasswordRequestDto
{
    [MaxLength(200)]
    public string? Token { get; set; }

    [MaxLength(500)]
    public string? NewPassword { get; set; }

    [MaxLength(500)]
    public string? ConfirmPassword { get; set; }
}

/// <summary>重設密碼失敗的細節：連結本身已無效（要重新申請）時為 true，只是新密碼不合規則時為 false（同一個連結還能再送）。</summary>
public class ResetPasswordResultDto
{
    public bool LinkInvalid { get; set; }
}

/// <summary>變更密碼頁的資料（<c>GET api/v1/Auth/password/change</c>，0.10.8 起）。</summary>
public class ChangePasswordStatusDto
{
    public string Account { get; set; } = string.Empty;

    /// <summary>support 帳號禁止變更密碼。</summary>
    public bool IsSupportAccount { get; set; }

    /// <summary>被要求變更密碼的原因（管理員要求或已到期）；不需要時為空字串。</summary>
    public string RequiredReason { get; set; } = string.Empty;

    public string PasswordRule { get; set; } = string.Empty;
}

/// <summary>變更自己的密碼（<c>POST api/v1/Auth/password/change</c>，0.10.8 起）。</summary>
public class ChangePasswordRequestDto
{
    [Required(ErrorMessage = "目前密碼 不可為空白")]
    [MaxLength(500)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "新密碼 不可為空白")]
    [MaxLength(500)]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "確認新密碼 不可為空白")]
    [MaxLength(500)]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// 做了會換掉自己工作階段版本的事（改密碼、啟用／停用兩步驟驗證、管理員改到自己）之後的結果（0.10.8 起）：
/// 這台裝置的新 Token（舊的一串已撤銷；其他裝置登出）。
/// </summary>
public class SessionRenewalDto
{
    /// <summary>這台裝置的新 Token；版本沒換，或呼叫時用的不是可換發的 Token（例如 0.10.8 以前發的）時為 null。</summary>
    public TokenResponseDto? Tokens { get; set; }
}

/// <summary>兩步驟驗證設定頁的狀態（<c>GET api/v1/Auth/two-factor/setup</c>，0.10.8 起）。</summary>
public class TwoFactorSetupStatusDto
{
    public string Account { get; set; } = string.Empty;

    /// <summary>有系統內的密碼（只用 Google 登入的帳號不需要設定）。</summary>
    public bool HasLocalPassword { get; set; }

    public bool IsEnabled { get; set; }

    /// <summary>系統要求這個帳號使用兩步驟驗證。</summary>
    public bool IsRequired { get; set; }

    public int RemainingBackupCodes { get; set; }
}

/// <summary>開始設定（<c>POST api/v1/Auth/two-factor/setup/start</c>）：QR Code 與金鑰；金鑰確認前不存進資料庫，由設定票證帶著。</summary>
public class TwoFactorSetupStartDto
{
    /// <summary>伺服器產生的 PNG（data URI）。</summary>
    public string QrDataUri { get; set; } = string.Empty;

    /// <summary>無法掃描時手動輸入的金鑰。</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>加密、綁定這個人、15 分鐘有效的設定票證；啟用時帶回來。</summary>
    public string SetupTicket { get; set; } = string.Empty;
}

/// <summary>以 App 上的驗證碼確認並啟用（<c>POST api/v1/Auth/two-factor/setup/enable</c>）。</summary>
public class TwoFactorEnableRequestDto
{
    [Required(ErrorMessage = "設定已逾時，請重新開始設定。")]
    [MaxLength(4000)]
    public string SetupTicket { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Code { get; set; }
}

/// <summary>重新產生備用碼、停用時確認是本人（驗證碼或一組備用碼）。</summary>
public class TwoFactorCodeRequestDto
{
    [MaxLength(100)]
    public string? Code { get; set; }
}

/// <summary>啟用或重新產生備用碼的結果：備用碼原文只出現這一次。啟用時另帶換發的 Token（同 <see cref="SessionRenewalDto"/>）。</summary>
public class TwoFactorBackupCodesDto : SessionRenewalDto
{
    public List<string> BackupCodes { get; set; } = [];
}
