using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 一個清單只能有一欄宣告預設排序（0.9.165）。
///
/// 背景：帳號管理曾有 4 欄、角色管理 3 欄同時宣告 <c>DefaultSortOrder</c>。伺服器端排序只取第一個
/// 有方向的欄位（<c>TableSortHelper.GetCurrentSortModel</c>），但畫面上可能好幾欄都亮著排序箭頭，
/// 使用者看不出實際依哪一欄排序。建置與其他測試都抓不到，所以以本測試守門。
/// 一個檔案只放一張表，因此以檔案為單位檢查。
/// </summary>
public sealed class TableDefaultSortConventionTests
{
    private static readonly Regex DefaultSortOrder = new(@"\bDefaultSortOrder\s*=", RegexOptions.Compiled);

    [Fact]
    public void EachRazorFile_ShouldDeclareAtMostOneDefaultSortOrder()
    {
        var root = FindComponentsRoot();
        var violations = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Select(path => (Path: Path.GetRelativePath(root, path), Count: DefaultSortOrder.Matches(File.ReadAllText(path)).Count))
            .Where(x => x.Count > 1)
            .Select(x => $"{x.Path}：{x.Count} 欄")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "以下清單有多欄宣告 DefaultSortOrder，只保留實際預設排序的那一欄："
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    private static string FindComponentsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MyProject.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "MyProject", "MyProject.Web", "Components");
            if (Directory.Exists(srcCandidate))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MyProject.Web/Components。");
    }
}
