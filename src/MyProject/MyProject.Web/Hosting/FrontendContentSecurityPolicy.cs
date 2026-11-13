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
/// <item><c>connect-src</c>：同站台只有 <c>'self'</c>；雙站台時靜態站要加上 API 主機的網址（部署時填進 web.config）。
/// 0.10.13 起，只有 Development 會另外允許 localhost 的開發工具（<see cref="LocalDevToolSources"/>）。</item>
/// </list>
/// </summary>
public static partial class FrontendContentSecurityPolicy
{
    /// <summary>
    /// 開發工具的連線來源（0.10.13 起，只在 Development）：Visual Studio 的瀏覽器連結（Browser Link，SignalR 連 http://localhost:&lt;埠&gt;）
    /// 與熱重載（WebSocket 連 ws/wss://localhost:&lt;埠&gt;）。少了它們，開發者第一次啟動就在主控台看到 CSP 錯誤，熱重載也不會動。
    /// ⚠️ 正式環境與獨立靜態站（web.config）一律不加。
    /// </summary>
    public static readonly IReadOnlyList<string> LocalDevToolSources = ["http://localhost:*", "https://localhost:*", "ws://localhost:*", "wss://localhost:*"];

    public static IReadOnlyList<string> Directives(string? importMapHash, string? apiOrigin, bool allowLocalDevTools = false) =>
    [
        "default-src 'self'",
        "script-src 'self' 'wasm-unsafe-eval'" + (string.IsNullOrEmpty(importMapHash) ? string.Empty : $" 'sha256-{importMapHash}'"),
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
        "font-src 'self' https://fonts.gstatic.com",
        "img-src 'self' data:",
        "connect-src 'self'" + (string.IsNullOrEmpty(apiOrigin) ? string.Empty : " " + apiOrigin)
            + (allowLocalDevTools ? " " + string.Join(' ', LocalDevToolSources) : string.Empty),
        "frame-ancestors 'none'",
        "base-uri 'self'",
        "object-src 'none'",
    ];

    public static string Build(string? importMapHash, string? apiOrigin, bool allowLocalDevTools = false)
        => string.Join("; ", Directives(importMapHash, apiOrigin, allowLocalDevTools));

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
