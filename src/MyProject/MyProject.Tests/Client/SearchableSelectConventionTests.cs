using System.Text.RegularExpressions;

namespace MyProject.Tests.Client;

/// <summary>
/// 可搜尋的下拉（<c>EnableSearch</c>）每個選項都要有 <c>Label</c>（0.10.16 起，.NET 11 全面實機探索發現，.NET 10 也一樣）：
/// AntDesign 的 Select 以選項的 <c>Label</c> 比對使用者打的字；選項文字只寫在子內容（<c>&lt;SelectOption Value="@x"&gt;@x&lt;/SelectOption&gt;</c>）時
/// <c>Label</c> 是空的，打任何字都比不到，下拉變成一片空白——搜尋形同不能用（專案的分類、團隊，使用者的角色、團隊等九處都是）。
/// 涵蓋 CRUD 產生器樣板。
/// </summary>
public sealed class SearchableSelectConventionTests
{
    [Fact]
    public void EveryOptionOfASearchableSelect_ShouldHaveALabel()
    {
        var files = SourceTree.UiFiles("*.razor")
            .Append(Path.Combine(SourceTree.Scripts, "crud-templates", "Web.Client", "Views", "__Plural__", "__Name__ViewView.razor.tmpl"));
        var offenders = new List<string>();
        var searchableSelects = 0;
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            foreach (Match select in Regex.Matches(code, @"<Select\b(?<attrs>(?:""[^""]*""|[^>""])*?)(?<!/)>(?<body>[\s\S]*?)</Select>"))
            {
                if (!select.Groups["attrs"].Value.Contains("EnableSearch", StringComparison.Ordinal))
                {
                    continue;
                }

                searchableSelects++;
                foreach (Match option in Regex.Matches(select.Groups["body"].Value, @"<SelectOption\b[^>]*>"))
                {
                    if (!option.Value.Contains("Label=", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}：{option.Value.Trim()}");
                    }
                }
            }
        }

        Assert.True(searchableSelects >= 10, $"只找到 {searchableSelects} 個可搜尋的下拉，比對規則可能失效。");
        Assert.True(offenders.Count == 0, "可搜尋的下拉有選項沒有 Label（打字搜尋會比不到任何選項）：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
