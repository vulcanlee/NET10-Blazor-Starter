using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Admins;

/// <summary>
/// 系統健康監控頁（0.10.6 起，WASM 遷移 M7，<c>GET api/v1/system-health</c>）：健康報告與寄信測試區的設定。
/// ⚠️ 每次讀取都會執行全部檢查，AI 已設定時「LLM API」一項會真的呼叫模型（產生費用，記進 Token 用量）。
/// </summary>
public class SystemHealthReportDto
{
    /// <summary>檢查時間（伺服器本地時間，不帶時差）。</summary>
    public DateTime CheckedAtLocal { get; set; }

    public int Score { get; set; }

    /// <summary>Healthy、Degraded、Unhealthy。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Green、Yellow、Red。</summary>
    public string Light { get; set; } = string.Empty;

    public List<SystemHealthItemDto> Items { get; set; } = [];

    /// <summary>寄信功能是否啟用（Provider 不是 None）。</summary>
    public bool IsEmailEnabled { get; set; }

    /// <summary>畫面上的寄送方式文字，例如「Pickup」「None（未啟用）」。</summary>
    public string EmailProviderText { get; set; } = string.Empty;

    /// <summary>測試信收件者的預設值（目前使用者的 Email）。</summary>
    public string? DefaultTestEmailTo { get; set; }
}

/// <summary>一個檢查項目。</summary>
public class SystemHealthItemDto
{
    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int Weight { get; set; }

    /// <summary>Healthy、Degraded、Unhealthy。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Green、Yellow、Red。</summary>
    public string Light { get; set; } = string.Empty;

    public string Evidence { get; set; } = string.Empty;

    public string? FailureMessage { get; set; }
}

/// <summary>最近 24 小時 WARN 以上日誌（<c>GET api/v1/system-health/warn-logs</c>）；讀取失敗只影響這一區，訊息放在 <see cref="EmptyText"/>。</summary>
public class SystemHealthWarnLogsDto
{
    /// <summary>新到舊。</summary>
    public List<LogEntryDto> Entries { get; set; } = [];

    /// <summary>區間與筆數的摘要；讀取失敗時為空字串。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>沒有資料時顯示的文字（含讀取失敗的原因與錯誤追蹤碼）。</summary>
    public string EmptyText { get; set; } = string.Empty;

    public List<string> Warnings { get; set; } = [];
}

/// <summary>寄出測試信（<c>POST api/v1/system-health/test-email</c>）。</summary>
public class SystemHealthTestEmailRequestDto
{
    [MaxLength(320, ErrorMessage = "收件者 Email 不可超過 320 個字。")]
    public string? To { get; set; }
}

/// <summary>測試信、測試通知的結果：成功或失敗都以 200 回傳，畫面依 <see cref="Success"/> 顯示綠色或紅色的結果列（與 Server 版相同）。</summary>
public class SystemHealthTestResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}
