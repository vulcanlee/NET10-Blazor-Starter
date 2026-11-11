using System.Globalization;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Export;

/// <summary>Token 用量的 CSV 匯出與下載檔名（0.10.5 起從 Server 版 <c>TokenUsageView</c> 搬來，由 <c>TokenUsageController</c> 產生檔案）。</summary>
public static class TokenUsageExport
{
    public static readonly IReadOnlyList<ExportColumn<TokenUsageLogAdapterModel>> CsvColumns =
    [
        new("時間", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
        new("使用者", x => x.Account ?? "（系統自動）"),
        new("作業", x => x.Operation),
        new("型別", x => x.CallKind),
        new("供應商", x => x.Provider),
        new("模型", x => x.Model),
        new("輸入", x => x.InputCount?.ToString()),
        new("輸出", x => x.OutputCount?.ToString()),
        new("推理", x => x.ReasoningCount?.ToString()),
        new("快取", x => x.CachedInputCount?.ToString()),
        new("圖片輸入", x => x.ImageInputCount?.ToString()),
        new("圖片快取", x => x.ImageCachedInputCount?.ToString()),
        new("圖片輸出", x => x.ImageOutputCount?.ToString()),
        new("合計", x => x.TotalCount?.ToString()),
        new("費用USD", x => CsvCost(x.CostUsd, "F6")),
        new("費用TWD", x => CsvCost(x.CostTwd, "F4")),
        new("匯率", x => CsvCost(x.CostExchangeRate, "F4")),
        new("計價依據", x => x.CostPriceKey),
        new("長脈絡", x => x.IsUnpriced ? null : (x.CostLongContext ? "是" : "否")),
        new("字元數", x => x.CharacterCount?.ToString()),
        new("音訊時長秒", x => x.DurationSeconds?.ToString()),
        new("耗時ms", x => x.ElapsedMilliseconds.ToString()),
        new("結果", x => x.Success ? "成功" : x.IsCanceled ? "已取消" : "失敗"),
        new("失敗原因", x => x.FailureReason),
    ];

    /// <summary>CSV 下載檔名（與 Server 版相同）。</summary>
    public static string CsvFileName(DateTime now) => $"MyProject.Web-llm-usage-{now:yyyyMMdd-HHmmss}.csv";

    /// <summary>
    /// PDF 下載檔名（與 Server 版相同）：整份維持原本的檔名；單頁籤才加上頁籤代碼，免得一次匯好幾個頁籤時分不出誰是誰。
    /// </summary>
    public static string PdfFileName(string? tab, DateTime now)
        => $"MyProject.Web-llm-usage-{(string.IsNullOrEmpty(tab) ? string.Empty : $"{tab}-")}{now:yyyyMMdd-HHmmss}.pdf";

    /// <summary>
    /// CSV 的金額欄。
    ///
    /// ⚠️ 一律用 F 格式 + InvariantCulture，<b>不能用 N</b>：N 會插千分位逗號，
    /// Excel 會把 "1,234.5678" 當文字匯入，使用者做 SUM 得到 0。
    ///
    /// ⚠️ 未定價輸出空字串而不是 0，否則試算表加總會靜默少算。
    /// </summary>
    private static string? CsvCost(double? value, string format)
        => value?.ToString(format, CultureInfo.InvariantCulture);
}
