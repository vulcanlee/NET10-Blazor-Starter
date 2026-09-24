using System.Net;
using MyProject.Models.Systems;

namespace MyProject.Business.Helpers;

/// <summary>
/// 系統寄出的每一種信的內容（HTML ＋ 純文字）。
///
/// <para>⚠️ 放進 HTML 的每一個變數都必須經過 <see cref="WebUtility.HtmlEncode(string)"/>：
/// 帳號、系統名稱都可能含有 <c>&lt;</c>、<c>&amp;</c>，不編碼就是信件內的 HTML 注入。</para>
///
/// <para>模板寫在程式裡（而不是外部 .html 檔）：可以單元測試、不怕部署漏檔，
/// 也不需要一套佔位符檢查機制。要改文案就改這裡。</para>
/// </summary>
public static class EmailTemplates
{
    public static EmailMessage BuildTest(string to, string systemName, DateTime sentAtLocal, string provider)
    {
        var sentAt = sentAtLocal.ToString("yyyy/MM/dd HH:mm:ss");
        var subject = $"[{systemName}] 測試信";

        var html = WrapHtml(
            systemName,
            "這是一封測試信",
            $"<p>如果您收到這封信，代表「{Encode(systemName)}」的寄信設定可以正常運作。</p>"
            + $"<p style=\"color:#666666;font-size:13px;\">寄送時間：{Encode(sentAt)}<br />寄送方式：{Encode(provider)}</p>");

        var text = $"如果您收到這封信，代表「{systemName}」的寄信設定可以正常運作。\n\n"
            + $"寄送時間：{sentAt}\n寄送方式：{provider}\n";

        return new EmailMessage(to, subject, html, text, EmailKinds.Test);
    }

    /// <summary>
    /// 忘記密碼的重設信。<paramref name="link"/> 內含 token，**只能出現在信裡**，不可寫進日誌或稽核。
    /// </summary>
    public static EmailMessage BuildPasswordReset(string to, string systemName, string account, string link, int lifetimeMinutes)
    {
        var subject = $"[{systemName}] 重設密碼";

        var html = WrapHtml(
            systemName,
            "重設您的密碼",
            $"<p>我們收到帳號「{Encode(account)}」的重設密碼申請。請在 <strong>{lifetimeMinutes} 分鐘內</strong>點下方按鈕設定新密碼：</p>"
            + $"<p style=\"margin:24px 0;\"><a href=\"{Encode(link)}\" style=\"display:inline-block;padding:10px 20px;background:#555555;color:#ffffff;text-decoration:none;border-radius:6px;\">設定新密碼</a></p>"
            + $"<p style=\"color:#666666;font-size:13px;\">按鈕無法點選時，請將下列網址貼到瀏覽器：<br />{Encode(link)}</p>"
            + "<p style=\"color:#666666;font-size:13px;\">連結只能使用一次。如果不是您本人申請，請忽略這封信，您的密碼不會改變。</p>");

        var text = $"我們收到帳號「{account}」的重設密碼申請。\n\n"
            + $"請在 {lifetimeMinutes} 分鐘內開啟下列網址設定新密碼（連結只能使用一次）：\n{link}\n\n"
            + "如果不是您本人申請，請忽略這封信，您的密碼不會改變。\n";

        return new EmailMessage(to, subject, html, text, EmailKinds.PasswordReset);
    }

    /// <summary>重設成功後的通知信：若不是本人操作，帳號主人才有機會察覺。</summary>
    public static EmailMessage BuildPasswordChanged(string to, string systemName, string account, DateTime changedAtLocal)
    {
        var changedAt = changedAtLocal.ToString("yyyy/MM/dd HH:mm:ss");
        var subject = $"[{systemName}] 您的密碼已變更";

        var html = WrapHtml(
            systemName,
            "您的密碼已變更",
            $"<p>帳號「{Encode(account)}」的密碼已於 {Encode(changedAt)} 透過「忘記密碼」重新設定。</p>"
            + "<p>如果這不是您本人的操作，請立即聯絡系統管理員。</p>");

        var text = $"帳號「{account}」的密碼已於 {changedAt} 透過「忘記密碼」重新設定。\n\n"
            + "如果這不是您本人的操作，請立即聯絡系統管理員。\n";

        return new EmailMessage(to, subject, html, text, EmailKinds.PasswordChanged);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string WrapHtml(string systemName, string heading, string bodyHtml)
    {
        return "<!DOCTYPE html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\" /></head>"
            + "<body style=\"margin:0;padding:24px;background:#f5f5f5;font-family:'Microsoft JhengHei','PingFang TC',sans-serif;color:#333333;\">"
            + "<div style=\"max-width:560px;margin:0 auto;padding:24px;background:#ffffff;border:1px solid #e5e5e5;border-radius:8px;\">"
            + $"<p style=\"margin:0 0 8px;color:#888888;font-size:13px;\">{Encode(systemName)}</p>"
            + $"<h1 style=\"margin:0 0 16px;font-size:20px;\">{Encode(heading)}</h1>"
            + bodyHtml
            + "<p style=\"margin:24px 0 0;color:#999999;font-size:12px;\">此信由系統自動寄出，請勿直接回覆。</p>"
            + "</div></body></html>";
    }
}
