namespace MyProject.Tests;

/// <summary>
/// 有單筆「刪除」的列表頁都要能勾選多筆後「刪除選取」（0.9.132 起）。
///
/// 新頁面多半是複製既有檢視或用 New-CrudModule.ps1 產生；漏了勾選欄或按鈕不會壞、不會紅，
/// 只會讓這一頁和其他頁不一致，因此以測試守門：操作欄有 <c>CrudActionButton Title="刪除"</c>，
/// 就必須同時有 <c>&lt;Selection</c> 與 <c>&lt;BatchDeleteButton</c>。
/// </summary>
public sealed class BatchDeleteConventionTests
{
    [Fact]
    public void ViewsWithRowDelete_ShouldOfferBatchDelete()
    {
        // 0.10.6 起有單筆刪除的列表頁都搬到 WASM 了：Server 的 Components 與 MyProject.Web.Client 兩處都掃。
        var views = UiRazorFiles()
            .Where(path => File.ReadAllText(path).Contains("CrudActionButton Title=\"刪除\"", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(views);

        var violations = views
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(x => !x.Text.Contains("<Selection", StringComparison.Ordinal)
                        || !x.Text.Contains("<BatchDeleteButton", StringComparison.Ordinal))
            .Select(x => $"{x.Name} 有單筆刪除，但缺少勾選欄 <Selection /> 或工具列的 <BatchDeleteButton />。")
            .ToList();

        Assert.True(violations.Count == 0,
            "列表頁的多筆刪除不一致（見 docs/architecture/開發慣例與限制速查.md「多筆刪除」）："
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// ⚠️ 勾選欄 <c>&lt;Selection</c> 不可包在 <c>&lt;RequirePermission&gt;</c>（或任何元件）裡，要用表格內的 <c>@if</c>（0.9.150）。
    /// 包起來時 AntDesign 會讓第 3 列起的欄位序號錯位，右側固定的操作欄讀到沒寬度的欄而丟 ArgumentNullException，
    /// 整頁空白 —— 資料不到 3 列時看不出來（專案頁有了 10 筆測試專案才爆出來）。
    /// 行為由 <c>TableSelectionContractTests.SelectionWrappedInComponent_WithRightFixedColumn_ShouldThrowFromThirdRow</c> 鎖住。
    /// CRUD 產生器的樣板一併檢查，產生出來的新模組才不會帶著這個問題。
    /// </summary>
    [Fact]
    public void Selection_ShouldNotBeWrappedInRequirePermission()
    {
        var componentsRoot = FindComponentsRoot();
        var templateRoot = Path.Combine(componentsRoot, "..", "..", "..", "..", "scripts", "crud-templates");
        var files = UiRazorFiles()
            .Concat(Directory.Exists(templateRoot)
                ? Directory.EnumerateFiles(templateRoot, "*.razor.tmpl", SearchOption.AllDirectories)
                : [])
            .ToList();

        var violations = files
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .Where(x => IsSelectionInsideRequirePermission(x.Text))
            .Select(x => $"{x.Name}：<Selection> 包在 <RequirePermission> 裡，請改用表格內的 @if。")
            .ToList();

        Assert.True(Directory.Exists(templateRoot), $"找不到 CRUD 樣板目錄：{templateRoot}");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 某個 <c>&lt;Selection</c> 之前最近的 <c>&lt;RequirePermission</c> 還沒關閉，就是包在裡面。
    /// Razor 註解（<c>@* … *@</c>）先拿掉 —— 說明這條規則的註解本身就會提到這兩個標籤。
    /// </summary>
    private static bool IsSelectionInsideRequirePermission(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"@\*.*?\*@", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var index = 0;
        while ((index = text.IndexOf("<Selection", index, StringComparison.Ordinal)) >= 0)
        {
            var open = text.LastIndexOf("<RequirePermission", index, StringComparison.Ordinal);
            var close = text.LastIndexOf("</RequirePermission>", index, StringComparison.Ordinal);
            if (open >= 0 && open > close)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static string FindComponentsRoot() => SourceTree.Components;

    private static IEnumerable<string> UiRazorFiles()
        => SourceTree.UiRoots.SelectMany(root => Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
