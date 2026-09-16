using System.Globalization;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// Token 數量的顯示格式。
///
/// 大數字以 K／M 精簡呈現（15.67M、867.33K），否則統計頁上一排 8 位數會完全讀不出量級差異。
/// </summary>
public static class TokenUsageFormat
{
    private const long Million = 1_000_000;
    private const long Thousand = 1_000;

    /// <summary>精簡格式：≥1M 顯示 M、≥1K 顯示 K，其餘顯示原數字。</summary>
    public static string Compact(long value)
    {
        if (value >= Million)
        {
            return (value / (double)Million).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        }

        if (value >= Thousand)
        {
            return (value / (double)Thousand).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        }

        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 明細列的欄位格式。null 代表「這次 API 沒有回傳這個欄位」，
    /// 顯示破折號而不是 0 —— 兩者意義完全不同。
    /// </summary>
    public static string Cell(int? value)
        => value.HasValue ? Compact(value.Value) : "—";
}
