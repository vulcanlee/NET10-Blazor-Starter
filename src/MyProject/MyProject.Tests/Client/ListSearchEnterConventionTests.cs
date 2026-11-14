namespace MyProject.Tests.Client;

/// <summary>
/// 清單的搜尋框按 Enter 就搜尋（0.10.17 起，使用者 2026/11/14 決定）：以前按 Enter 沒有反應，一定要按放大鏡按鈕。
/// 每個清單搜尋框（Placeholder「請輸入搜尋關鍵字」）都要有 <c>OnPressEnter</c>，呼叫與「搜尋」按鈕相同的方法；涵蓋 CRUD 產生器樣板。
/// </summary>
public sealed class ListSearchEnterConventionTests
{
    [Fact]
    public void EveryListSearchBox_ShouldSearchOnEnter()
    {
        var files = SourceTree.UiFiles("*.razor")
            .Append(Path.Combine(SourceTree.Scripts, "crud-templates", "Web.Client", "Views", "__Plural__", "__Name__ViewView.razor.tmpl"));
        var boxes = files
            .SelectMany(path => File.ReadAllLines(path)
                .Where(line => line.Contains("Placeholder=\"請輸入搜尋關鍵字\"", StringComparison.Ordinal))
                .Select(line => (File: Path.GetFileName(path), Line: line.Trim())))
            .ToList();

        Assert.True(boxes.Count >= 6, $"只找到 {boxes.Count} 個清單搜尋框，比對規則可能失效。");
        var missing = boxes.Where(x => !x.Line.Contains("OnPressEnter=\"@(async _ => await OnSearchAsync())\"", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "這些搜尋框按 Enter 不會搜尋：" + string.Join("、", missing.Select(x => x.File)));
    }
}
