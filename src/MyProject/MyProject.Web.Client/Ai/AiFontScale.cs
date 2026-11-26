using System.Globalization;

namespace MyProject.Web.Ai;

/// <summary>
/// AI 視窗的「一般／中／大」字級（0.10.45 起，整併自日誌檢視、AI 例外分析、AI 對話紀錄明細三份相同的寫法）。
/// 百分比同時是 CSS 倍率 <c>--ai-font-scale</c> 的來源與「哪一顆被選中」的識別值；每次開窗都回到 <see cref="DefaultPercent"/>
/// （字級選擇刻意不跨工作階段保存）。
/// </summary>
public static class AiFontScale
{
    public const int DefaultPercent = 100;

    public static readonly IReadOnlyList<(int Percent, string Label)> Options =
    [
        (100, "一般"),
        (125, "中"),
        (150, "大"),
    ];

    /// <summary>
    /// 寫進 <c>--ai-font-scale</c> 的倍率（1／1.25／1.5）。
    /// ⚠️ 一定要用 <see cref="CultureInfo.InvariantCulture"/>：若當下文化把小數點寫成逗號，
    /// 產出的會是無效的 CSS 值 <c>1,25</c>，字級靜默失效 —— 開發機上永遠看不到。
    /// </summary>
    public static string ToCss(int percent) => (percent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
}
