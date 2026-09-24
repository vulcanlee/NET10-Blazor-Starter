namespace MyProject.Models.Systems;

/// <summary>
/// 一封要寄出的信（與寄送方式無關）。
///
/// <para><see cref="Kind"/> 是信件種類代碼（例如 <c>Test</c>、<c>PasswordReset</c>），
/// **日誌只能記它**，不能記收件者、主旨或內文 —— 收件者是個資，內文可能夾帶重設連結。</para>
/// </summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody, string Kind);

/// <summary>信件種類代碼。寫進日誌與稽核的只有這個值。</summary>
public static class EmailKinds
{
    /// <summary>管理員在系統健康監控頁寄出的測試信。</summary>
    public const string Test = "Test";
}
