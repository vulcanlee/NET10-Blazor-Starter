namespace MyProject.Tests;

/// <summary>
/// 資料頁每一列都要有「異動歷程」（0.9.173 起）：五個主要資料頁與 CRUD 產生器的樣板。
///
/// 漏了不會壞、不會紅，只會讓管理員在這一頁查不到某筆資料是誰改的，所以以測試守門：
/// 一般與「顯示已刪除」兩個分支各一個 <c>&lt;AuditHistoryButton</c>（已刪除的資料更需要查是誰刪的），
/// 頁尾一個 <c>&lt;AuditHistoryDialog</c>；目標類型要與服務寫稽核時的 TargetType 相同。
/// </summary>
public sealed class AuditHistoryConventionTests
{
    public static TheoryData<string, string> Pages => new()
    {
        { Path.Combine("Views", "Categories", "CategoryViewView.razor"), "\"Category\"" },
        { Path.Combine("Views", "Projects", "ProjectViewView.razor"), "\"Project\"" },
        { Path.Combine("Views", "Teams", "TeamViewView.razor"), "\"Team\"" },
        { Path.Combine("Views", "Admins", "MyUserView.razor"), "\"MyUser\"" },
        { Path.Combine("Views", "Admins", "RoleViewView.razor"), "\"RoleView\"" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void DataPage_ShouldOfferAuditHistoryOnEveryRow(string relativePath, string targetType)
    {
        var text = File.ReadAllText(SourceTree.UiFile(relativePath));

        AssertHasHistory(relativePath, text, targetType);
    }

    [Fact]
    public void CrudTemplate_ShouldOfferAuditHistoryOnEveryRow()
    {
        // 0.10.2 起 CRUD 產生器只產生 WASM 頁面（MyProject.Web.Client/Views）。
        var template = Path.Combine(SourceTree.Scripts, "crud-templates",
            "Web.Client", "Views", "__Plural__", "__Name__ViewView.razor.tmpl");

        AssertHasHistory("__Name__ViewView.razor.tmpl", File.ReadAllText(template), "\"__Name__\"");
    }

    private static void AssertHasHistory(string name, string text, string targetType)
    {
        Assert.True(CountOf(text, "<AuditHistoryButton") == 2, $"{name}：一般與「顯示已刪除」兩個分支都要有 <AuditHistoryButton>。");
        Assert.True(CountOf(text, "<AuditHistoryDialog @ref=\"historyDialog\"") == 1, $"{name}：頁尾要有一個 <AuditHistoryDialog @ref=\"historyDialog\" />。");
        Assert.True(CountOf(text, $"historyDialog.ShowAsync({targetType},") == 2, $"{name}：ShowAsync 的目標類型要是 {targetType}（與服務寫稽核的 TargetType 相同）。");
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
