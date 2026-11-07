namespace MyProject.Models.Systems;

/// <summary>
/// 定時巡檢（0.9.161 起）的整體狀態。前三個來自巡檢報告每段第一行的「狀態：正常／注意／異常」；
/// <see cref="Unreadable"/> 是報告格式判讀不出來（例如管理員改了提示詞）。
/// </summary>
public static class AdminPatrolStatuses
{
    public const string Normal = "正常";

    public const string Attention = "注意";

    public const string Abnormal = "異常";

    public const string Unreadable = "無法判讀";

    /// <summary>除了「正常」都要通知管理員：判讀不出來也通知，巡檢壞掉才不會沒人發現。</summary>
    public static bool NeedsAttention(string status) => status != Normal;
}

/// <summary>
/// 巡檢報告中會判斷狀態的段落（不含「總結」「建議處理」）。資料表只存這些固定名稱或 <see cref="Other"/>，
/// 不存模型寫的標題（速查表 §6.7：模型回應內文只進內容檔）。
/// </summary>
public static class AdminPatrolSections
{
    public static IReadOnlyList<string> Known { get; } = ["健康狀態", "例外與錯誤", "排程與備份", "資源用量", "帳號與安全"];

    public const string Other = "其他段落";
}

/// <summary>巡檢報告清單的一列。</summary>
/// <param name="AttentionSections">需要注意的段落（<see cref="AdminPatrolSections"/> 的名稱，以「、」串接）。</param>
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

/// <summary>排程作業要存的一份巡檢報告。</summary>
public sealed record AdminPatrolReportDraft(
    Guid ReportId,
    DateTime CreatedAt,
    string Status,
    string AttentionSections,
    string Trigger,
    string? TriggeredByAccount,
    int JobRunId,
    string? ModelName,
    bool IsTruncated,
    string Markdown);
