using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>
/// 登入失敗告警（0.9.166 起）：全系統在時間窗內的登入失敗次數（不分帳號、不分 IP，含不存在的帳號）達門檻時，
/// 通知全體管理員並寄信。同一帳號連續輸錯由 <see cref="LockoutSettings"/> 的鎖定處理，這裡抓的是橫向猜測。
/// 可在「系統參數」頁覆寫。
/// </summary>
public class LoginFailureAlertSettings
{
    public const string SectionName = "LoginFailureAlertSettings";

    /// <summary>時間窗內的登入失敗次數達到這個數就告警；0＝不告警。</summary>
    [Range(0, 10000)]
    public int Threshold { get; set; } = 20;

    /// <summary>統計最近幾分鐘。</summary>
    [Range(1, 1440)]
    public int WindowMinutes { get; set; } = 10;

    /// <summary>發出後多久內不再重發。</summary>
    [Range(1, 1440)]
    public int CooldownMinutes { get; set; } = 60;
}
