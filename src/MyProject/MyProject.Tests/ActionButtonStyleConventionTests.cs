using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 操作按鈕與工具列按鈕的外觀（0.10.27 起）。
///
/// CrudActionButton 與 ToolbarIconButton 的 .razor.css 一直寫著「28×28、透明無框」與「36×36」，
/// 實際上從來沒有生效：Blazor 的 CSS 隔離只替「這個元件自己輸出的 HTML 元素」加上 scope 屬性，
/// 傳 Class 給別的元件（AntDesign 的 &lt;Button&gt;）時，那個元件輸出的 &lt;button&gt; 沒有這個屬性，
/// 規則就套不到。畫面上每顆按鈕都是 AntDesign 預設的 56×32 加框線，操作欄的圖示之間距離很大。
/// 外觀改寫在 theme.css（全站），規則見 docs/architecture/介面視覺設計規範.md §4.3。
/// </summary>
public sealed class ActionButtonStyleConventionTests
{
    // 元件標籤（大寫開頭）上的 Class 屬性。Blazor 比對元件參數不分大小寫，class= 一樣是傳給元件，所以屬性名稱不分大小寫；
    // 標籤名稱仍要大寫開頭（小寫的 <a class> 是自己輸出的元素）。屬性值裡的引號內容整段跳過；@(…) 運算式裡可以有引號。
    private static readonly Regex ComponentClass = new(
        @"<(?<tag>[A-Z]\w*)\b(?:""[^""]*""|[^>""])*?\b(?i:class)=""(?<value>@\((?:[^()]|\([^()]*\))*\)|[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex StringLiteral = new(@"""(?<text>[^""\r\n]*)""", RegexOptions.Compiled);

    private static readonly Regex CssComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CssRule = new(@"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// 0.10.27 以前就存在、從來沒生效的規則（檔名|class）：搜尋框、篩選下拉與日期區間是 AntDesign 的 Input／Select／RangePicker，
    /// 這些 .razor.css 寫的寬度套不到，畫面上是 AntDesign 自己的寬度。0.10.26 的手機寬度修正已經改用工具列 ::deep 控制寬度，
    /// 這裡的規則要不要改成 ::deep（會改變現在的寬度）留給之後決定，所以先列為已知，不要再新增。
    /// 把其中一條改成有效（::deep）或刪掉時，請一起從這個清單拿掉。
    /// </summary>
    private static readonly HashSet<string> KnownIneffective = new(StringComparer.Ordinal)
    {
        "AuditLogView.razor.css|.audit-log-target-id-input",
        "AuditLogView.razor.css|.audit-log-account-input",
        "AuditLogView.razor.css|.audit-log-ip-input",
        "AuditLogView.razor.css|.audit-log-search-input",
        "EmailLogView.razor.css|.email-log-search-input",
        "ExceptionLogView.razor.css|.exception-log-account-input",
        "ExceptionLogView.razor.css|.exception-log-search-input",
        "TokenUsageView.razor.css|.token-usage-account-input",
        "MyUserView.razor.css|.my-user-search-input",
        "RoleViewView.razor.css|.role-view-search-input",
        "CategoryViewView.razor.css|.category-view-search-input",
        "ProjectViewView.razor.css|.project-view-search-input",
        "TeamViewView.razor.css|.team-view-search-input",
    };

    /// <summary>
    /// 傳給別的元件的 class，在自己的 .razor.css 裡只能出現在 <c>::deep</c> 後面
    /// （例如 <c>.wrap ::deep .x</c>）；寫成 <c>.x</c> 或 <c>.x ::deep …</c> 都套不到，只會讓人以為有效。
    /// Class 是運算式（<c>Class="@CssClass"</c>）時，改看屬性值本身、<c>@code</c> 區塊與 code-behind 裡字串常值中的 class 名稱。
    /// </summary>
    [Fact]
    public void ScopedCss_ShouldNotStyleClassesPassedToOtherComponents()
    {
        var problems = new List<string>();
        var stillIneffective = new HashSet<string>(StringComparer.Ordinal);
        var checkedCount = 0;
        foreach (var razor in SourceTree.UiFiles("*.razor"))
        {
            var scopedCss = razor + ".css";
            if (!File.Exists(scopedCss))
            {
                continue;
            }

            var source = RazorComment.Replace(File.ReadAllText(razor), string.Empty);
            var codeBehind = File.Exists(razor + ".cs") ? File.ReadAllText(razor + ".cs") : string.Empty;
            var passedClasses = PassedClasses(source, codeBehind);
            if (passedClasses.Count == 0)
            {
                continue;
            }

            checkedCount++;
            var css = CssComment.Replace(File.ReadAllText(scopedCss), string.Empty);
            foreach (Match rule in CssRule.Matches(css))
            {
                foreach (var selector in rule.Groups["selectors"].Value.Split(',').Select(s => s.Trim()))
                {
                    var hit = passedClasses.FirstOrDefault(name => Targets(selector, name));
                    if (hit == null)
                    {
                        continue;
                    }

                    var key = $"{Path.GetFileName(scopedCss)}|.{hit}";
                    if (KnownIneffective.Contains(key))
                    {
                        stillIneffective.Add(key);
                        continue;
                    }

                    problems.Add($"{Path.GetFileName(scopedCss)}：{selector}（.{hit} 是傳給別的元件的 class）");
                }
            }
        }

        Assert.True(checkedCount > 0, "一個傳 Class 給別的元件、又有 .razor.css 的元件都沒找到，掃描路徑可能失效了。");
        Assert.True(problems.Count == 0,
            "這些規則套不到（CSS 隔離的 scope 屬性只加在自己輸出的元素上）：\n" + string.Join("\n", problems)
            + "\n請改寫在 theme.css（全站），或從自己輸出的外層元素用 ::deep 往下套。");

        var stale = KnownIneffective.Except(stillIneffective).ToList();
        Assert.True(stale.Count == 0,
            "這些已知的無效規則已經不在了，請從 KnownIneffective 清單拿掉（清單不可以放著爛掉）：" + string.Join("、", stale));
    }

    /// <summary>
    /// 操作按鈕與工具列按鈕的外觀寫在 theme.css：28×28、框線透明（比照 KnowledgeExtraction.AI 的操作欄）。
    /// 選擇器要把 class 重複兩次：只寫一次的話，滑鼠移上與點過之後的狀態會輸給 AntDesign 的 <c>.ant-btn:hover</c>、<c>.ant-btn:focus</c>（權重較高）。
    /// </summary>
    [Theory]
    [InlineData("crud-action-button")]
    [InlineData("toolbar-icon-button")]
    public void ThemeCss_ShouldGiveIconButtonsA28pxBorderlessSquare(string className)
    {
        var theme = CssComment.Replace(File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "theme.css")), string.Empty);
        var name = Regex.Escape(className);
        var bodies = CssRule.Matches(theme)
            .Where(rule => rule.Groups["selectors"].Value.Split(',').Any(selector =>
                Regex.IsMatch(selector.Trim(), @"^\." + name + @"\." + name + "$")))
            .Select(rule => rule.Groups["body"].Value)
            .ToList();

        Assert.True(bodies.Count > 0, $"theme.css 沒有 .{className}.{className} 的基本規則。");
        var body = string.Join("\n", bodies);
        Assert.Matches(@"\bwidth:\s*28px", body);
        Assert.Matches(@"\bheight:\s*28px", body);
        Assert.Matches(@"\bborder-color:\s*transparent", body);
    }

    private static List<string> PassedClasses(string source, string codeBehind)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ComponentClass.Matches(source))
        {
            var value = match.Groups["value"].Value;
            var expression = value.TrimStart().StartsWith('@');
            var literal = Regex.Replace(value, @"@\((?:[^()]|\([^()]*\))*\)|@[\w.]+", " ");
            foreach (var name in literal.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(name);
            }

            if (expression)
            {
                // Class="@CssClass" 或 Class="@(…)"：看屬性值本身、@code 區塊與 code-behind 裡字串常值中的 class 名稱
                // （只取含連字號的，避免把一般文字算進來；不看標記區，那裡的 class="…" 是自己輸出的元素）。
                var code = source.IndexOf("@code", StringComparison.Ordinal);
                var scope = value + "\n" + (code >= 0 ? source[code..] : string.Empty) + "\n" + codeBehind;
                foreach (Match text in StringLiteral.Matches(scope))
                {
                    foreach (var token in text.Groups["text"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                 .Where(token => Regex.IsMatch(token, @"^[a-z][a-z0-9]*(?:-[a-z0-9]+)+$")))
                    {
                        names.Add(token);
                    }
                }
            }
        }

        return [.. names];
    }

    private static bool Targets(string selector, string className)
    {
        var match = Regex.Match(selector, @"\." + Regex.Escape(className) + @"(?![\w-])");
        if (!match.Success)
        {
            return false;
        }

        var deep = selector.IndexOf("::deep", StringComparison.Ordinal);
        return deep < 0 || match.Index < deep;
    }
}
