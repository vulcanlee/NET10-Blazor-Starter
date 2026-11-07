using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MyProject.Web.Email;

/// <summary>
/// 寄信佇列內文的加密（0.9.167 起）。重設密碼信含可用的重設連結，而資料庫只存權杖雜湊：
/// 內文不加密存進 <c>EmailOutbox</c>，等於把重設連結存進資料庫。內文寄出或放棄後就清除，加密只保護待寄的那段時間。
/// ⚠️ 系統備份同時包含資料庫與金鑰環 —— 備份檔外流仍等於待寄的內文外流（同兩步驟驗證密鑰，見備份與還原操作手冊）。
/// </summary>
public sealed class EmailBodyProtector
{
    private readonly IDataProtector protector;

    public EmailBodyProtector(IDataProtectionProvider dataProtectionProvider)
    {
        protector = dataProtectionProvider.CreateProtector("MyProject.EmailOutbox.v1");
    }

    public string Protect(string htmlBody, string textBody)
        => protector.Protect(JsonSerializer.Serialize(new Body(htmlBody, textBody)));

    /// <exception cref="CryptographicException">金鑰環換過或遺失，無法解密（呼叫端當作永久失敗）。</exception>
    public (string HtmlBody, string TextBody) Unprotect(string protectedBody)
    {
        var body = JsonSerializer.Deserialize<Body>(protector.Unprotect(protectedBody))
            ?? throw new CryptographicException("Email body is empty after decryption.");
        return (body.Html, body.Text);
    }

    private sealed record Body(string Html, string Text);
}
