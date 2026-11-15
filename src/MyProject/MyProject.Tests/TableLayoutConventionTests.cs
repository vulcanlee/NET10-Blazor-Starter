using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 清單表格的版面規則（0.10.25 起）。
///
/// 1440 寬、側邊欄展開時，專案清單與使用者管理的欄位被擠到一個字一行、整頁出現橫向捲軸，
/// 資料保留頁的「下次會清掉」「自動清理」被固定的「操作」欄蓋住而看不出右邊還有內容。
/// 兩個原因都不會讓建置或測試變紅，只有實際開頁面才看得到：
/// 外層容器少了 <c>overflow-x: auto</c>（表格撐寬整頁），以及欄位沒設 <c>Width</c>（AntDesign 自動版面把中文逐字斷行）。
/// 規則全文見 docs/architecture/介面視覺設計規範.md §4.2。
/// </summary>
public sealed class TableLayoutConventionTests
{
    // class 屬性裡任何一個以 table-wrap 結尾的類別（不分大小寫，class 與 Class 都算）。
    private static readonly Regex TableWrapClass = new(@"\bclass=""[^""]*?\b(?<name>[a-z0-9-]*table-wrap)\b[^""]*""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 欄位標籤：引號內的屬性值整段跳過，屬性裡的 lambda（=>）不會讓比對提早結束。
    private static readonly Regex ColumnTag = new(@"<(?:(?:Action)?Column|Selection)\b(?:""[^""]*""|[^>""])*>", RegexOptions.Compiled);

    private static readonly Regex WidthAttribute = new(@"\bWidth=""(?<value>\d+)""", RegexOptions.Compiled);

    /// <summary>已刪除模式才出現的欄：不算進 ScrollX（欄寬加總超過 ScrollX 時表格會自動變寬）。</summary>
    private static readonly string[] DeletedModeColumns = ["刪除時間", "刪除者"];

    /// <summary>
    /// 每個表格的外層容器都要能自己橫向捲動：用共用的 <c>.view-table-wrap</c>（theme.css），
    /// 或在同一個元件的 <c>.razor.css</c> 寫 <c>overflow-x: auto</c>。
    /// ⚠️ Blazor 的 CSS 隔離以元件為界，別的元件寫的同名規則套不到這裡（TokenUsageGroupTable 踩過）。
    /// </summary>
    [Fact]
    public void EveryTableWrap_ShouldScrollHorizontally_InsteadOfWideningThePage()
    {
        var razorFiles = SourceTree.UiFiles("*.razor");
        Assert.NotEmpty(razorFiles);

        var theme = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "theme.css"));
        var checkedCount = 0;
        var problems = new List<string>();
        foreach (var razor in razorFiles)
        {
            foreach (Match match in TableWrapClass.Matches(File.ReadAllText(razor)))
            {
                checkedCount++;
                var name = match.Groups["name"].Value;
                var scopedCss = razor + ".css";
                var css = (File.Exists(scopedCss) ? File.ReadAllText(scopedCss) : string.Empty) + "\n" + theme;
                if (!ScrollsHorizontally(css, name))
                {
                    problems.Add($"{Path.GetFileName(razor)}：.{name}");
                }
            }
        }

        Assert.True(checkedCount > 0, "一個 table-wrap 都沒找到，掃描路徑可能失效了。");
        Assert.True(problems.Count == 0,
            "這些表格外層沒有 overflow-x: auto，欄位多時會撐寬整頁、出現整頁橫向捲軸："
            + string.Join("、", problems) + "。請改用共用的 view-table-wrap，或在同一個元件的 .razor.css 補上 overflow-x: auto。");
    }

    /// <summary>
    /// 欄位多的四張表（專案清單、使用者管理、資料保留、AI 對話紀錄）每一欄（含勾選欄）都要有寬度，
    /// 而且 <c>ScrollX</c> 要等於平常會出現的欄寬加總（不含已刪除模式才有的欄）。
    /// 沒有寬度時 AntDesign 會把所有欄壓進容器寬度、中文逐字斷行；固定在右側的「操作」欄沒寬度時，
    /// 相鄰固定欄的計算還會丟 ArgumentNullException（TokenUsageView 的註解）；
    /// 設了 ScrollX 之後，沒寬度的勾選欄會被擠到 0、整欄消失（0.10.25 實測）。
    /// ScrollX 比加總小時畫面不會壞（表格自動變寬），但註解與文件寫的規則就不成立了（獨立審查抓到 AI 對話紀錄少算 100）。
    /// </summary>
    [Theory]
    [InlineData("Views/Projects/ProjectViewView.razor")]
    [InlineData("Views/Admins/MyUserView.razor")]
    [InlineData("Views/Admins/DataRetentionView.razor")]
    [InlineData("Views/Analytics/AiCallLogView.razor")]
    public void WideTables_ShouldGiveEveryColumnAWidth_AndScrollXShouldBeTheirSum(string relativePath)
    {
        var content = File.ReadAllText(SourceTree.UiFile(relativePath));
        var columns = ColumnTag.Matches(content).Select(match => match.Value).ToList();
        Assert.NotEmpty(columns);

        var missing = columns.Where(column => !WidthAttribute.IsMatch(column)).Select(TitleOf).ToList();
        Assert.True(missing.Count == 0, $"{relativePath} 這些欄沒有 Width：{string.Join("、", missing)}。");

        var scrollX = Regex.Match(content, @"\bScrollX=""(?<value>\d+)""");
        Assert.True(scrollX.Success, $"{relativePath} 的 Table 沒有設 ScrollX（數字）：捲動要交給 AntDesign，固定的操作欄才會有陰影。");

        var expected = columns.Where(column => !DeletedModeColumns.Contains(TitleOf(column)))
            .Sum(column => int.Parse(WidthAttribute.Match(column).Groups["value"].Value));
        Assert.True(int.Parse(scrollX.Groups["value"].Value) == expected,
            $"{relativePath} 的 ScrollX 是 {scrollX.Groups["value"].Value}，平常會出現的欄寬加總（含勾選欄、不含已刪除模式的欄）是 {expected}。");
    }

    private static string TitleOf(string columnTag)
        => columnTag.StartsWith("<Selection", StringComparison.Ordinal)
            ? "勾選欄"
            : Regex.Match(columnTag, @"Title=""(?<title>[^""]*)""").Groups["title"].Value;

    private static bool ScrollsHorizontally(string css, string className)
        => Regex.Matches(css, @"\." + Regex.Escape(className) + @"\s*\{(?<body>[^}]*)\}")
            .Any(rule => Regex.IsMatch(rule.Groups["body"].Value, @"\boverflow(?:-x)?\s*:\s*(?:auto|scroll)\b"));
}
