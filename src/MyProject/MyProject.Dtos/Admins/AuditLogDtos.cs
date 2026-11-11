using MyProject.Dtos.Commons;

namespace MyProject.Dtos.Admins;

/// <summary>
/// 稽核紀錄頁的查詢條件（0.10.4 起，<c>POST api/v1/audit-logs/search</c> 與 <c>export</c>）。
/// 時間是伺服器的本地時間（不帶時區），與畫面上的日期選擇器相同；<see cref="SearchRequestBaseDto.Keyword"/> 比對動作、操作者、目標、摘要與 IP。
/// 排序只支援 <c>OccurredAt</c>，未指定時依發生時間由新到舊。
/// </summary>
public class AuditLogSearchRequestDto : SearchRequestBaseDto
{
    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    /// <summary>動作代碼（完全相同）。</summary>
    public string? Action { get; set; }

    /// <summary>true 只看成功、false 只看失敗、null 不限。</summary>
    public bool? Success { get; set; }

    /// <summary>操作者帳號（部分符合）。</summary>
    public string? Account { get; set; }

    /// <summary>來源 IP（開頭相符）。</summary>
    public string? ClientIp { get; set; }

    /// <summary>目標類型（完全相同）。</summary>
    public string? TargetType { get; set; }

    /// <summary>目標識別（完全相同）。</summary>
    public string? TargetId { get; set; }

    /// <summary>true：使用者按下「查詢」，伺服器寫一筆搜尋稽核；換頁、排序、重新整理不帶。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>稽核紀錄頁的一列（0.10.4 起）。時間由伺服器換算好，瀏覽器不必知道伺服器的時區。</summary>
public class AuditLogItemDto : AuditHistoryItemDto
{
    /// <summary>發生時間（UTC）。</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>簽章欄的文字，例如「已簽章，異動鏈 #12」或「升級前的紀錄，未納入完整性驗證」。</summary>
    public string Signature { get; set; } = string.Empty;
}

/// <summary>明細窗（<c>GET api/v1/audit-logs/{id}</c>）：列的內容加上詳細內容檔的全文。</summary>
public class AuditLogDetailDto : AuditLogItemDto
{
    /// <summary>詳細內容（修改差異、查詢條件）；這筆沒有內容檔時為 null，內容檔不見或讀取失敗時是說明文字。</summary>
    public string? Content { get; set; }
}

/// <summary>稽核紀錄頁的選項（<c>GET api/v1/audit-logs/options</c>）。</summary>
public class AuditLogOptionsDto
{
    /// <summary>資料庫裡出現過的動作代碼。</summary>
    public List<string> Actions { get; set; } = [];

    /// <summary>資料庫裡出現過的目標類型。</summary>
    public List<string> TargetTypes { get; set; } = [];

    /// <summary>「清除超過保留天數」時，異動與安全紀錄的保留天數。</summary>
    public int PurgeDays { get; set; }

    /// <summary>「清除超過保留天數」時，瀏覽紀錄（換頁、搜尋、查看）的保留天數。</summary>
    public int BrowsingPurgeDays { get; set; }

    /// <summary>目前沒有記錄的範圍提示（例如沒有記錄修改前後差異）；都有記錄時為 null。</summary>
    public string? ScopeNotice { get; set; }
}

/// <summary>「清除超過保留天數的紀錄」的結果（<c>POST api/v1/audit-logs/purge</c>）。</summary>
public class AuditLogPurgeResultDto
{
    /// <summary>兩類都清除成功。</summary>
    public bool Succeeded { get; set; }

    /// <summary>有刪到資料（異動與安全紀錄那一段成功），畫面要重新載入。</summary>
    public bool Changed { get; set; }

    /// <summary>給使用者看的結果。</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>完整性驗證的結果（<c>POST api/v1/audit-logs/verify</c>）。</summary>
public class AuditIntegrityReportDto
{
    public bool HasIssues { get; set; }

    /// <summary>一行摘要，例如「驗證 120 筆，全部正確」。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>問題總數（<see cref="Issues"/> 最多列 50 個）。</summary>
    public int IssueCount { get; set; }

    public List<AuditIntegrityIssueDto> Issues { get; set; } = [];
}

public class AuditIntegrityIssueDto
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}
