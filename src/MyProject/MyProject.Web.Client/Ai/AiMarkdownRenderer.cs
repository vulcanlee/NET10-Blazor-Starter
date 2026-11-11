using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MyProject.Web.Ai;

/// <summary>
/// 把模型回傳的 Markdown 轉成可以安全交給 <c>MarkupString</c> 的 HTML。
///
/// <para>
/// ⚠️ 為什麼不能只靠 <c>DisableHtml()</c>：它做的是移除區塊 HTML 的解析器、並關掉行內
/// raw HTML 解析（兩者都會變成被轉義的純文字），但它<b>完全不管連結的 URL scheme</b>。
/// 也就是說 <c>[點我](javascript:...)</c> 會原封不動渲染成可點擊的連結。
/// </para>
/// <para>
/// 模型的輸出受日誌內容影響（有人把字串送進 log 就能間接影響輸出），屬於不可信輸入，
/// 所以還需要移除圖片與連結 scheme 白名單。這是 prompt injection 的第二、三道防線，
/// 第一道在伺服器的 <c>AiPromptGuardrails</c>。
/// </para>
/// <para>
/// 0.10.5 起放在 Client（命名空間不變）：WASM 頁面逐段顯示 AI 回覆時在瀏覽器消毒，伺服器的 PDF 也共用同一份規則。
/// </para>
/// </summary>
public static class AiMarkdownRenderer
{
    /// <summary>連結允許的 scheme。其餘（javascript、data、vbscript、file…）一律清掉。</summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>
    /// 刻意最小化的管線，請不要擴充：
    /// <list type="bullet">
    /// <item><c>DisableHtml()</c> 移掉區塊 HTML 解析器並關閉行內 raw HTML 解析。</item>
    /// <item><b>不可</b>加 <c>UseAdvancedExtensions()</c> 或 <c>UseGenericAttributes()</c>：
    /// 後者讓 <c>## 標題 {onclick="..."}</c> 這種語法直接注入 HTML 屬性，
    /// 等於把 XSS 從後門放回來。</item>
    /// <item><b>不可</b>加 <c>UseAutoLinks()</c>：會把日誌裡的裸 URL 變成可點連結。</item>
    /// </list>
    /// 由 MyProject.Tests 的 AiMarkdownRendererTests 守門。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .Build();

    /// <summary>Markdown 轉成已消毒的 HTML。空輸入回空字串。</summary>
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = ParseSanitized(markdown);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// 解析並消毒語法樹。伺服器的 <c>AiReportPdfBuilder</c> 共用這棵樹，
    /// 消毒規則才不會在畫面與 PDF 兩邊漂移。
    /// </summary>
    public static MarkdownDocument ParseSanitized(string? markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            // 圖片一律降級為純文字。模型輸出受日誌內容影響，外部圖片等於一個由攻擊者
            // 指定網址、且會被管理員瀏覽器自動觸發的請求（追蹤像素或內網探測）。
            if (link.IsImage)
            {
                link.IsImage = false;
                link.Url = string.Empty;
                continue;
            }

            if (IsAllowedUrl(link.Url) == false)
            {
                link.Url = string.Empty;
                continue;
            }

            OpenInNewTab(link);
        }

        // 角括號自動連結（<javascript:…>）是 CommonMark 核心語法，DisableHtml() 關不掉，也不是 LinkInline（0.10.5 獨立審查發現）。
        // 同一份白名單：不合格的降級成純文字（網址照樣看得到，只是不能點）。先 ToList，因為迴圈裡會換掉節點。
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            var url = autolink.IsEmail ? $"mailto:{autolink.Url}" : autolink.Url;
            if (IsAllowedUrl(url))
            {
                OpenInNewTab(autolink);
            }
            else
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }

        return document;
    }

    private static void OpenInNewTab(Inline link)
    {
        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer nofollow");
    }

    private static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return AllowedSchemes.Contains(absolute.Scheme, StringComparer.OrdinalIgnoreCase);
        }

        // 無法解析成絕對 URI 時：純相對路徑（#x、/x、./x）視為安全；
        // 含冒號代表帶了可疑 scheme（例如中間插了控制字元的 "java\tscript:"），擋掉。
        return url.Contains(':', StringComparison.Ordinal) == false;
    }
}
