using MimeKit;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Email;

/// <summary>把 <see cref="EmailMessage"/> 轉成 MIME（HTML ＋ 純文字 multipart/alternative；0.10.40 起有附件時外層是 multipart/mixed）。</summary>
internal static class MimeMessageFactory
{
    /// <summary>Pickup 模式允許不填寄件者，此時用這個位址 —— 信不會真的寄出。</summary>
    internal const string PickupFallbackFromAddress = "no-reply@localhost";

    public static MimeMessage Create(EmailMessage message, EmailSettings settings, string systemName)
    {
        var fromName = string.IsNullOrWhiteSpace(settings.FromName) ? systemName : settings.FromName;
        var fromAddress = string.IsNullOrWhiteSpace(settings.FromAddress)
            ? PickupFallbackFromAddress
            : settings.FromAddress;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(fromName, fromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        var builder = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        };
        foreach (var attachment in message.Attachments ?? [])
        {
            builder.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = builder.ToMessageBody();

        return mime;
    }
}
