namespace MyProject.Dtos.Shell;

/// <summary>個人資料頁（<c>GET /api/v1/profile</c>）。時間已換成伺服器的本地時間。</summary>
public class ProfileDto
{
    public int Id { get; set; }

    public string Account { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public bool IsGoogleAccount { get; set; }

    public bool HasLocalPassword { get; set; }

    public List<string> Roles { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public List<string> SubordinateTeams { get; set; } = [];

    public DateTime? PasswordChangedAtLocal { get; set; }

    public DateTime? PasswordExpiresAtLocal { get; set; }

    public bool MustChangePassword { get; set; }

    public bool TwoFactorEnabled { get; set; }

    /// <summary>修改姓名時帶回，用來偵測別人同時改過。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public List<LoginRecordDto> RecentLogins { get; set; } = [];
}

public class LoginRecordDto
{
    public DateTime OccurredAtLocal { get; set; }

    /// <summary>已轉成中文的動作說明（例如「密碼登入」）。</summary>
    public string Action { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? ClientIp { get; set; }
}

/// <summary>修改自己的姓名（<c>PUT /api/v1/profile/name</c>）。</summary>
public class UpdateProfileNameDto
{
    public string? Name { get; set; }

    public string? ConcurrencyStamp { get; set; }
}
