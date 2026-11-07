using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 驗證 .razor 裡沒有「文字緊接著 @程式碼」的寫法（0.9.172 起，瀏覽器人工驗收時發現）。
///
/// Razor 把「字母或數字後面緊接 @」當成 Email 位址，<b>不會</b>切換成程式碼：
/// <c>請聯絡系統管理員@TraceCode.Suffix(...)</c> 會把整段原樣顯示在畫面上，追蹤碼永遠出不來。
/// 中文字也算字母，所以中文句子後面直接接 <c>@</c> 一定中招；全形標點（例如「：」）後面則沒問題。
/// 0.9.77 起三個錯誤提示（頁面錯誤邊界、浮層錯誤邊界、首頁小工具）都是這樣，自動測試完全沒發現。
///
/// 正確寫法：用明確運算式 <c>@(...)</c>，例如 <c>系統管理員@(TraceCode.Suffix(...))</c>；真的要顯示 Email 就寫 <c>@@</c>。
/// </summary>
public sealed class RazorTransitionConventionTests
{
    /// <summary>字母或數字後面緊接 @ 再接識別字（@ 後面是「(」的明確運算式不會被當成 Email，可以直接接在文字後面）。</summary>
    private static readonly Regex EmailLikeTransition = new(@"(?<=[\p{L}\p{Nd}])@[A-Za-z_]", RegexOptions.Compiled);

    [Fact]
    public void RazorComponents_ShouldNotPutCodeRightAfterText()
    {
        var root = FindComponentsRoot();
        var razorFiles = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories).ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(razorFiles);

        var violations = new List<string>();
        foreach (var file in razorFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (EmailLikeTransition.IsMatch(lines[index]))
                {
                    violations.Add($"{Path.GetFileName(file)}:{index + 1} {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "文字後面緊接 @ 會被 Razor 當成 Email 位址、原樣顯示，程式碼不會執行。請改成 @(...)，例如 系統管理員@(TraceCode.Suffix(...))；"
                + "畫面上真的要顯示 Email 時寫 @@。若命中的是 @code 區塊裡的 C# 字串或 Razor 註解（逐行掃描分不出來），請換個寫法避開，例如把 Email 放進常數。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Pattern_ShouldCatchTextFollowedByCode_ButNotPunctuationOrExplicitExpressions()
    {
        Assert.Matches(EmailLikeTransition, "請聯絡系統管理員@TraceCode.Suffix(x)。");
        Assert.DoesNotMatch(EmailLikeTransition, "不受影響@(TraceCode.Suffix(x))");
        Assert.DoesNotMatch(EmailLikeTransition, "密碼規則：@PasswordPolicy.Describe()");
        Assert.DoesNotMatch(EmailLikeTransition, "<div>@context.Name</div>");
        Assert.DoesNotMatch(EmailLikeTransition, "系統管理員 @(TraceCode.Suffix(x))");
    }

    private static string FindComponentsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var srcCandidate = Path.Combine(dir.FullName, "src", "MyProject", "MyProject.Web", "Components");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            var candidate = Path.Combine(dir.FullName, "MyProject.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MyProject.Web/Components。");
    }
}
