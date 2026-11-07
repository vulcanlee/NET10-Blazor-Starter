using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

/// <summary>
/// 寄信佇列與寄信紀錄（0.9.167 起）：每封信一列。背景寄信器從記憶體交接區取出信後先寫進這裡，再依
/// <see cref="NextAttemptAtUtc"/> 寄出；暫時性失敗會重試。程式重新啟動後，「待寄」的信會接著寄。
/// ⚠️ 內文以 Data Protection 加密存在 <see cref="ProtectedBody"/>（重設密碼信含可用的重設連結），
/// 寄出成功或放棄後立刻清成 null；紀錄頁只看得到收件者、主旨與結果。
/// 依 <c>EmailDeliverySettings.RetentionDays</c> 自動清除（「待寄」不清）。
/// </summary>
public class EmailOutbox
{
    public int Id { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>信件種類代碼（<c>EmailKinds</c>，例如 <c>PasswordReset</c>）。</summary>
    [MaxLength(32)]
    public string Kind { get; set; } = string.Empty;

    [MaxLength(320)]
    public string To { get; set; } = string.Empty;

    [MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>加密後的內文（Html 與純文字）；寄出成功、失敗放棄或未寄出時為 null。</summary>
    public string? ProtectedBody { get; set; }

    /// <summary><see cref="EmailOutboxStatus"/> 的其中一個值。</summary>
    [MaxLength(16)]
    public string Status { get; set; } = EmailOutboxStatus.Pending;

    /// <summary>已經嘗試寄送幾次（含第一次）。</summary>
    public int Attempts { get; set; }

    /// <summary>「待寄」的下一次嘗試時間；其他狀態為 null。</summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    public DateTime? SentAtUtc { get; set; }

    /// <summary>最後一次失敗的例外型別與訊息（最多 500 字）。</summary>
    [MaxLength(500)]
    public string? LastError { get; set; }
}

/// <summary><see cref="EmailOutbox.Status"/> 的值（寫進資料庫，既有值不可更改）。</summary>
public static class EmailOutboxStatus
{
    /// <summary>待寄：還沒寄，或暫時性失敗後等待重試。</summary>
    public const string Pending = "Pending";

    /// <summary>已寄出。</summary>
    public const string Sent = "Sent";

    /// <summary>失敗：永久性失敗，或重試次數用完。</summary>
    public const string Failed = "Failed";

    /// <summary>未寄出：入列時寄信方式是 None（寄信停用）。</summary>
    public const string Skipped = "Skipped";
}
