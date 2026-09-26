using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Layout;

namespace MyProject.Tests;

/// <summary>
/// 守護「實際 <c>@page</c> 路由 ↔ Datas/HelpTopics.json ↔ Datas/Help/*.md ↔ 六大段」四層一致性。
/// </summary>
/// <remarks>
/// 這一類落差全部是<b>靜默</b>的：建置成功、畫面不報錯，只有使用者按下說明鈕才看到空白或佔位內容。
/// 新增頁面卻忘了寫說明，沒有這組測試的話永遠不會有人發現。
/// </remarks>
public sealed class PageHelpCatalogTests
{
    /// <summary>
    /// <b>刻意沒有說明的路由</b>。新增頁面若不寫說明，必須在這裡明示並附理由 ——
    /// 逼出一次自覺的決定，而不是讓新頁面靜默地沒有說明入口。
    /// </summary>
    private static readonly Dictionary<string, string> RoutesWithoutHelp = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = "EmptyLayout 的轉場頁，沒有頂欄，放不了說明鈕",
        ["/Error"] = "EmptyLayout 的系統錯誤頁，沒有頂欄",
        ["/not-found"] = "404 頁，內容本身就是說明",
        ["/Auths/Login"] = "NoFooterLayout 登入頁，沒有頂欄",
        ["/Auths/Logout"] = "登出的轉導頁，停留時間趨近於零",
        ["/Auths/Pending"] = "NoFooterLayout 等待審核頁，沒有頂欄",
        ["/Auths/ForgotPassword"] = "NoFooterLayout 忘記密碼頁，沒有頂欄",
        ["/Auths/ResetPassword"] = "NoFooterLayout 重設密碼頁，沒有頂欄",
    };

    private static readonly string[] UnfinishedMarkers = ["TODO", "TBD", "待補", "（略）", "(略)"];

    [Fact]
    public void EveryPageRoute_IsEitherInCatalog_OrExplicitlyExcluded()
    {
        var catalog = LoadCatalog().Select(topic => NormalizeRouteKey(topic.Route)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unhandled = LoadPageRoutes()
            .Where(route => !catalog.Contains(NormalizeRouteKey(route)) && !RoutesWithoutHelp.ContainsKey(route))
            .ToList();

        Assert.True(
            unhandled.Count == 0,
            $"下列 @page 路由既未登記於 Datas/HelpTopics.json，也未列入 RoutesWithoutHelp（請二選一）：{string.Join('、', unhandled)}");
    }

    [Fact]
    public void EveryCatalogRoute_MatchesARealPageRoute_AndIsNotExcluded()
    {
        var actual = LoadPageRoutes().Select(NormalizeRouteKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = RoutesWithoutHelp.Keys.Select(NormalizeRouteKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dangling = LoadCatalog()
            .Where(topic => !actual.Contains(NormalizeRouteKey(topic.Route)) || excluded.Contains(NormalizeRouteKey(topic.Route)))
            .Select(topic => topic.Route)
            .ToList();

        Assert.True(
            dangling.Count == 0,
            $"HelpTopics.json 中下列路由沒有對應的 @page，或同時列在 RoutesWithoutHelp：{string.Join('、', dangling)}");
    }

    [Fact]
    public void Catalog_HasNoDuplicateRoutes_AndEveryEntryIsComplete()
    {
        var catalog = LoadCatalog();

        var duplicates = catalog
            .GroupBy(topic => NormalizeRouteKey(topic.Route), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        Assert.True(duplicates.Count == 0, $"下列路由在索引中重複登記：{string.Join('、', duplicates)}");

        var invalid = catalog
            .Where(topic => !topic.Route.StartsWith('/')
                            || string.IsNullOrWhiteSpace(topic.Title)
                            || string.IsNullOrWhiteSpace(topic.File))
            .Select(topic => $"{topic.Route}|{topic.Title}|{topic.File}")
            .ToList();
        Assert.True(invalid.Count == 0, $"下列條目的 route 未以 / 開頭、或 title／file 為空：{string.Join('、', invalid)}");
    }

    [Fact]
    public void EveryDeclaredFile_ExistsOnDisk_AndFollowsSlugRule()
    {
        var directory = LocateHelpDirectory();
        var failures = new List<string>();

        foreach (var topic in LoadCatalog())
        {
            var expected = PageHelpService.ToSlugFileName(topic.Route);
            if (!string.Equals(topic.File, expected, StringComparison.Ordinal))
            {
                failures.Add($"{topic.Route}：file 為 {topic.File}，依規則應為 {expected}");
            }

            if (!File.Exists(Path.Combine(directory, topic.File)))
            {
                failures.Add($"{topic.Route}：{topic.File} 不存在");
            }
        }

        Assert.True(failures.Count == 0, $"索引與說明檔不一致：\n{string.Join('\n', failures)}");
    }

    [Fact]
    public void EveryFileOnDisk_IsReferencedByCatalog()
    {
        var referenced = LoadCatalog().Select(topic => topic.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = Directory
            .EnumerateFiles(LocateHelpDirectory(), "*.md")
            .Select(path => Path.GetFileName(path))
            .Where(name => !referenced.Contains(name))
            .ToList();

        Assert.True(orphans.Count == 0, $"下列說明檔沒有被索引引用（孤兒檔，永遠不會顯示）：{string.Join('、', orphans)}");
    }

    [Fact]
    public void EveryHelpDocument_HasAllSixSections_InOrder_AndNoUnknownHeading()
    {
        var allowed = PageHelpMarkdownParser.RequiredHeadings.Concat(PageHelpMarkdownParser.OptionalHeadings).ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var (name, markdown) in LoadAllDocuments())
        {
            var headings = PageHelpMarkdownParser.ExtractSectionHeadings(markdown);

            var required = headings.Where(PageHelpMarkdownParser.RequiredHeadings.Contains).ToList();
            if (!required.SequenceEqual(PageHelpMarkdownParser.RequiredHeadings))
            {
                failures.Add($"{name}：必要章節缺漏、重複或順序錯誤，實際為 [{string.Join('、', required)}]");
            }

            var unknown = headings.Where(heading => !allowed.Contains(heading)).ToList();
            if (unknown.Count > 0)
            {
                failures.Add($"{name}：含非白名單的 H2 [{string.Join('、', unknown)}]（章節標題必須逐字相符；「一分鐘看懂這一頁」必須是 ###）");
            }
        }

        Assert.True(failures.Count == 0, $"說明檔章節不符規範：\n{string.Join('\n', failures)}");
    }

    [Fact]
    public void EverySection_HasSubstantiveContent()
    {
        const int minimumLength = 40;
        var failures = new List<string>();

        foreach (var (name, markdown) in LoadAllDocuments())
        {
            foreach (var section in PageHelpMarkdownParser.Parse(markdown).Sections)
            {
                var length = Regex.Replace(section.Markdown, @"\s", string.Empty).Length;
                if (length < minimumLength)
                {
                    failures.Add($"{name} / {section.Heading}：僅 {length} 字（至少 {minimumLength}）");
                }

                var hit = UnfinishedMarkers.FirstOrDefault(x => section.Markdown.Contains(x, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    failures.Add($"{name} / {section.Heading}：含未完成標記「{hit}」");
                }
            }
        }

        Assert.True(failures.Count == 0, $"下列章節內容不足或含未完成標記：\n{string.Join('\n', failures)}");
    }

    /// <summary>
    /// 前言只在選「全部」時渲染，是使用者點開說明後第一眼看到的內容，必須有實質的「一分鐘看懂這一頁」。
    /// 刻意只釘結構、不釘標籤字面；每項字數下限擋「- **誰會用到**：所有人。」這種敷衍。
    /// </summary>
    [Fact]
    public void EveryHelpDocument_HasSubstantiveQuickLookPreamble()
    {
        const int minimumBullets = 5;
        const int minimumBulletLength = 15;

        var quickLookHeading = $"### {PageHelpMarkdownParser.QuickLookHeading}";
        var bulletPattern = new Regex(@"^\s*[-*]\s+(?<text>.+?)\s*$");
        var failures = new List<string>();

        foreach (var (name, markdown) in LoadAllDocuments())
        {
            var preamble = PageHelpMarkdownParser.Parse(markdown).Preamble;
            var lines = preamble.Split('\n').Select(x => x.TrimEnd('\r')).ToList();

            var firstLine = lines.FirstOrDefault(x => x.Length > 0) ?? string.Empty;
            if (!firstLine.StartsWith("# ", StringComparison.Ordinal))
            {
                failures.Add($"{name}：第一行必須是 # 頁名");
            }

            var headingIndex = lines.FindIndex(x => x.TrimEnd() == quickLookHeading);
            if (headingIndex < 0)
            {
                failures.Add($"{name}：前言缺少「{quickLookHeading}」小標");
                continue;
            }

            var bullets = lines
                .Skip(headingIndex + 1)
                .Select(line => bulletPattern.Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["text"].Value)
                .ToList();

            if (bullets.Count < minimumBullets)
            {
                failures.Add($"{name}：「{PageHelpMarkdownParser.QuickLookHeading}」僅 {bullets.Count} 項（至少 {minimumBullets}）");
            }

            var thin = bullets.Where(text => Regex.Replace(text, @"\s", string.Empty).Length < minimumBulletLength).ToList();
            if (thin.Count > 0)
            {
                failures.Add($"{name}：下列項目過短（至少 {minimumBulletLength} 字）：{string.Join('、', thin)}");
            }

            var hit = UnfinishedMarkers.FirstOrDefault(x => preamble.Contains(x, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                failures.Add($"{name}：前言含未完成標記「{hit}」");
            }
        }

        Assert.True(failures.Count == 0, $"下列說明檔的前言不符規範：\n{string.Join('\n', failures)}");
    }

    /// <summary>
    /// 「六、相關頁面」的每一個清單項都必須可解析成卡片，且目標必須是<b>有說明的</b>真實頁面、不可指向自己。
    /// 解析不到的行永遠不會變成可點的卡片（UI 只渲染清單前的引言），等於作者的意圖靜默失效。
    /// </summary>
    [Fact]
    public void EveryRelatedPageLine_Parses_AndTargetsAnotherHelpedPage()
    {
        var catalog = LoadCatalog();
        var catalogRoutes = catalog.Select(topic => NormalizeRouteKey(topic.Route)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var (name, markdown) in LoadAllDocuments())
        {
            var document = PageHelpMarkdownParser.Parse(markdown);
            var section = document.Sections.FirstOrDefault(x => x.Heading == PageHelpMarkdownParser.RelatedPagesHeading);
            if (section is null)
            {
                continue;   // 缺章節由六大段的測試負責報
            }

            var bulletCount = Regex.Matches(section.Markdown, @"(?m)^\s*[-*]\s").Count;
            if (bulletCount == 0 || bulletCount != document.RelatedPages.Count)
            {
                failures.Add($"{name}：清單項 {bulletCount} 行、可解析 {document.RelatedPages.Count} 筆（至少一筆，格式須為 `- [標題](/route)：說明`）");
            }

            var ownRoute = catalog.Where(topic => topic.File == name).Select(topic => NormalizeRouteKey(topic.Route)).FirstOrDefault();

            foreach (var related in document.RelatedPages)
            {
                var key = NormalizeRouteKey(related.Route);
                if (!catalogRoutes.Contains(key))
                {
                    failures.Add($"{name}：相關頁面 {related.Route} 不是已登記說明的頁面");
                }

                if (key == ownRoute)
                {
                    failures.Add($"{name}：相關頁面 {related.Route} 指向自己");
                }
            }
        }

        Assert.True(failures.Count == 0, $"相關頁面區塊有下列問題：\n{string.Join('\n', failures)}");
    }

    /// <summary>
    /// UTF-8 含 BOM、無替代字元。必須放在 dotnet test 裡：scripts/Test-DocsEncoding.ps1 只掃 docs/，掃不到 src/ 下的說明檔。
    /// </summary>
    [Fact]
    public void EveryHelpDocument_IsUtf8WithBom_AndHasNoReplacementCharacter()
    {
        var failures = new List<string>();

        foreach (var path in Directory.EnumerateFiles(LocateHelpDirectory(), "*.md"))
        {
            var name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);

            if (!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
            {
                failures.Add($"{name}：缺少 UTF-8 BOM");
            }

            if (Encoding.UTF8.GetString(bytes).Contains('�'))
            {
                failures.Add($"{name}：含 Unicode 替代字元（原始編碼可能已損壞）");
            }
        }

        Assert.True(failures.Count == 0, $"下列說明檔的編碼不符規範：\n{string.Join('\n', failures)}");
    }

    /// <summary>寫了表格的說明檔，渲染後必須真的產生 &lt;table&gt;（少了 UsePipeTables() 會變成字面的 | 欄 |）。</summary>
    [Fact]
    public void EveryHelpDocumentWithATable_RendersAnHtmlTable()
    {
        var tableDelimiter = new Regex(@"^\|[-:\s|]+\|\s*$", RegexOptions.Multiline);

        var broken = LoadAllDocuments()
            .Where(doc => tableDelimiter.IsMatch(doc.Markdown))
            .Where(doc => !HelpMarkdownRenderer.ToHtml(doc.Markdown).Contains("<table", StringComparison.Ordinal))
            .Select(doc => doc.Name)
            .ToList();

        Assert.True(broken.Count == 0, $"下列說明檔寫了表格，但渲染後沒有 <table>：{string.Join('、', broken)}");
    }

    /// <summary>
    /// Web SDK 預設只複製 *.json，.md 不在其中。漏了 Content 規則不會編譯失敗，
    /// 只會在 publish 後讓每一頁的說明都變成佔位內容（dotnet run 讀專案目錄，抓不到）。
    /// </summary>
    [Fact]
    public void WebCsproj_DeclaresHelpMarkdownContentGlob()
    {
        var csproj = File.ReadAllText(LocateWebPath("MyProject.Web.csproj"));

        Assert.Contains(@"Datas\Help\**\*.md", csproj);
        Assert.Contains("CopyToPublishDirectory", csproj);
    }

    private static List<PageHelpTopicModel> LoadCatalog()
        => JsonSerializer.Deserialize<List<PageHelpTopicModel>>(
               File.ReadAllText(LocateWebPath(Path.Combine("Datas", "HelpTopics.json"))),
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? [];

    private static IEnumerable<(string Name, string Markdown)> LoadAllDocuments()
        => Directory
            .EnumerateFiles(LocateHelpDirectory(), "*.md")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

    private static List<string> LoadPageRoutes()
        => Directory
            .EnumerateFiles(LocateWebPath("Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"(?m)^@page\s+""([^""]+)"""))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeRouteKey(string? route) => PageHelpService.NormalizePath(route).ToLowerInvariant();

    private static string LocateHelpDirectory() => LocateWebPath(Path.Combine("Datas", "Help"));

    private static string LocateWebPath(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MyProject.Web", relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"找不到 MyProject.Web 底下的 {relativePath}（從 {AppContext.BaseDirectory} 往上找）。");
    }
}
