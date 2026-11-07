using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 使用手冊的守門（0.9.162 起）：《新手完整操作指南》要涵蓋每一個有操作說明的頁面，
/// 新增頁面時不能只寫操作說明、忘了手冊（規則見 docs/guides/使用手冊寫作規範.md 第 9 節）。
/// 只檢查「有沒有章節」，不檢查內容對不對。
/// </summary>
public sealed class UserManualCoverageTests
{
    private const string ManualFile = "新手完整操作指南.md";
    private const string StyleGuideFile = "使用手冊寫作規範.md";

    private static readonly Regex Heading = new(@"^#{2,4}\s+.*$", RegexOptions.Multiline);

    [Fact]
    public void ManualAndStyleGuide_Exist()
    {
        var guides = Path.Combine(FindUp("docs"), "guides");

        Assert.True(File.Exists(Path.Combine(guides, ManualFile)), $"找不到 docs/guides/{ManualFile}。");
        Assert.True(File.Exists(Path.Combine(guides, StyleGuideFile)), $"找不到 docs/guides/{StyleGuideFile}。");
    }

    [Fact]
    public void EveryHelpTopicRoute_HasAManualSection()
    {
        var manual = File.ReadAllText(Path.Combine(FindUp("docs"), "guides", ManualFile));
        var headings = Heading.Matches(manual).Select(match => match.Value).ToList();

        var missing = LoadHelpRoutes()
            .Where(route => headings.Any(heading => heading.Contains($"`{route}`", StringComparison.OrdinalIgnoreCase)) == false)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"下列頁面在 Datas/HelpTopics.json 有操作說明，但《{ManualFile}》沒有標題含 `/路由` 的章節"
            + $"（照 docs/guides/{StyleGuideFile} 第 5 節的樣板補上）：{string.Join('、', missing)}");
    }

    private static List<string> LoadHelpRoutes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindUp("MyProject.Web"), "Datas", "HelpTopics.json")));
        return [.. document.RootElement.EnumerateArray().Select(topic => topic.GetProperty("route").GetString()!)];
    }

    private static string FindUp(string relativeName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, relativeName), Path.Combine(dir.FullName, "src", "MyProject", relativeName) })
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"找不到 {relativeName}。");
    }
}
