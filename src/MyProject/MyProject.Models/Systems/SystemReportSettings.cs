using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>
/// 系統巡檢報告的設定（<c>SystemReportSettings</c> 區段，0.10.39 起；取代 <c>AdminPatrolSettings</c>）。
/// 全部可以在「系統參數」頁修改（群組「系統巡檢報告」與「資料保留」）。
///
/// 放在 Models：Business 層的報告與快照服務讀保留天數。以 <c>ValidateDataAnnotations</c> 在啟動時與系統參數頁存檔時驗證範圍。
/// 容量門檻之間<b>不做</b>跨欄位驗證（系統參數頁一次只改一個值，跨欄位驗證會讓人卡在中間狀態）；
/// 規則改用 <see cref="EffectiveOrangeFreePercent"/> 這類值：較寬的門檻小於較嚴的時，以較嚴的為準。
/// 日報／週報／月報的「要不要執行」是「排程作業」頁的啟用開關，不在這裡；產生時間（星期幾、幾號、幾點）在這裡。
/// </summary>
public class SystemReportSettings
{
    public const string SectionName = "SystemReportSettings";

    /// <summary>時間格式：24 小時制 HH:mm。</summary>
    public const string TimePattern = "^([01][0-9]|2[0-3]):[0-5][0-9]$";

    private const string TimeMessage = "請用 24 小時制 HH:mm，例如 07:30。";

    /// <summary>日報的產生時間（每天，24 小時制 HH:mm）。</summary>
    [Required]
    [RegularExpression(TimePattern, ErrorMessage = TimeMessage)]
    public string DailyTime { get; set; } = "07:30";

    /// <summary>週報在星期幾產生：1＝星期一 … 7＝星期日（0.10.41 起）。報告涵蓋上週一～週日。</summary>
    [Range(1, 7)]
    public int WeeklyDayOfWeek { get; set; } = 1;

    /// <summary>週報的產生時間（24 小時制 HH:mm，0.10.41 起）。</summary>
    [Required]
    [RegularExpression(TimePattern, ErrorMessage = TimeMessage)]
    public string WeeklyTime { get; set; } = "08:00";

    /// <summary>月報在每月幾號產生（1～28，0.10.41 起；不開放 29～31，每個月都有這一天）。報告涵蓋上個月。</summary>
    [Range(1, 28)]
    public int MonthlyDay { get; set; } = 1;

    /// <summary>月報的產生時間（24 小時制 HH:mm，0.10.41 起）。</summary>
    [Required]
    [RegularExpression(TimePattern, ErrorMessage = TimeMessage)]
    public string MonthlyTime { get; set; } = "08:30";

    /// <summary>是否請 AI 寫整體狀況、處理順序與下期預測；關閉時用固定句型（燈號與數字不受影響）。</summary>
    public bool AiNarrativeEnabled { get; set; } = true;

    /// <summary>紅燈：磁碟可用空間低於這個百分比。</summary>
    [Range(1, 50)]
    public int CapacityRedFreePercent { get; set; } = 5;

    /// <summary>紅燈：磁碟可用空間低於這麼多 GB（不論百分比）；0＝不檢查。</summary>
    [Range(0, 100_000)]
    public int CapacityRedFreeGb { get; set; } = 2;

    /// <summary>紅燈：依成長速度預估這麼多天內用完。</summary>
    [Range(1, 3650)]
    public int CapacityRedForecastDays { get; set; } = 7;

    /// <summary>橙燈：磁碟可用空間低於這個百分比。</summary>
    [Range(1, 90)]
    public int CapacityOrangeFreePercent { get; set; } = 15;

    /// <summary>橙燈：依成長速度預估這麼多天內用完。</summary>
    [Range(1, 3650)]
    public int CapacityOrangeForecastDays { get; set; } = 30;

    /// <summary>黃燈：依成長速度預估這麼多天內用完。</summary>
    [Range(1, 3650)]
    public int CapacityYellowForecastDays { get; set; } = 90;

    /// <summary>日報與手動產生的報告保留天數；舊版巡檢報告也用這個天數。</summary>
    [Range(1, 3650)]
    public int DailyRetentionDays { get; set; } = 90;

    /// <summary>週報保留天數（0.10.41 起）。</summary>
    [Range(7, 3650)]
    public int WeeklyRetentionDays { get; set; } = 365;

    /// <summary>月報保留天數（0.10.41 起；季度帳號清查附在月報裡，是資安稽核的留存，預設 3 年）。</summary>
    [Range(31, 3650)]
    public int MonthlyRetentionDays { get; set; } = 1095;

    /// <summary>容量快照保留天數（預估成長速度要用，太短會預估不準）。</summary>
    [Range(30, 3650)]
    public int CapacitySnapshotRetentionDays { get; set; } = 730;

    /// <summary>某一種報告的保留天數：週報、月報各自的天數，其他（日報、指定區間）用日報的天數。</summary>
    public int RetentionDays(string frequency) => frequency switch
    {
        SystemReportFrequencies.Weekly => WeeklyRetentionDays,
        SystemReportFrequencies.Monthly => MonthlyRetentionDays,
        _ => DailyRetentionDays,
    };

    /// <summary>實際使用的橙燈百分比：不會小於紅燈的百分比。</summary>
    public int EffectiveOrangeFreePercent => Math.Max(CapacityOrangeFreePercent, CapacityRedFreePercent);

    /// <summary>實際使用的橙燈天數：不會小於紅燈的天數。</summary>
    public int EffectiveOrangeForecastDays => Math.Max(CapacityOrangeForecastDays, CapacityRedForecastDays);

    /// <summary>實際使用的黃燈天數：不會小於橙燈的天數。</summary>
    public int EffectiveYellowForecastDays => Math.Max(CapacityYellowForecastDays, EffectiveOrangeForecastDays);

    /// <summary>把 HH:mm 拆成時與分；格式不對回 false（驗證已擋，這裡只是防呆）。</summary>
    public static bool TryParseTime(string? value, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        if (value is null || value.Length != 5 || value[2] != ':'
            || !int.TryParse(value.AsSpan(0, 2), out hour) || !int.TryParse(value.AsSpan(3, 2), out minute))
        {
            return false;
        }

        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }
}
