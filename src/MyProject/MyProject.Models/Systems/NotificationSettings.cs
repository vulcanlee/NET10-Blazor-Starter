using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>站內通知（0.9.100 起）。可在「系統參數」頁覆寫。</summary>
public class NotificationSettings
{
    public const string SectionName = "NotificationSettings";

    /// <summary>通知保留天數（從建立時間起算，不論已讀未讀），超過的由排程作業「站內通知清理」刪除；0＝不自動清除。</summary>
    [Range(0, 3650)]
    public int RetentionDays { get; set; } = 90;

    /// <summary>已讀通知保留天數（0.9.125 起，從標為已讀的時間起算），超過的由同一個排程作業提早刪除；0＝不提早刪除。</summary>
    [Range(0, 3650)]
    public int ReadRetentionDays { get; set; } = 7;

    /// <summary>
    /// 通知推播串流（0.10.7 起，WASM 遷移 M8）：開啟時 WASM 外框以一條長連線即時收到「通知／公告有變動」；
    /// 關閉時只靠 60 秒輪詢（輪詢一直都在，是推播斷線時的備援）。不在系統參數頁；改 appsettings 後新的連線立即生效。
    /// </summary>
    public bool PushEnabled { get; set; } = true;
}
