using System.Net.Sockets;
using System.Security.Cryptography;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MyProject.Web.Email;

/// <summary>
/// 寄送失敗要不要重試（0.9.167 起）。只重試「等一下可能就好」的失敗；帳密錯、收件者被拒這類重試永遠不會好，
/// 重試只會讓錯誤重複出現。分不出來的例外當作暫時性（最多重試 <c>EmailDeliverySettings.MaxRetries</c> 次）。
/// </summary>
public static class EmailFailureClassifier
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        // 郵件伺服器的回應碼：4xx 是「稍後再試」，5xx 是永久拒絕。
        SmtpCommandException smtp => (int)smtp.StatusCode is >= 400 and < 500,
        AuthenticationException => false,
        SslHandshakeException => false,
        ParseException => false,
        UnauthorizedAccessException => false,
        CryptographicException => false,
        SmtpProtocolException => true,
        SocketException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => true,
        _ => true,
    };
}
