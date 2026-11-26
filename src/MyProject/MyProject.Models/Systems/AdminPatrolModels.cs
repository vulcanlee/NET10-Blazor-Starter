namespace MyProject.Models.Systems;

/// <summary>
/// 定時巡檢（0.9.161～0.10.38）的整體狀態，來自巡檢報告每段第一行的「狀態：正常／注意／異常」。
/// 舊報告也可能是「無法判讀」（報告格式判讀不出來），畫面以預設顏色顯示。0.10.39 起不再產生新的巡檢報告。
/// </summary>
public static class AdminPatrolStatuses
{
    public const string Normal = "正常";

    public const string Attention = "注意";

    public const string Abnormal = "異常";
}

/// <summary>巡檢報告清單的一列。</summary>
/// <param name="AttentionSections">需要注意的段落（固定的段落名稱，以「、」串接）。</param>
/// <param name="Trigger">觸發方式（<c>JobRunTriggers</c>）。</param>
/// <param name="TriggeredByAccount">手動執行的管理員帳號；排程與補跑為 null。</param>
public sealed record AdminPatrolReportSummary(
    Guid ReportId,
    DateTime CreatedAt,
    string Status,
    string AttentionSections,
    string Trigger,
    string? TriggeredByAccount,
    string? ModelName,
    bool IsTruncated);

/// <summary>一份巡檢報告：清單資料＋內容。內容檔不見時 <paramref name="Markdown"/> 為 null。</summary>
public sealed record AdminPatrolReportDetail(AdminPatrolReportSummary Summary, string? Markdown);
