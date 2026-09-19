using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 守住全站色票層（<c>wwwroot/theme.css</c>）的兩個前提。
///
/// 兩條規則的失敗症狀都是**完全靜默的**：建置不會紅、<c>dotnet format</c> 看不到 CSS、
/// <c>TreatWarningsAsErrors</c> 也管不到樣式表。只有實際開頁面才會發現，
/// 而發現的時候通常已經過了好幾個 commit。
///
/// 規範全文見 docs/architecture/介面視覺設計規範.md。
/// </summary>
public sealed class ThemeConventionTests
{
    /// <summary>
    /// 全站色票的字面值。除了 theme.css 以外的地方出現這些，就是色票又分叉了。
    /// </summary>
    private static readonly string[] PaletteHexes =
    [
        "#51132f", // text
        "#704054", // muted
        "#a52b59", // accent
        "#d37598", // accent-dim
        "#f7b6d0", // accent-light（深色面上的重點色）
        "#ea88ad", // primary 漸層亮端
        "#c43970", // primary 漸層中段
        "#b52a60", // primary 漸層暗端
        "#4a1028", // rail 起點
        "#6b1c3d", // rail 終點
        "#8d2049", // rail active 暗端
    ];

    private static readonly Regex HexColor = new(@"#[0-9a-fA-F]{6}\b", RegexOptions.Compiled);

    /// <summary>
    /// theme.css 的 link 必須排在 AntDesign 的 CSS 之後。
    ///
    /// ⚠️ 這是本專案最容易「整批無聲失效」的一條。theme.css 對 <c>.ant-table</c>、
    /// <c>.ant-pagination</c>、<c>.ant-input</c> 的覆寫若排在 AntDesign 之前，
    /// 同分特異性會輸給後載入的那一份 —— 十個清單頁的表格、分頁與輸入框樣式
    /// **同時**退回 AntDesign 預設，而且沒有任何錯誤訊息。
    ///
    /// 會踩到的情境不是「有人故意改」，而是整理 &lt;head&gt;、把 link 依字母排序，
    /// 或 AntDesign 升版換了引入方式。
    /// </summary>
    [Fact]
    public void ThemeCss_ShouldLoadAfterAntDesignStylesheet()
    {
        var appRazor = Path.Combine(FindWebRoot(), "Components", "App.razor");
        var content = File.ReadAllText(appRazor);

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.False(string.IsNullOrWhiteSpace(content), $"讀不到 {appRazor}。");

        var antIndex = content.IndexOf("ant-design-blazor.css", StringComparison.Ordinal);
        var themeIndex = content.IndexOf("theme.css", StringComparison.Ordinal);

        Assert.True(antIndex >= 0, "App.razor 裡找不到 AntDesign 的樣式表引入。");
        Assert.True(themeIndex >= 0, "App.razor 裡找不到 theme.css 的引入。");

        Assert.True(
            themeIndex > antIndex,
            "theme.css 必須排在 AntDesign 的 CSS 之後，否則對 .ant-* 的覆寫會在同分特異性上"
                + "輸給後載入的 AntDesign —— 十個清單頁的表格／分頁／輸入框樣式會同時靜默失效，"
                + "建置與格式檢查都看不出來。");
    }

    /// <summary>
    /// 色票字面值只能住在 theme.css。
    ///
    /// ⚠️ 沒有這一條，「換品牌色只改一個檔」在交付當天成立、三個功能之後就不成立了 ——
    /// 0.9.30 之前正是如此：同一組粉梅色分散在 Login.razor.css、OverlayStyles.razor
    /// 與各檢視的硬編色碼裡，共三份以上。
    ///
    /// 需要新的深淺時，請在 theme.css 補一個 token 或用 <c>rgba(var(--app-…-rgb), a)</c> 組出來，
    /// 不要在別的檔案寫死色碼。
    /// </summary>
    [Fact]
    public void PaletteHexes_ShouldOnlyLiveInThemeCss()
    {
        var webRoot = FindWebRoot();
        var themeCss = Path.Combine(webRoot, "wwwroot", "theme.css");

        Assert.True(File.Exists(themeCss), $"找不到色票檔 {themeCss}。");

        var files = Directory
            .EnumerateFiles(Path.Combine(webRoot, "Components"), "*.css", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(webRoot, "Components"), "*.razor", SearchOption.AllDirectories))
            .Concat([Path.Combine(webRoot, "wwwroot", "app.css")])
            .Where(File.Exists)
            .ToList();

        // 掃描路徑若失效，測試會空跑綠燈，等於沒有守門。
        Assert.NotEmpty(files);

        var violations = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];

                // 註解裡提到色碼是說明用途（例如對比度的說明），不算分叉。
                if (line.TrimStart().StartsWith("/*", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith('*'))
                {
                    continue;
                }

                foreach (var match in HexColor.Matches(line).Cast<Match>())
                {
                    if (PaletteHexes.Contains(match.Value.ToLowerInvariant()) == false)
                    {
                        continue;
                    }

                    violations.Add($"{Path.GetFileName(file)}:{index + 1} {line.Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "色票字面值只能出現在 wwwroot/theme.css。需要新的深淺請補 token 或用 "
                + "rgba(var(--app-…-rgb), a) 組出來 —— 寫死色碼不會壞掉任何東西，"
                + "只會讓「換品牌色只改一個檔」這個保證在日後悄悄失效。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    private static string FindWebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MyProject.Web");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "MyProject", "MyProject.Web");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MyProject.Web。");
    }
}
