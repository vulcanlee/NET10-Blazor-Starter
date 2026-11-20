namespace MyProject.Web.SystemReports;

/// <summary>預估結果。</summary>
/// <param name="SampleDays">用了幾天的快照。</param>
/// <param name="DailyGrowthBytes">每天平均成長（位元組）；資料不夠時為 null。</param>
/// <param name="DaysToFull">還剩幾天用完；不會用完（沒有成長或在減少）或資料不夠時為 null。</param>
public sealed record CapacityForecastResult(int SampleDays, long? DailyGrowthBytes, int? DaysToFull)
{
    public bool HasEnoughData => DailyGrowthBytes is not null;
}

/// <summary>
/// 依每日容量快照預估成長（0.10.39 起，純函式）。
///
/// 用「已用空間」做最小平方法直線回歸，不用「可用空間」：磁碟擴充後總容量變大，可用空間會突然跳上去，
/// 已用空間不受影響，斜率才不會被擴充打亂。少於 <see cref="MinimumSampleDays"/> 天不預估（剛上線、資料不夠時避免亂報）。
/// </summary>
public static class CapacityForecast
{
    public const int MinimumSampleDays = 7;

    /// <summary>只用最近這麼多天（太舊的成長速度不代表現在）。</summary>
    public const int WindowDays = 90;

    /// <param name="samples">每天的已用位元組（日期不重複，順序不拘）。</param>
    /// <param name="freeBytes">現在的可用空間。</param>
    public static CapacityForecastResult Predict(IEnumerable<(DateTime Date, long UsedBytes)> samples, long freeBytes)
    {
        var points = samples
            .GroupBy(x => x.Date.Date)
            .Select(g => (Date: g.Key, Used: g.Last().UsedBytes))
            .OrderBy(x => x.Date)
            .ToList();
        if (points.Count > 0)
        {
            var from = points[^1].Date.AddDays(-WindowDays);
            points = points.Where(x => x.Date > from).ToList();
        }

        if (points.Count < MinimumSampleDays)
        {
            return new CapacityForecastResult(points.Count, null, null);
        }

        var origin = points[0].Date;
        var xs = points.Select(x => (x.Date - origin).TotalDays).ToList();
        var ys = points.Select(x => (double)x.Used).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        if (denominator <= 0)
        {
            return new CapacityForecastResult(points.Count, null, null);
        }

        var slope = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum() / denominator;
        var dailyGrowth = (long)Math.Round(slope);
        int? daysToFull = slope > 0 ? (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, freeBytes) / slope)) : null;
        return new CapacityForecastResult(points.Count, dailyGrowth, daysToFull);
    }
}
