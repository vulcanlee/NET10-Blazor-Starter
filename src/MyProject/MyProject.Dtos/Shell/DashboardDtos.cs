namespace MyProject.Dtos.Shell;

/// <summary>
/// 登入後首頁（0.10.6 起，WASM 遷移 M7，<c>GET api/v1/dashboard</c>）：歡迎列、快速入口與這個人看得到的小工具。
/// 快速入口與小工具都由伺服器依權限過濾，畫面只渲染收到的；每個小工具的資料各有自己的端點。
/// </summary>
public class DashboardDto
{
    public string SystemName { get; set; } = string.Empty;

    public string SystemDescription { get; set; } = string.Empty;

    public string SystemVersion { get; set; } = string.Empty;

    /// <summary>執行環境名稱（Development、Production…）。</summary>
    public string EnvironmentName { get; set; } = string.Empty;

    public List<DashboardQuickLinkDto> QuickLinks { get; set; } = [];

    /// <summary>依排序；只有看得到的。</summary>
    public List<DashboardWidgetDto> Widgets { get; set; } = [];
}

public class DashboardQuickLinkDto
{
    public string Url { get; set; } = string.Empty;

    /// <summary>classic Material Icons 名稱。</summary>
    public string Icon { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

/// <summary>一個看得到的小工具；<see cref="Id"/> 對應前端的元件。</summary>
public class DashboardWidgetDto
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>classic Material Icons 名稱。</summary>
    public string Icon { get; set; } = string.Empty;
}

/// <summary>「我的帳號」小工具（<c>GET api/v1/dashboard/account</c>）。時間是伺服器本地時間，不帶時差。</summary>
public class DashboardAccountDto
{
    /// <summary>上一次登入（不含這一次）；沒有紀錄時為 null。</summary>
    public DateTime? PreviousLoginLocal { get; set; }

    /// <summary>「Google 登入」或「帳號密碼」。</summary>
    public string? PreviousLoginMethod { get; set; }

    public string? PreviousLoginClientIp { get; set; }

    /// <summary>有本地密碼才顯示密碼到期與兩步驟驗證。</summary>
    public bool HasLocalPassword { get; set; }

    /// <summary>密碼到期時間；null＝不會到期。</summary>
    public DateTime? PasswordExpiresAtLocal { get; set; }

    /// <summary>7 天內到期（伺服器以目前時間判斷）。</summary>
    public bool PasswordExpiresSoon { get; set; }

    public bool TwoFactorEnabled { get; set; }
}

/// <summary>「我的通知」小工具（<c>GET api/v1/dashboard/notifications</c>）：未讀數與最新 5 則。</summary>
public class DashboardNotificationsDto
{
    public int UnreadCount { get; set; }

    public List<DashboardNotificationItemDto> Items { get; set; } = [];
}

public class DashboardNotificationItemDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>站內連結；伺服器已過濾成安全的相對網址，不安全時為 null。</summary>
    public string? Link { get; set; }

    /// <summary>伺服器本地時間，不帶時差。</summary>
    public DateTime CreatedAtLocal { get; set; }

    public bool IsRead { get; set; }
}

/// <summary>「排程作業」小工具（<c>GET api/v1/dashboard/scheduled-jobs</c>，只限管理員）：需要注意的作業。</summary>
public class DashboardScheduledJobsDto
{
    public bool SchedulingEnabled { get; set; }

    /// <summary>作業總數。</summary>
    public int JobCount { get; set; }

    public List<DashboardScheduledJobAttentionDto> Attention { get; set; } = [];
}

public class DashboardScheduledJobAttentionDto
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>上次執行失敗、上次執行被中斷、逾期未執行、已停用。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>是「已停用」（畫面用灰色標籤，其他用警示色）。</summary>
    public bool IsDisabled { get; set; }
}
