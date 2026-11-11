namespace MyProject.Dtos.Admins;

/// <summary>
/// 系統管理助理的「巡檢報告」（0.10.7 起，WASM 遷移 M8；<c>GET api/v1/admin-assistant/patrol-reports</c>，只限管理員）。
/// 報告全體管理員共用；看報告不寫稽核（與 Server 版相同）。時間是伺服器本地時間，以不帶時差送出。
/// </summary>
public class AdminPatrolReportListDto
{
    /// <summary>報告保存天數。</summary>
    public int RetentionDays { get; set; }

    public List<AdminPatrolReportSummaryDto> Items { get; set; } = [];
}

public class AdminPatrolReportSummaryDto
{
    public Guid ReportId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>正常、注意、異常、無法判讀。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>需注意的段落（以頓號分隔）；沒有時為空字串。</summary>
    public string AttentionSections { get; set; } = string.Empty;

    /// <summary>觸發方式（Schedule、CatchUp、Manual）。</summary>
    public string Trigger { get; set; } = string.Empty;

    public string? TriggeredByAccount { get; set; }

    public string? ModelName { get; set; }

    public bool IsTruncated { get; set; }
}

/// <summary>一份報告：清單資料＋內文 Markdown（內容檔不見時為 null，畫面自己消毒成 HTML）。</summary>
public class AdminPatrolReportDetailDto
{
    public AdminPatrolReportSummaryDto Summary { get; set; } = new();

    public string? Markdown { get; set; }
}
