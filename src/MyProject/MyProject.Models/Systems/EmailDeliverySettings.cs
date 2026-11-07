using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.Systems;

/// <summary>寄信佇列的重試與寄信紀錄保留（0.9.167 起）。可在「系統參數」頁覆寫。</summary>
public class EmailDeliverySettings
{
    public const string SectionName = "EmailDeliverySettings";

    /// <summary>暫時性失敗（連線失敗、逾時、郵件伺服器回 4xx）重試幾次；間隔依序 1、5、15、30、60 分鐘。0＝不重試。</summary>
    [Range(0, 5)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>寄信紀錄保留天數（從建立時間起算），超過的由排程作業「寄信紀錄清理」刪除；「待寄」不刪。0＝不自動清除。</summary>
    [Range(0, 3650)]
    public int RetentionDays { get; set; } = 90;
}
