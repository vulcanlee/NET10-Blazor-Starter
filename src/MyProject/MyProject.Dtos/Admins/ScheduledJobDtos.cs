namespace MyProject.Dtos.Admins;

/// <summary>
/// 排程作業頁的總覽（0.10.6 起，WASM 遷移 M7，<c>GET api/v1/scheduled-jobs</c>）。
/// 排程器是伺服器的單例狀態，API 只負責讀與觸發；時間是伺服器的本地時間，以不帶時差送出。
/// </summary>
public class ScheduledJobOverviewDto
{
    /// <summary>總開關（<c>ScheduledJobSettings:Enabled</c>）。</summary>
    public bool SchedulingEnabled { get; set; }

    /// <summary>執行中設定被改壞時的驗證訊息；正常為 null。</summary>
    public string? SettingsError { get; set; }

    public List<ScheduledJobItemDto> Items { get; set; } = [];
}

/// <summary>一個排程作業。</summary>
public class ScheduledJobItemDto
{
    /// <summary>穩定的識別名稱（用在網址、設定鍵與稽核）。</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Cron { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    /// <summary>下次執行（伺服器本地時間）；停用或總開關關閉時為 null。</summary>
    public DateTime? NextRunLocal { get; set; }

    /// <summary>最近一次執行（可能還在執行中）。</summary>
    public ScheduledJobRunDto? LastRun { get; set; }

    /// <summary>「立即執行」已在佇列或執行中（本行程）。</summary>
    public bool IsQueued { get; set; }
}

/// <summary>一筆執行紀錄（<c>GET api/v1/scheduled-jobs/{name}/runs</c> 與總覽的最近一次）。</summary>
public class ScheduledJobRunDto
{
    public int Id { get; set; }

    /// <summary>開始時間（伺服器本地時間）。</summary>
    public DateTime StartedAtLocal { get; set; }

    /// <summary><c>JobRunStatuses</c>：Running、Succeeded、Failed、Interrupted、Skipped。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>JobRunTriggers</c>：Schedule、CatchUp、Manual。</summary>
    public string Trigger { get; set; } = string.Empty;

    public string? TriggeredByAccount { get; set; }

    public long? DurationMs { get; set; }

    public string? Message { get; set; }

    public string? TraceId { get; set; }
}

/// <summary>啟用或停用一個作業（<c>PUT api/v1/scheduled-jobs/{name}/enabled</c>）。</summary>
public class ScheduledJobEnabledRequestDto
{
    public bool Enabled { get; set; }
}
