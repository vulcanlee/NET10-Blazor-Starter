using MyProject.Models.AdapterModel;

namespace MyProject.Web.Export;

/// <summary>系統例外紀錄的 CSV 匯出（0.10.5 起從 Server 版 <c>ExceptionLogView</c> 搬來，由 <c>ExceptionLogsController</c> 產生檔案）。</summary>
public static class ExceptionLogExport
{
    public static readonly IReadOnlyList<ExportColumn<ExceptionLogAdapterModel>> CsvColumns =
    [
        new("最後發生", x => x.LastOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("次數", x => x.OccurrenceCount.ToString()),
        new("例外類型", x => x.ExceptionType),
        new("訊息", x => x.Message),
        new("來源", x => x.Source),
        new("頁面", x => x.Page),
        new("操作", x => x.Operation),
        new("記錄器", x => x.LoggerName),
        new("使用者", x => x.Account),
        new("首次發生", x => x.FirstOccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("最後追蹤碼", x => x.LastTraceId),
    ];

    /// <summary>下載檔名（與 Server 版相同）。</summary>
    public static string FileName(DateTime now) => $"MyProject.Web-exceptions-{now:yyyyMMdd-HHmmss}.csv";
}
