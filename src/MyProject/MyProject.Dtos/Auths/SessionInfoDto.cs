namespace MyProject.Dtos.Auths;

/// <summary>
/// <c>GET /api/v1/Auth/me</c> 的回傳（0.10.0 起取代只靠 Token claims 的 <see cref="CurrentUserDto"/>，欄位是它的超集）。
/// 每次都從資料庫讀，WASM 前端與 App 據此決定選單、按鈕，以及是否要先改密碼或設定兩步驟驗證。
/// ⚠️ 權限只決定畫面顯示，真正的授權仍由每個 API 判斷。
/// </summary>
public class SessionInfoDto
{
    public int Id { get; set; }

    public string Account { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public bool IsAdmin { get; set; }

    /// <summary>有效權限鍵（所有角色的聯集）；管理員一律通過，不另列出。</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>有效團隊名稱。</summary>
    public List<string> Teams { get; set; } = [];

    /// <summary>必須先變更密碼；為 true 時業務 API 一律回 403（<c>PASSWORD_CHANGE_REQUIRED</c>）。</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>必須先設定兩步驟驗證；為 true 時業務 API 一律回 403（<c>TWO_FACTOR_SETUP_REQUIRED</c>）。</summary>
    public bool RequiresTwoFactorSetup { get; set; }

    public bool TwoFactorEnabled { get; set; }
}
