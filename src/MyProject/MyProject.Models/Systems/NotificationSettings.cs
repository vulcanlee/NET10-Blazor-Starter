using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>站內通知（0.9.100 起）。可在「系統參數」頁覆寫。</summary>
public class NotificationSettings
{
    public const string SectionName = "NotificationSettings";

    /// <summary>通知保留天數（不論已讀未讀），超過的由排程作業「站內通知清理」刪除；0＝不自動清除。</summary>
    [Range(0, 3650)]
    public int RetentionDays { get; set; } = 90;
}
