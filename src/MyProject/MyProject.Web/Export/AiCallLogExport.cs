using MyProject.Models.AdapterModel;
using MyProject.Web.Ai;

namespace MyProject.Web.Export;

/// <summary>AI 對話紀錄的 CSV 匯出（0.10.5 起從 Server 版 <c>AiCallLogView</c> 搬來，由 <c>AiCallLogsController</c> 產生檔案）。</summary>
public static class AiCallLogExport
{
    /// <summary>
    /// CSV 欄位。⚠️ 只有中繼資料、不含內文：CSV 常被帶出系統，內文請在明細視窗逐筆下載。
    /// </summary>
    public static readonly IReadOnlyList<ExportColumn<AiCallLogAdapterModel>> CsvColumns =
    [
        new("送出時間（本地）", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("結果", x => AiCallLogText.DescribeOutcome(x)),
        new("失敗原因", x => x.FailureReason),
        new("作業", x => x.Operation),
        new("帳號", x => x.Account),
        new("供應商", x => x.Provider),
        new("模型", x => x.Model),
        new("HTTP", x => x.HttpStatus?.ToString()),
        new("結束原因", x => x.FinishReason),
        new("耗時ms", x => x.ElapsedMilliseconds.ToString()),
        new("送出字元", x => x.RequestCharacters.ToString()),
        new("回應字元", x => x.ResponseCharacters.ToString()),
        new("關聯說明", x => x.RelatedInfo),
        new("呼叫識別碼", x => x.CallId.ToString()),
    ];

    /// <summary>下載檔名（與 Server 版相同）。</summary>
    public static string FileName(DateTime now) => $"MyProject.Web-ai-call-logs-{now:yyyyMMdd-HHmmss}.csv";
}
