using MyProject.Dtos.Commons;

namespace MyProject.Dtos.Admins;

/// <summary>使用者清單的查詢（0.10.4 起，<c>POST api/v1/users/search</c> 與 <c>export</c>）。關鍵字比對帳號、名稱、Email、角色名稱。</summary>
public class UserSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>true：查「顯示已刪除」的清單，固定依刪除時間由新到舊。</summary>
    public bool Deleted { get; set; }

    /// <summary>true：使用者按下「搜尋」，伺服器寫一筆搜尋稽核。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>
/// 使用者（0.10.4 起）。清單、單筆、新增與修改共用：
/// 讀取時不帶密碼；寫入時 <see cref="Password"/> 有值才設定新密碼（修改時留白表示沿用）。
/// <c>Can*</c> 是伺服器依規則（support 帳號、自己、狀態）算好的，畫面據此決定顯示哪些按鈕；伺服器寫入時仍會再檢查。
/// </summary>
public class UserDto
{
    public int Id { get; set; }

    public string Account { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>新密碼（只用於寫入；讀取時一律是空的）。</summary>
    public string? Password { get; set; }

    /// <summary>true 是啟用。</summary>
    public bool Status { get; set; } = true;

    public bool IsAdmin { get; set; }

    public bool MustChangePassword { get; set; }

    public int? RoleViewId { get; set; }

    public string RoleViewName { get; set; } = string.Empty;

    /// <summary>額外角色（只有單筆 <c>GET {id}</c> 與寫入時有）。</summary>
    public List<int> AdditionalRoleIds { get; set; } = [];

    /// <summary>直接指定的團隊（只有單筆 <c>GET {id}</c> 與寫入時有）。</summary>
    public List<string> TeamNames { get; set; } = [];

    public bool TwoFactorEnabled { get; set; }

    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>鎖定中時顯示的文字（伺服器本地時間，例如「鎖定至 14:30」）；沒有鎖定時為 null。</summary>
    public string? LockedText { get; set; }

    public string? OAuthProvider { get; set; }

    public DateTime CreateAt { get; set; }

    public DateTime UpdateAt { get; set; }

    /// <summary>樂觀並行的版本號：修改時帶回開窗時的值，與資料庫不同代表別人已先修改（409）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }

    public bool CanDelete { get; set; }

    public bool CanCopy { get; set; }

    public bool CanToggleStatus { get; set; }

    public bool CanForceLogout { get; set; }

    public bool CanResetTwoFactor { get; set; }

    /// <summary>以「複製」建立時的範本帳號（只用於新增，寫進稽核）。</summary>
    public string? CopiedFromAccount { get; set; }
}

/// <summary>角色下拉的一個選項（含預設團隊：存檔前的「確認團隊設定」要用）。</summary>
public class UserRoleOptionDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> DefaultTeams { get; set; } = [];
}

/// <summary>使用者表單的選項（<c>GET api/v1/users/options</c>）。</summary>
public class UserFormOptionsDto
{
    public List<UserRoleOptionDto> Roles { get; set; } = [];

    /// <summary>啟用中的團隊。</summary>
    public List<string> Teams { get; set; } = [];

    /// <summary>新增時預設的角色（「預設角色」；找不到時是第一個角色）。</summary>
    public int? DefaultRoleId { get; set; }

    /// <summary>密碼規則的說明文字（顯示在密碼欄下方）。</summary>
    public string PasswordRule { get; set; } = string.Empty;
}

/// <summary>「複製」的範本（<c>GET api/v1/users/{id}/copy-draft</c>）：角色、團隊、狀態、管理員照抄，帳號、名稱、密碼、Email 留空。</summary>
public class UserCopyDraftDto
{
    public UserDto Draft { get; set; } = new();

    /// <summary>範本的團隊裡已停用、不帶入的。</summary>
    public List<string> SkippedTeamNames { get; set; } = [];
}

/// <summary>修改的結果（<c>PUT api/v1/users/{id}</c>）。</summary>
public class UserSaveResultDto
{
    /// <summary>
    /// 改到自己的密碼、角色或管理員身分時，自己的工作階段版本已換掉：瀏覽器要整頁導到這個網址換發登入（其他裝置維持登出）。
    /// 沒有需要時為 null。
    /// </summary>
    public string? SessionRefreshUrl { get; set; }
}

/// <summary>查 Email 是否已有人使用（<c>POST api/v1/users/accounts-using-email</c>）。</summary>
public class EmailLookupRequestDto
{
    public string Email { get; set; } = string.Empty;
}
