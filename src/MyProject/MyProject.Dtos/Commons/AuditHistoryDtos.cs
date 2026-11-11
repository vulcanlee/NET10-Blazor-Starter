namespace MyProject.Dtos.Commons;

/// <summary>
/// 某一筆資料的異動歷程中的一筆（0.10.2 起，<c>GET api/v1/audit-logs/history</c>，只限管理員）。
/// 時間為伺服器本地時間；詳細內容（修改前後差異）另以 <c>GET api/v1/audit-logs/{id}/content</c> 讀取。
/// </summary>
public class AuditHistoryItemDto
{
    public int Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public int? ActorUserId { get; set; }

    public string? ActorAccount { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    public string? Detail { get; set; }

    public bool Success { get; set; }

    public string? ClientIp { get; set; }

    /// <summary>有詳細內容檔（伺服器上的路徑不外流，只回有沒有）。</summary>
    public bool HasContent { get; set; }
}

/// <summary>一筆稽核的詳細內容（0.10.2 起）。</summary>
public class AuditContentDto
{
    public string Text { get; set; } = string.Empty;
}
