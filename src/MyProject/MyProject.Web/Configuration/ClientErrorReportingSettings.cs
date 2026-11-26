using System.ComponentModel.DataAnnotations;

namespace MyProject.Web.Configuration;

/// <summary>
/// 瀏覽器端錯誤回報（<c>ClientErrorReporting</c> 區段，0.9.79 起，LOG-20）。
///
/// 只有登入後才送到伺服器（<c>api/v1/shell/client-reports</c> 要登入，匿名者無法灌資料）。
/// 每位使用者每分鐘最多記 <see cref="MaxPerCircuitPerMinute"/> 筆錯誤，超過的留在瀏覽器稍後再送；設定鍵名沿用 Blazor 電路時代的名稱。
/// </summary>
public class ClientErrorReportingSettings
{
    public const string SectionName = "ClientErrorReporting";

    public bool Enabled { get; set; } = true;

    [Range(1, 1000)]
    public int MaxPerCircuitPerMinute { get; set; } = 10;
}
