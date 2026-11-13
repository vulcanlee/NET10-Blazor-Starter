using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MyProject.Web.Hosting;

/// <summary>
/// 前端（Blazor WebAssembly）頁面的 Content-Security-Policy（0.10.10 起，WASM 遷移 M11）。
/// 同站台時由這個主機加在前端的回應上；獨立的靜態站台由 <c>MyProject.Web.Client/web.config</c> 加，內容必須一致（StaticSiteWebConfigTests 守門）。
/// <list type="bullet">
/// <item><c>script-src 'wasm-unsafe-eval'</c>：WebAssembly 要編譯 .wasm；不需要 <c>'unsafe-eval'</c>。</item>
/// <item>import map 是 index.html 裡唯一的行內腳本，內容在建置時才產生（檔名指紋），所以帶它的 SHA-256；其他行內腳本一律不允許。</item>
/// <item><c>style-src 'unsafe-inline'</c>：AntDesign 會注入行內樣式；Material Icons 的樣式表來自 fonts.googleapis.com，字型來自 fonts.gstatic.com。</item>
/// <item><c>img-src data:</c>：兩步驟驗證的 QR Code 是 data URI。</item>
/// <item><c>connect-src</c>：同站台只有 <c>'self'</c>；雙站台時靜態站要加上 API 主機的網址（部署時填進 web.config）。</item>
/// </list>
/// </summary>
public static partial class FrontendContentSecurityPolicy
{
    public static IReadOnlyList<string> Directives(string? importMapHash, string? apiOrigin) =>
    [
        "default-src 'self'",
        "script-src 'self' 'wasm-unsafe-eval'" + (string.IsNullOrEmpty(importMapHash) ? string.Empty : $" 'sha256-{importMapHash}'"),
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
        "font-src 'self' https://fonts.gstatic.com",
        "img-src 'self' data:",
        "connect-src 'self'" + (string.IsNullOrEmpty(apiOrigin) ? string.Empty : " " + apiOrigin),
        "frame-ancestors 'none'",
        "base-uri 'self'",
        "object-src 'none'",
    ];

    public static string Build(string? importMapHash, string? apiOrigin) => string.Join("; ", Directives(importMapHash, apiOrigin));

    /// <summary>
    /// index.html 裡 import map 的 SHA-256（Base64）；沒有 import map 時回 null。
    /// ⚠️ 瀏覽器先把換行正規化成 LF 再算雜湊（HTML 剖析的前處理），所以這裡也一樣，否則 Windows 換行的檔案會對不上。
    /// </summary>
    public static string? ImportMapHash(string html)
    {
        var match = ImportMapPattern().Match(html);
        if (!match.Success)
        {
            return null;
        }

        var content = match.Groups["content"].Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    [GeneratedRegex("<script type=\"importmap\">(?<content>[\\s\\S]*?)</script>")]
    private static partial Regex ImportMapPattern();
}
