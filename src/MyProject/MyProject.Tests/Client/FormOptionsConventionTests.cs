using System.Text.RegularExpressions;

namespace MyProject.Tests.Client;

/// <summary>
/// 畫面選項資料一致性（0.10.15 起）：前後端分離後，下拉、樹狀選單的選項是畫面向伺服器讀的；別的使用者可能在這之後新增、刪除或停用了分類、團隊、角色。
/// 規則（使用者 2026/11/14 決定）：
/// <list type="number">
/// <item><b>開窗時重讀</b>：有表單選項的頁面以 <c>LoadFormOptionsAsync</c> 讀選項，每個開窗方法（新增、修改、複製）一開始就呼叫它。</item>
/// <item><b>存檔失敗後重讀</b>：伺服器擋下已不存在或已停用的選項時（<c>SelectedOptionGuard</c>），畫面換成最新的選項。</item>
/// <item><b>查詢條件下拉</b>：只有查詢用選項的頁面，按「重新整理」時重讀。</item>
/// </list>
/// 新頁面（含 CRUD 產生器的樣板）照做；這裡守門。
/// </summary>
public sealed class FormOptionsConventionTests
{
    /// <summary>只有查詢條件下拉（沒有表單選項）的頁面：「重新整理」要重讀選項。值是重讀選項的方法名稱。</summary>
    private static readonly Dictionary<string, string> FilterOnlyPages = new(StringComparer.Ordinal)
    {
        ["AuditLogView.razor.cs"] = "ReloadActionOptionsAsync",
        ["ExceptionLogView.razor.cs"] = "ReloadOptionsAsync",
        ["AiCallLogView.razor.cs"] = "ReloadFilterOptionsAsync",
        ["TokenUsageView.razor.cs"] = "ReloadFilterOptionsAsync",
    };

    /// <summary>讀 options 但不是下拉資料的頁面（例如日誌檢視只讀「AI 是否可用」）。</summary>
    private static readonly string[] NotDropdownData = ["LogViewerView.razor.cs"];

    /// <summary>
    /// 一定要照規則的表單頁面（含不經 OptionsAsync、從別的端點拿選項的公告管理）。
    /// 另外，凡是定義了 <c>LoadFormOptionsAsync</c> 的頁面（例如 CRUD 產生器以 <c>-WithTeams</c> 產生的）都一併檢查。
    /// </summary>
    private static readonly string[] FormPageFiles =
    [
        "CategoryViewView.razor.cs",
        "ProjectViewView.razor.cs",
        "RoleViewView.razor.cs",
        "MyUserView.razor.cs",
        "AnnouncementView.razor.cs",
    ];

    public static TheoryData<string> FormPages => new(FormPageFiles.Union(PagesDefiningLoadFormOptions()).Distinct(StringComparer.Ordinal).ToArray());

    private static IEnumerable<string> PagesDefiningLoadFormOptions()
        => SourceTree.UiFiles("*.razor.cs")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"Task(<bool>)? LoadFormOptionsAsync\(\)"))
            .Select(path => Path.GetFileName(path)!);

    [Theory]
    [MemberData(nameof(FormPages))]
    public void FormPages_ShouldReloadOptionsEveryTimeADialogOpens_AndAfterAFailedSave(string file)
    {
        var code = File.ReadAllText(SourceTree.UiFiles(file).Single());

        Assert.Matches(@"Task(<bool>)? LoadFormOptionsAsync\(\)", code);
        foreach (var opener in OpenerNames(code))
        {
            Assert.True(BodyStartsWithOptionsReload(code, opener), $"{file} 的 {opener} 一開始沒有 await LoadFormOptionsAsync()（開窗時要重讀選項）。");
        }

        Assert.True(OpenerNames(code).Count >= 2, $"{file} 找不到開窗方法（OnAddAsync／OnEditAsync）。");
        // 存檔失敗後重讀：至少一個「ViewNotification.Error … LoadFormOptionsAsync」或錯誤訊息後的重讀。
        Assert.Matches(@"(ViewNotification\.Error\([^;]*\)|errorMessage = result\.Message);\s*\r?\n\s*await LoadFormOptionsAsync\(\)", code);
    }

    [Fact]
    public void EveryPageThatReadsOptions_ShouldFollowOneOfTheRules()
    {
        var formPages = FormPageFiles.Union(PagesDefiningLoadFormOptions()).ToHashSet(StringComparer.Ordinal);
        var offenders = SourceTree.UiFiles("*.razor.cs")
            .Where(path => File.ReadAllText(path).Contains("Api.OptionsAsync()", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => !formPages.Contains(name!) && !FilterOnlyPages.ContainsKey(name!) && !NotDropdownData.Contains(name))
            .ToList();

        Assert.True(offenders.Count == 0, "這些頁面讀了選項，但沒有登記成「表單選項」或「查詢條件」頁面（開窗或重新整理時要重讀）：" + string.Join("、", offenders));
    }

    [Fact]
    public void FilterOnlyPages_ShouldReloadOptionsOnRefresh()
    {
        foreach (var (file, method) in FilterOnlyPages)
        {
            var code = File.ReadAllText(SourceTree.UiFiles(file).Single());
            var refresh = Regex.Match(code, @"Task OnRefreshAsync\(\)\s*\{(?<body>[\s\S]*?)\n\s{8}\}");
            Assert.True(refresh.Success, $"{file} 找不到 OnRefreshAsync。");
            Assert.Contains($"await {method}()", refresh.Groups["body"].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CrudGeneratorTemplate_WithTeams_ShouldReloadOptionsWhenADialogOpens()
    {
        var template = File.ReadAllText(Path.Combine(SourceTree.Scripts, "crud-templates", "Web.Client", "Views", "__Plural__", "__Name__ViewView.razor.cs.tmpl"));

        Assert.Contains("private async Task<bool> LoadFormOptionsAsync()", template, StringComparison.Ordinal);
        Assert.Matches(@"Task OnAddAsync\(\)\s*\{\s*#IF TEAMS\s*await LoadFormOptionsAsync\(\);", template);
        Assert.Matches(@"Task OnEditAsync\([^)]*\)\s*\{\s*#IF TEAMS\s*await LoadFormOptionsAsync\(\);", template);
        Assert.Matches(@"(?s)ViewNotification\.Error\(NotificationService, result\.Message\);\s*#IF TEAMS\s*await LoadFormOptionsAsync\(\)", template);
    }

    private static List<string> OpenerNames(string code)
        => Regex.Matches(code, @"\bTask (?<name>On(Add|Edit|Copy)Async)\(").Select(m => m.Groups["name"].Value).Distinct().ToList();

    private static bool BodyStartsWithOptionsReload(string code, string method)
    {
        // 第一行（包在 try 裡也算：例外由 catch 統一顯示）。
        var match = Regex.Match(code, $@"\bTask {method}\([^)]*\)\s*\{{\s*(try\s*\{{\s*)?(?<first>[^\r\n]*)");
        return match.Success && match.Groups["first"].Value.Trim() == "await LoadFormOptionsAsync();";
    }
}
