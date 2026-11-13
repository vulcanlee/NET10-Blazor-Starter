using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.Extensions.DependencyInjection;
using MyProject.Web.Client;

namespace MyProject.Tests.Client;

/// <summary>
/// PWA 與離線（0.10.11 起，WASM 遷移 M12）的守門：Service Worker 只快取前端自己的靜態檔、伺服器端點一律只走網路、
/// 新版本要等使用者同意才換上、manifest 與色票及系統名稱一致、靜態站與專案設定都備妥。
/// Service Worker 在瀏覽器裡執行，這裡沒有 JS 引擎，所以解析原始碼檢查；實際行為由實機驗證。
/// 放進 <see cref="IntegrationHostCollection"/>：伺服器端點的檢查會啟動真的主機。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class PwaConventionTests
{
    private static readonly string PublishedServiceWorker = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "service-worker.published.js"));

    /// <summary>寫入快取的呼叫（Cache 的 put、add、addAll）。</summary>
    private static readonly Regex CacheWrite = new(@"\b(?:put|add|addAll)\(");

    /// <summary>快取名稱的前綴：offline-cache-、編碼過的範圍路徑、「:」（發佈版與開發版相同，開發版才刪得到發佈版留下的快取）。</summary>
    private const string CacheNamePrefixDeclaration = "const cacheNamePrefix = `offline-cache-${encodeURIComponent(new URL(self.registration.scope).pathname)}:`;";

    /// <summary>同站台時 API 主機一併提供前端，這些端點都在 Service Worker 的範圍裡：一律不攔截、不回 index.html。</summary>
    [Theory]
    [InlineData("/api/v1/Auth/me")]
    [InlineData("/API/v1/Auth/refresh")]
    [InlineData("/api/v1/downloads/abc")]
    [InlineData("/signin-google")]
    [InlineData("/Auths/Google/Login")]
    [InlineData("/Auths/Google/Callback")]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public void PublishedServiceWorker_ShouldLeaveServerEndpointsToTheNetwork(string path)
    {
        Assert.True(IsNetworkOnly(path), $"{path} 不在 Service Worker 的 networkOnlyPaths 裡：同站台時會被攔截（導覽時甚至回 index.html，Google 登入與下載就壞了）。");
    }

    /// <summary>前端的頁面（重設密碼頁除外，見下一條）不可被當成伺服器端點，否則離線時開不起來。</summary>
    [Fact]
    public void PublishedServiceWorker_ShouldStillServeTheFrontendPages()
    {
        var routes = ClientRouteTable.Routes.Where(route => !string.Equals(route, "Auths/ResetPassword", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(routes.Count > 30, $"只找到 {routes.Count} 個前端路由。");

        var wronglyExcluded = routes.Where(route => IsNetworkOnly("/" + route)).ToList();
        Assert.True(wronglyExcluded.Count == 0, "這些前端頁面被當成伺服器端點：" + string.Join("、", wronglyExcluded));
        Assert.False(IsNetworkOnly("/healthy"), "比對要以整段路徑為單位（/health 不可誤中 /healthy）。");
    }

    /// <summary>重設密碼頁的網址帶 Token：一律向伺服器拿，讓伺服器加的 no-referrer、no-store 生效（快取的 index.html 沒有這兩個標頭）。</summary>
    [Fact]
    public void PublishedServiceWorker_ShouldNotServeTheResetPasswordPageFromTheCache()
    {
        Assert.True(IsNetworkOnly("/Auths/ResetPassword"));
    }

    /// <summary>
    /// 主機實際註冊的每一個端點（同站台）：不在 <c>api/</c> 底下、也不是前端靜態檔（MapStaticAssets）或其回退的，都要在排除清單裡。
    /// 從 <see cref="EndpointDataSource"/> 列出，所以 Controller 的類別或方法層 <c>[Route]</c>、絕對路徑、<c>MapGet</c>、<c>MapHealthChecks</c> 都涵蓋得到。
    /// </summary>
    [Fact]
    public void PublishedServiceWorker_ShouldCoverEveryNonApiServerEndpoint()
    {
        using var factory = new ApiTestApplicationFactory();
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.Contains(endpoints, endpoint => endpoint.Metadata.GetMetadata<StaticAssetDescriptor>() is not null);

        var serverRoutes = endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<StaticAssetDescriptor>() is null)
            .Where(endpoint => !IsDevelopmentStaticAssetFallback(endpoint))
            // 測試專案自己加的探針 Controller 不是正式的端點。
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action
                || action.ControllerTypeInfo.Assembly == typeof(MyProject.Web.Program).Assembly)
            .Select(endpoint => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .Where(route => !route.Equals("/api", StringComparison.OrdinalIgnoreCase) && !route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        Assert.Contains("/health/live", serverRoutes);
        Assert.Contains(serverRoutes, route => route.StartsWith("/Auths/Google/", StringComparison.OrdinalIgnoreCase));
        var uncovered = serverRoutes.Where(route => !IsNetworkOnly(route)).ToList();
        Assert.True(uncovered.Count == 0, "這些伺服器端點不在 Service Worker 的 networkOnlyPaths 裡：" + string.Join("、", uncovered));
    }

    /// <summary>不是端點、由驗證中介軟體處理的網址（Google 登入的 OAuth 回呼 CallbackPath）也要在排除清單裡。</summary>
    [Fact]
    public void PublishedServiceWorker_ShouldCoverTheGoogleCallbackPath()
    {
        var program = File.ReadAllText(SourceTree.WebFile("Program.cs"));
        var mapped = Regex.Matches(program, "CallbackPath\\s*=\\s*\"(?<path>/[^\"]+)\"")
            .Select(match => match.Groups["path"].Value)
            .ToList();

        Assert.NotEmpty(mapped);
        Assert.All(mapped, path => Assert.True(IsNetworkOnly(path), $"{path} 不在 Service Worker 的 networkOnlyPaths 裡。"));
    }

    /// <summary>跨來源（雙站台的 API、Google 字型）、不是 GET 的請求一律不攔截；伺服器端點的判斷排在導覽回退（回 index.html）之前。</summary>
    [Fact]
    public void PublishedServiceWorker_ShouldOnlyHandleSameOriginGets_AndCheckServerEndpointsBeforeTheNavigationFallback()
    {
        var handler = FetchHandler();

        var method = handler.IndexOf("request.method !== 'GET'", StringComparison.Ordinal);
        var origin = handler.IndexOf("url.origin !== self.location.origin", StringComparison.Ordinal);
        var serverPath = handler.IndexOf("isNetworkOnly(url.pathname)", StringComparison.Ordinal);
        var navigate = handler.IndexOf("request.mode === 'navigate'", StringComparison.Ordinal);

        Assert.True(method >= 0, "fetch 處理沒有先排除不是 GET 的請求。");
        Assert.True(origin >= 0, "fetch 處理沒有排除跨來源的請求。");
        Assert.True(serverPath >= 0, "fetch 處理沒有排除伺服器端點。");
        Assert.True(navigate >= 0, "fetch 處理沒有導覽回退。");
        Assert.True(method < navigate && origin < navigate && serverPath < navigate, "排除條件必須在導覽回退（回 index.html）之前。");

        // 排除的請求直接 return（不呼叫 respondWith，由瀏覽器自己走網路）。
        var exclusions = handler[..navigate];
        Assert.Equal(3, Regex.Matches(exclusions, @"\)\s*\{\s*return;\s*\}").Count);
    }

    /// <summary>只在安裝時把清單上的檔案（帶完整性雜湊）放進快取；執行期間從不寫快取，所以不可能存到 API 回應、資料或 Token。</summary>
    [Fact]
    public void PublishedServiceWorker_ShouldOnlyCacheTheAssetManifestAtInstall()
    {
        var code = WithoutComments(PublishedServiceWorker);

        Assert.Contains("self.importScripts('./service-worker-assets.js')", code, StringComparison.Ordinal);
        Assert.Contains("integrity: asset.hash", code, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", code, StringComparison.OrdinalIgnoreCase);

        // 寫快取的呼叫（put、add、addAll）只能出現在 onInstall 裡。
        var install = Regex.Match(code, @"async function onInstall\(\)\s*\{[\s\S]*?\n\}");
        Assert.True(install.Success, "service-worker.published.js 找不到 onInstall。");
        Assert.Matches(CacheWrite, install.Value);
        var outside = code.Remove(install.Index, install.Length);
        Assert.True(!CacheWrite.IsMatch(outside), "onInstall 以外寫了快取：" + string.Join("、", CacheWrite.Matches(outside).Select(match => match.Value)));
    }

    /// <summary>
    /// 快取名稱帶這個 Service Worker 的範圍（路徑）與清單版本；啟用時只刪「自己範圍、但版本不同」的快取 ——
    /// 同一個來源的不同路徑部署兩套系統（https://host/a/ 與 https://host/b/）時，不會互相刪掉對方的離線快取。
    /// </summary>
    [Fact]
    public void PublishedServiceWorker_ShouldNameItsCacheByScopeAndVersion_AndDropOnlyItsOwnOldOnesOnActivate()
    {
        var code = WithoutComments(PublishedServiceWorker);

        Assert.Contains(CacheNamePrefixDeclaration, code, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"const cacheName = `\$\{cacheNamePrefix\}\$\{self\.assetsManifest\.version\}`"), code);
        Assert.Matches(new Regex(@"addEventListener\('activate'[\s\S]*caches\.keys\(\)[\s\S]*key\.startsWith\(cacheNamePrefix\) && key !== cacheName[\s\S]*caches\.delete"), code);
    }

    /// <summary>
    /// 快取名稱的規則（與 service-worker.published.js 的 cacheNamePrefix 相同：範圍路徑以 encodeURIComponent 編碼，後面接「:」再接版本）：
    /// 任兩個不同範圍，一個的前綴都不會是另一個快取名稱的開頭（根目錄 / 不會誤刪 /a/ 的快取）；名稱以 offline-cache- 開頭、以版本結尾（實機腳本靠這兩點認快取）。
    /// 清單版本是 Base64，可能含 /、+。
    /// </summary>
    [Fact]
    public void CacheNames_OfDifferentScopes_ShouldNeverMatchEachOthersPrefix()
    {
        string[] scopes = ["/", "/a/", "/ab/", "/a/b/", "/a:b/", "/系統/"];
        string[] versions = ["AbC+/9==", "a/b+c/d="];

        foreach (var scope in scopes)
        {
            foreach (var version in versions)
            {
                var name = CachePrefix(scope) + version;
                Assert.StartsWith("offline-cache-", name, StringComparison.Ordinal);
                Assert.EndsWith(version, name, StringComparison.Ordinal);
                foreach (var other in scopes.Where(other => other != scope))
                {
                    Assert.False(name.StartsWith(CachePrefix(other), StringComparison.Ordinal), $"範圍 {other} 的 Service Worker 會刪掉範圍 {scope} 的快取 {name}。");
                }
            }
        }

        // JS 的 encodeURIComponent 與 .NET 的 Uri.EscapeDataString 都會把「:」編碼，範圍裡不會出現分隔用的「:」。
        static string CachePrefix(string scopePath) => $"offline-cache-{Uri.EscapeDataString(scopePath)}:";
    }

    /// <summary>新版本不自動換上（使用者可能正在編輯）：只有收到畫面送來的 SKIP_WAITING 才 skipWaiting，也不搶下已開的分頁。</summary>
    [Fact]
    public void PublishedServiceWorker_ShouldWaitUntilTheUserAgrees()
    {
        var code = WithoutComments(PublishedServiceWorker);

        var skipWaiting = Regex.Matches(code, @"skipWaiting\(\)").Count;
        Assert.Equal(1, skipWaiting);
        Assert.Matches(new Regex(@"addEventListener\('message'[^\n]*\n?[^\n]*SKIP_WAITING'\)\s*\{\s*self\.skipWaiting\(\)"), code);
        Assert.DoesNotContain("clients.claim", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 開發用的 Service Worker 不快取任何東西（改了程式重新整理就看得到），也就不會出現新版本提示。
    /// 啟用時刪掉同一範圍發佈版留下的離線快取（前綴與發佈版相同），但不可寫入任何快取。
    /// </summary>
    [Fact]
    public void DevelopmentServiceWorker_ShouldNotCacheAnything()
    {
        var code = WithoutComments(File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "service-worker.js")));

        Assert.DoesNotMatch(CacheWrite, code);
        Assert.DoesNotContain("caches.open", code, StringComparison.Ordinal);
        Assert.DoesNotContain("caches.match", code, StringComparison.Ordinal);
        Assert.DoesNotContain("respondWith", code, StringComparison.Ordinal);
        Assert.DoesNotContain("importScripts", code, StringComparison.Ordinal);
        Assert.DoesNotContain("'fetch'", code, StringComparison.Ordinal);

        Assert.Contains(CacheNamePrefixDeclaration, code, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"addEventListener\('activate'[\s\S]*caches\.keys\(\)[\s\S]*key\.startsWith\(cacheNamePrefix\)[\s\S]*caches\.delete"), code);
    }

    /// <summary>發佈時以 service-worker.published.js 取代 service-worker.js，並產生資產清單。</summary>
    [Fact]
    public void ClientProject_ShouldGenerateTheServiceWorkerAssetsManifest()
    {
        var project = XDocument.Load(Path.Combine(SourceTree.Client, "MyProject.Web.Client.csproj"));

        Assert.Equal("service-worker-assets.js", project.Descendants("ServiceWorkerAssetsManifest").Single().Value);
        var worker = project.Descendants("ServiceWorker").Single();
        Assert.Equal(@"wwwroot\service-worker.js", worker.Attribute("Include")?.Value);
        Assert.Equal(@"wwwroot\service-worker.published.js", worker.Attribute("PublishedContent")?.Value);
    }

    /// <summary>
    /// 不產生 .br／.gz 壓縮副本的檔案（0.10.11 獨立審查）：Service Worker 兩個檔案（SDK 壓縮 service-worker.js 時還沒加上檔頭的版本註解，
    /// 資產清單也可能在靜態站發佈後被改寫；伺服器看到壓縮副本就會改送它，瀏覽器拿到的就不是這一版）、appsettings.json（部署後改 ApiBaseUrl）、
    /// manifest（web.config 的 Brotli 規則沒有它的類型）。
    /// 設定寫在前端的 FrontendCompression.props，前端與 API 主機都要匯入：同站台發佈時由 API 主機（根專案）壓縮前端的檔案，只看它自己的設定。
    /// 實際效果以兩種模式各 dotnet publish 一次確認（輸出沒有這些檔案的 .br／.gz，端點清單也沒有引用）。
    /// </summary>
    [Fact]
    public void FrontendCompressionExclusions_ShouldApplyToBothTheStaticSiteAndTheSameSiteHost()
    {
        var props = XDocument.Load(Path.Combine(SourceTree.Client, "FrontendCompression.props"));
        var excluded = props.Descendants("CompressionExcludePatterns").Single().Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("$(CompressionExcludePatterns)", excluded[0]);
        foreach (var file in new[] { "service-worker.js", "service-worker-assets.js", "appsettings*.json", "*.webmanifest" })
        {
            Assert.Contains(file, excluded);
            Assert.Contains("**/" + file, excluded);
        }

        foreach (var project in new[] { Path.Combine(SourceTree.Client, "MyProject.Web.Client.csproj"), SourceTree.WebFile("MyProject.Web.csproj") })
        {
            var imports = XDocument.Load(project).Descendants("Import").Select(x => x.Attribute("Project")?.Value).ToList();
            Assert.True(imports.Any(x => x?.EndsWith("FrontendCompression.props", StringComparison.Ordinal) == true), $"{Path.GetFileName(project)} 沒有匯入 FrontendCompression.props。");
            Assert.Empty(XDocument.Load(project).Descendants("CompressionExcludePatterns"));
        }

        // 不再需要發佈後刪壓縮副本。
        var client = XDocument.Load(Path.Combine(SourceTree.Client, "MyProject.Web.Client.csproj"));
        Assert.Empty(client.Descendants("Target").Single(x => x.Attribute("Name")?.Value == "FillStaticSiteImportMapHash").Elements("Delete"));
    }

    /// <summary>
    /// 新版安裝失敗（0.10.11 獨立審查）：例如某個檔案與資產清單的完整性雜湊不符，新版直接變成 redundant、使用者靜靜地留在舊版。
    /// pwa.js 偵測「從 installing 變成 redundant、從沒進入 installed 或 activated」，經瀏覽器錯誤回報送到伺服器日誌（登入後才送，之前先暫存）。
    /// </summary>
    [Fact]
    public void Pwa_ShouldReportAServiceWorkerThatFailedToInstall()
    {
        var pwa = WithoutComments(File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "pwa.js")));
        var watch = Regex.Match(pwa, @"function watchInstalling\(worker\)\s*\{[\s\S]*?\n    \}");
        Assert.True(watch.Success, "pwa.js 找不到 watchInstalling。");

        Assert.Matches(new Regex(@"worker\.state === 'installed' \|\| worker\.state === 'activated'\)\s*\{\s*installed = true;"), watch.Value);
        Assert.Matches(new Regex(@"worker\.state === 'redundant' && !installed\)\s*\{\s*reportInstallFailure\(worker\);"), watch.Value);

        var report = Regex.Match(pwa, @"function reportInstallFailure\(worker\)\s*\{[\s\S]*?\n    \}");
        Assert.True(report.Success, "pwa.js 找不到 reportInstallFailure。");
        Assert.Contains("window.appClientErrors.report('error', ", report.Value, StringComparison.Ordinal);
        Assert.Contains("worker.scriptURL", report.Value, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"'Service worker [^'\p{IsCJKUnifiedIdeographs}]+'"), report.Value);

        // 錯誤回報的 report 走跟 window error 相同的路（過濾、截斷、登入前暫存）。
        var reporter = WithoutComments(File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "client-error-reporter.js")));
        Assert.Matches(new Regex(@"report: \(kind, message, stack, source\) => capture\(kind, message, stack, source\)"), reporter);

        var html = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "index.html"));
        var reporterAt = html.IndexOf("<script src=\"js/client-error-reporter.js\">", StringComparison.Ordinal);
        Assert.True(reporterAt >= 0 && reporterAt < html.IndexOf("<script src=\"js/pwa.js\">", StringComparison.Ordinal),
            "client-error-reporter.js 必須排在 pwa.js 之前載入。");
    }

    /// <summary>入口頁要連到 manifest 與 iOS 的主畫面圖示，並載入註冊 Service Worker 的 pwa.js（不可用行內腳本，CSP 只允許 import map）。</summary>
    [Fact]
    public void IndexHtml_ShouldLinkTheManifest_AndRegisterTheServiceWorkerFromAFile()
    {
        var html = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "index.html"));

        Assert.Contains("<link rel=\"manifest\" href=\"manifest.webmanifest\" />", html, StringComparison.Ordinal);
        Assert.Matches(new Regex("<link rel=\"apple-touch-icon\" href=\"images/icon-192\\.png\" />"), html);
        Assert.Contains("<script src=\"js/pwa.js\"></script>", html, StringComparison.Ordinal);

        var inlineScripts = Regex.Matches(html, "<script(?![^>]*\\bsrc=)[^>]*>").Select(match => match.Value).ToList();
        Assert.Equal(["<script type=\"importmap\">"], inlineScripts);

        var pwa = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "pwa.js"));
        Assert.Contains("navigator.serviceWorker.register('service-worker.js'", pwa, StringComparison.Ordinal);
    }

    /// <summary>可以安裝為 PWA：名稱是畫面上的系統名稱、獨立視窗、顏色取自 theme.css 的色票、192 與 512 的圖示都在。</summary>
    [Fact]
    public void Manifest_ShouldMatchTheSystemNameAndThePalette()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "manifest.webmanifest")));
        var root = manifest.RootElement;

        using var settings = JsonDocument.Parse(File.ReadAllText(SourceTree.WebFile("appsettings.json")), ShippedAppSettings.JsonOptions);
        var systemName = settings.RootElement.GetProperty("SystemSettings").GetProperty("SystemInformation").GetProperty("SystemName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(systemName));
        Assert.Equal(systemName, root.GetProperty("name").GetString());
        Assert.Equal(systemName, root.GetProperty("short_name").GetString());

        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("./", root.GetProperty("start_url").GetString());
        Assert.Equal("./", root.GetProperty("scope").GetString());
        Assert.Equal("zh-TW", root.GetProperty("lang").GetString());

        var theme = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "theme.css"));
        var accent = Regex.Match(theme, @"--app-accent:\s*(?<hex>#[0-9a-fA-F]{6});").Groups["hex"].Value;
        var page = Regex.Match(theme, @"--app-page:\s*linear-gradient\([^,]+,\s*(?<hex>#[0-9a-fA-F]{6})").Groups["hex"].Value;
        Assert.False(string.IsNullOrEmpty(accent), "theme.css 找不到 --app-accent。");
        Assert.False(string.IsNullOrEmpty(page), "theme.css 找不到 --app-page 的第一個顏色。");
        Assert.Equal(accent, root.GetProperty("theme_color").GetString(), ignoreCase: true);
        Assert.Equal(page, root.GetProperty("background_color").GetString(), ignoreCase: true);

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        foreach (var size in new[] { 192, 512 })
        {
            var icon = Assert.Single(icons, x => x.GetProperty("sizes").GetString() == $"{size}x{size}");
            Assert.Equal("image/png", icon.GetProperty("type").GetString());
            var file = Path.Combine(SourceTree.ClientWebRoot, icon.GetProperty("src").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"找不到圖示 {file}。");
            Assert.Equal((size, size), PngSize(file));
        }
    }

    /// <summary>靜態站（IIS）要認得 .webmanifest（預設沒有這個 MIME，會回 404）。</summary>
    [Fact]
    public void StaticSiteWebConfig_ShouldServeTheManifestWithItsMimeType()
    {
        var staticContent = XDocument.Load(Path.Combine(SourceTree.Client, "web.config")).Descendants("staticContent").Single();

        Assert.Contains(staticContent.Elements("remove"), x => x.Attribute("fileExtension")?.Value == ".webmanifest");
        Assert.Contains(staticContent.Elements("mimeMap"), x => x.Attribute("fileExtension")?.Value == ".webmanifest" && x.Attribute("mimeType")?.Value == "application/manifest+json");
    }

    /// <summary>networkOnlyPaths 的比對規則（與 service-worker.published.js 的 isNetworkOnly 相同）：小寫、整段相符或其下的路徑。</summary>
    private static bool IsNetworkOnly(string path)
    {
        var lower = path.ToLowerInvariant();
        return NetworkOnlyPaths().Any(prefix => lower == prefix || lower.StartsWith(prefix + "/", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> NetworkOnlyPaths()
    {
        var code = WithoutComments(PublishedServiceWorker);
        var list = Regex.Match(code, @"const networkOnlyPaths = \[(?<items>[^\]]*)\];");
        Assert.True(list.Success, "service-worker.published.js 找不到 networkOnlyPaths。");

        // 比對的實作也要釘住：小寫、整段相符或其下的路徑。
        Assert.Matches(new Regex(@"function isNetworkOnly\(pathname\)\s*\{\s*const path = pathname\.toLowerCase\(\);\s*return networkOnlyPaths\.some\(prefix => path === prefix \|\| path\.startsWith\(prefix \+ '/'\)\);\s*\}"), code);

        var paths = Regex.Matches(list.Groups["items"].Value, "'(?<path>[^']*)'").Select(match => match.Groups["path"].Value).ToList();
        Assert.All(paths, path => Assert.Equal(path.ToLowerInvariant().TrimEnd('/'), path));
        return paths;
    }

    /// <summary>從原始碼執行時 MapStaticAssets 另加的萬用端點（建置後新增的前端檔案也取得到）。</summary>
    private static bool IsDevelopmentStaticAssetFallback(RouteEndpoint endpoint)
        => endpoint.RoutePattern.RawText == "{**path:file}";

    private static string FetchHandler()
    {
        var code = WithoutComments(PublishedServiceWorker);
        var start = code.IndexOf("self.addEventListener('fetch'", StringComparison.Ordinal);
        Assert.True(start >= 0, "service-worker.published.js 沒有 fetch 處理。");
        var end = code.IndexOf("\n});", start, StringComparison.Ordinal);
        return code[start..end];
    }

    /// <summary>去掉 // 與 /* */ 註解（註解裡的字不算程式）。</summary>
    private static string WithoutComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*[\s\S]*?\*/", string.Empty), @"(?m)^\s*//.*$|\s//\s.*$", string.Empty);

    private static (int Width, int Height) PngSize(string file)
    {
        var header = new byte[24];
        using (var stream = File.OpenRead(file))
        {
            stream.ReadExactly(header);
        }

        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
    }
}
