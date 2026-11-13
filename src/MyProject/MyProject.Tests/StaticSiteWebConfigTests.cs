using System.Xml.Linq;
using MyProject.Web.Hosting;

namespace MyProject.Tests;

/// <summary>
/// 前端靜態站台的 IIS 設定（0.10.10 起，WASM 遷移 M11；<c>MyProject.Web.Client/web.config</c>，發佈到輸出根目錄）。
/// 這些規則只有部署到 IIS 才會生效，本機（Kestrel、測試主機）完全測不出來，所以解析 XML 檢查：
/// SPA 回退、<c>_framework</c> 找不到時回 404、MIME、預先壓縮的 Brotli、快取、安全標頭與 CSP 要跟 API 主機的同站台模式一致。
/// </summary>
public sealed class StaticSiteWebConfigTests
{
    private static readonly XDocument Config = XDocument.Load(Path.Combine(SourceTree.Client, "web.config"));

    private static IReadOnlyList<XElement> InboundRules
        => Config.Descendants("rewrite").Elements("rules").Elements("rule").ToList();

    private static IReadOnlyList<XElement> OutboundRules
        => Config.Descendants("outboundRules").Elements("rule").ToList();

    [Fact]
    public void SpaFallback_ShouldServeIndexHtml_AfterRealFiles_AndNeverForMissingFrameworkFiles()
    {
        var rules = InboundRules;
        Assert.True(rules.Count >= 3, $"只找到 {rules.Count} 條 rewrite 規則。");
        Assert.All(rules, rule => Assert.Equal("true", rule.Attribute("stopProcessing")?.Value));

        var staticFile = IndexOf(rules, rule => Action(rule) is ("Rewrite", "wwwroot/{R:1}")
            && rule.Descendants("add").Any(x => x.Attribute("matchType")?.Value == "IsFile"));
        var frameworkNotFound = IndexOf(rules, rule => rule.Element("match")?.Attribute("url")?.Value == "^_framework/"
            && rule.Element("action")?.Attribute("type")?.Value == "CustomResponse"
            && rule.Element("action")?.Attribute("statusCode")?.Value == "404");
        var fallback = IndexOf(rules, rule => Action(rule) is ("Rewrite", "wwwroot/index.html")
            && rule.Element("match")?.Attribute("url")?.Value == ".*"
            && !rule.Descendants("add").Any());

        Assert.True(staticFile >= 0, "找不到「wwwroot 底下有這個檔案就直接送」的規則。");
        Assert.True(frameworkNotFound >= 0, "找不到「_framework 底下找不到的檔案回 404」的規則（否則瀏覽器會把 index.html 當成 .wasm 或 .js 解析）。");
        Assert.True(fallback >= 0, "找不到 SPA 回退到 wwwroot/index.html 的規則。");
        Assert.True(staticFile < frameworkNotFound && frameworkNotFound < fallback, "順序必須是：實體檔案 → _framework 404 → SPA 回退。");
    }

    [Fact]
    public void Brotli_ShouldBeServedOnlyWhenTheBrowserAcceptsIt_WithTheOriginalContentType()
    {
        var rules = InboundRules;
        var brotli = IndexOf(rules, rule => Action(rule).Url == "wwwroot/{R:1}.br");
        var staticFile = IndexOf(rules, rule => Action(rule).Url == "wwwroot/{R:1}");

        Assert.True(brotli >= 0 && brotli < staticFile, "預先壓縮的 .br 規則要排在一般實體檔案之前。");
        var conditions = rules[brotli].Descendants("add").ToList();
        Assert.Contains(conditions, x => x.Attribute("input")?.Value == "{HTTP_ACCEPT_ENCODING}");
        Assert.Contains(conditions, x => x.Attribute("matchType")?.Value == "IsFile" && x.Attribute("input")!.Value.EndsWith(".br", StringComparison.Ordinal));

        var rewrites = OutboundRules.Select(rule => (Variable: rule.Element("match")?.Attribute("serverVariable")?.Value, Value: rule.Element("action")?.Attribute("value")?.Value)).ToList();
        Assert.Contains(("RESPONSE_Content_Encoding", "br"), rewrites);
        Assert.Contains(("RESPONSE_Vary", "Accept-Encoding"), rewrites);
        Assert.Contains(("RESPONSE_Content_Type", "application/wasm"), rewrites);
        Assert.Contains(("RESPONSE_Content_Type", "text/javascript"), rewrites);
        Assert.Contains(("RESPONSE_Content_Type", "text/css"), rewrites);

        // IIS 自己的壓縮關掉：已經是 .br 的回應不可再被壓一次。
        Assert.Equal("false", Config.Descendants("urlCompression").Single().Attribute("doStaticCompression")?.Value);
    }

    [Theory]
    [InlineData(".wasm", "application/wasm")]
    [InlineData(".dat", "application/octet-stream")]
    [InlineData(".blat", "application/octet-stream")]
    [InlineData(".json", "application/json")]
    [InlineData(".woff2", "font/woff2")]
    public void MimeTypes_ShouldBeRegistered(string extension, string mimeType)
    {
        var staticContent = Config.Descendants("staticContent").Single();

        Assert.Contains(staticContent.Elements("remove"), x => x.Attribute("fileExtension")?.Value == extension);
        Assert.Contains(staticContent.Elements("mimeMap"), x => x.Attribute("fileExtension")?.Value == extension && x.Attribute("mimeType")?.Value == mimeType);
    }

    /// <summary>預設一律要重新驗證（index.html、appsettings.json 改了要立刻生效）；只有檔名帶指紋的 _framework 檔案可以長期快取。</summary>
    [Fact]
    public void Caching_ShouldRevalidateByDefault_AndOnlyCacheFingerprintedFilesForLong()
    {
        Assert.Equal("DisableCache", Config.Descendants("clientCache").Single().Attribute("cacheControlMode")?.Value);

        var longCache = Assert.Single(OutboundRules, rule => rule.Element("action")?.Attribute("value")?.Value?.Contains("immutable", StringComparison.Ordinal) == true);
        var preCondition = Config.Descendants("preCondition").Single(x => x.Attribute("name")?.Value == longCache.Attribute("preCondition")?.Value);
        var pattern = preCondition.Elements("add").Single(x => x.Attribute("input")?.Value == "{REQUEST_URI}");
        Assert.StartsWith("^/_framework/", pattern.Attribute("pattern")!.Value);
        Assert.Equal("false", pattern.Attribute("ignoreCase")?.Value);

        // 0.10.10 獨立審查：只有 200 才長期快取 —— 部署途中缺檔的 404 不能被快取一年。
        Assert.Equal("MatchAll", preCondition.Attribute("logicalGrouping")?.Value);
        Assert.Contains(preCondition.Elements("add"), x => x.Attribute("input")?.Value == "{RESPONSE_STATUS}" && x.Attribute("pattern")?.Value == "^200$");
    }

    [Fact]
    public void SecurityHeaders_ShouldMatchTheApiHost()
    {
        var headers = CustomHeaders();

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        // 0.10.10 獨立審查：Token 存在前端網址的 storage，前端也要 HSTS（與 API 主機的 UseHsts 相同）。
        Assert.StartsWith("max-age=", headers["Strict-Transport-Security"]);

        // Referrer-Policy 由 outbound 規則加（重設密碼頁要換成 no-referrer），兩條互斥。
        var referrer = OutboundRules.Where(rule => rule.Element("match")?.Attribute("serverVariable")?.Value == "RESPONSE_Referrer_Policy")
            .Select(rule => rule.Element("action")?.Attribute("value")?.Value)
            .ToList();
        Assert.Contains("strict-origin-when-cross-origin", referrer);
        Assert.Contains("no-referrer", referrer);
    }

    [Fact]
    public void ContentSecurityPolicy_ShouldMatchTheApiHost_AndCarryTheImportMapHashPlaceholder()
    {
        var csp = CustomHeaders()["Content-Security-Policy"];

        foreach (var directive in FrontendContentSecurityPolicy.Directives(importMapHash: StaticSiteWebConfigPlaceholders.ImportMapHash, apiOrigin: null))
        {
            Assert.Contains(directive, csp, StringComparison.Ordinal);
        }
    }

    /// <summary>網址帶 Token 的重設密碼頁：不讓 Token 經 Referer 外流、不讓瀏覽器與代理快取（API 主機同站台模式也一樣）。</summary>
    [Fact]
    public void ResetPasswordPage_ShouldNotLeakOrCacheItsToken()
    {
        var resetRules = OutboundRules
            .Where(rule => PreConditionPattern(rule) is { } pattern && pattern.Contains("Auths/ResetPassword", StringComparison.Ordinal) && !IsNegated(rule))
            .Select(rule => (Variable: rule.Element("match")?.Attribute("serverVariable")?.Value, Value: rule.Element("action")?.Attribute("value")?.Value))
            .ToList();

        Assert.Contains(("RESPONSE_Referrer_Policy", "no-referrer"), resetRules);
        Assert.Contains(("RESPONSE_Cache_Control", "no-store"), resetRules);
    }

    private static Dictionary<string, string> CustomHeaders()
        => Config.Descendants("customHeaders").Single().Elements("add")
            .ToDictionary(x => x.Attribute("name")!.Value, x => x.Attribute("value")!.Value);

    private static (string? Type, string? Url) Action(XElement rule)
        => (rule.Element("action")?.Attribute("type")?.Value, rule.Element("action")?.Attribute("url")?.Value);

    private static int IndexOf(IReadOnlyList<XElement> rules, Func<XElement, bool> predicate)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (predicate(rules[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static XElement? PreCondition(XElement rule)
        => Config.Descendants("preCondition").SingleOrDefault(x => x.Attribute("name")?.Value == rule.Attribute("preCondition")?.Value);

    private static string? PreConditionPattern(XElement rule)
        => PreCondition(rule)?.Elements("add").FirstOrDefault(x => x.Attribute("input")?.Value == "{REQUEST_URI}")?.Attribute("pattern")?.Value;

    private static bool IsNegated(XElement rule)
        => PreCondition(rule)?.Elements("add").Any(x => x.Attribute("negate")?.Value == "true") == true;
}

/// <summary>靜態站 web.config 裡、發佈時才填入的值。</summary>
internal static class StaticSiteWebConfigPlaceholders
{
    /// <summary>import map 的 SHA-256（發佈時由 MyProject.Web.Client.csproj 的 FillStaticSiteImportMapHash 填入）。</summary>
    public const string ImportMapHash = "__IMPORTMAP_SHA256__";
}
