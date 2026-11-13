using System.Text.RegularExpressions;

namespace MyProject.Tests.Client;

/// <summary>
/// 清單的「重新整理」只有真的讀到資料才說「已更新最新資料」（0.10.11 實機發現：離線時按重新整理，
/// 畫面同時跳出「目前離線，請恢復連線後再試。」與「已更新最新資料」—— 多數清單不管成功與否都送出成功訊息）。
/// 每一處都必須包在 <c>if (await …)</c> 的成功判斷裡；CRUD 產生器的樣板同樣檢查，之後產生的頁面也不會再犯。
/// </summary>
public class RefreshMessageConventionTests
{
    private const string RefreshedMessage = "\"已更新最新資料\"";

    [Fact]
    public void RefreshedMessage_ShouldOnlyBeShownAfterASuccessfulReload()
    {
        var files = SourceTree.UiFiles("*.cs")
            .Append(Path.Combine(SourceTree.Scripts, "crud-templates", "Web.Client", "Views", "__Plural__", "__Name__ViewView.razor.cs.tmpl"))
            .ToList();

        var usages = new List<string>();
        var violations = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(RefreshedMessage, StringComparison.Ordinal) || lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                var where = $"{Path.GetFileName(file)}:{i + 1}";
                usages.Add(where);
                var previous = i - 1;
                while (previous >= 0 && (string.IsNullOrWhiteSpace(lines[previous]) || lines[previous].Trim() == "{"))
                {
                    previous--;
                }

                if (previous < 0 || !Regex.IsMatch(lines[previous], @"^\s*if \(await \w+\("))
                {
                    violations.Add(where);
                }
            }
        }

        Assert.True(usages.Count >= 10, $"找到的「已更新最新資料」只有 {usages.Count} 處，掃描範圍可能錯了。");
        Assert.True(violations.Count == 0,
            "「已更新最新資料」要包在 if (await ReloadAsync(...)) 裡（讀取失敗時只顯示錯誤）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
