namespace MyProject.Models.Systems;

/// <summary>
/// 一封要寄出的信（與寄送方式無關）。
///
/// <para><see cref="Kind"/> 是信件種類代碼（例如 <c>Test</c>、<c>PasswordReset</c>），
/// **日誌只能記它**，不能記收件者、主旨、內文或附件檔名 —— 收件者是個資，內文可能夾帶重設連結。</para>
/// <para><see cref="Attachments"/>（0.10.40 起）：附件跟著內文一起加密存進寄信佇列，寄出或放棄後一起清除；
/// 總大小不可超過 <see cref="EmailAttachment.MaxTotalBytes"/>（由呼叫端檢查，超過就改寄連結）。</para>
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody, string Kind, IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>信件附件（0.10.40 起）。檔名只用英數字（部分郵件軟體會把中文檔名變亂碼）。</summary>
public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content)
{
    /// <summary>一封信全部附件的大小上限：8 MB（多數郵件伺服器的上限是 10～25 MB，Base64 編碼後約大 37%）。</summary>
    public const int MaxTotalBytes = 8 * 1024 * 1024;
}

/// <summary>信件種類代碼。寫進日誌與稽核的只有這個值。</summary>
public static class EmailKinds
{
    /// <summary>管理員在系統健康監控頁寄出的測試信。</summary>
    public const string Test = "Test";

    /// <summary>忘記密碼：含重設連結的信（0.9.60 起）。</summary>
    public const string PasswordReset = "PasswordReset";

    /// <summary>重設成功後通知帳號主人「密碼已變更」（0.9.60 起）。</summary>
    public const string PasswordChanged = "PasswordChanged";

    /// <summary>系統例外告警（0.9.78 起，LOG-12）。</summary>
    public const string ExceptionAlert = "ExceptionAlert";

    /// <summary>站內通知的同步信（0.9.100 起）。</summary>
    public const string Notification = "Notification";

    /// <summary>系統巡檢報告（0.10.40 起，附 PDF）。</summary>
    public const string SystemReport = "SystemReport";
}
