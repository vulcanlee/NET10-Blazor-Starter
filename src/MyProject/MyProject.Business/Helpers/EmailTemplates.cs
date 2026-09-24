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
