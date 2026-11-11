using MyProject.Models.AdapterModel;

namespace MyProject.Web.Export;

/// <summary>稽核紀錄的 CSV 匯出（0.10.4 起從 Server 版 <c>AuditLogView</c> 搬來，由 <c>AuditLogsController</c> 產生檔案）。</summary>
public static class AuditLogExport
{
    /// <summary>CSV 欄位。標頭明講「本地」：稽核檔常被帶出系統比對，讀檔的人不會知道資料庫存的是 UTC。</summary>
    public static readonly IReadOnlyList<ExportColumn<AuditLogAdapterModel>> CsvColumns =
    [
        new("發生時間（本地）", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("結果", x => x.Success ? "成功" : "失敗"),
        new("動作", x => x.Action),
        new("操作者帳號", x => x.ActorAccount),
        new("操作者Id", x => x.ActorUserId?.ToString()),
        new("目標類型", x => x.TargetType),
        new("目標識別", x => x.TargetId),
        new("摘要", x => x.Detail),
        // 0.9.117 起：放在最後，不打亂既有依欄位位置讀檔的下游。
        new("來源IP", x => x.ClientIp),
    ];

    /// <summary>下載檔名（與 Server 版相同）。</summary>
    public static string FileName(DateTime now) => $"MyProject.Web-audit-{now:yyyyMMdd-HHmmss}.csv";
}
