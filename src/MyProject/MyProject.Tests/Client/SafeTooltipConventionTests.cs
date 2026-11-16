using System.Text.RegularExpressions;

namespace MyProject.Tests.Client;

/// <summary>
/// 0.10.32 起：提示框滑鼠移開後不可殘留。全站一律用 <c>SafeTooltip</c>（補上 AntDesign 1.6.3 的兩個漏洞），
/// 不直接寫 AntDesign 的 <c>&lt;Tooltip&gt;</c>；表格標題列的排序提示是 AntDesign 表格內部的提示框，
/// 由 js/sorter-tooltip-guard.js 補救，它要在 ant-design-blazor.js 之後載入。
/// </summary>
public class SafeTooltipConventionTests
{
    private static readonly Regex RawTooltip = new(@"<Tooltip[\s>]", RegexOptions.Compiled);

    [Fact]
    public void Pages_ShouldUseSafeTooltip_InsteadOfAntDesignTooltip()
    {
        var templates = Directory.GetFiles(Path.Combine(SourceTree.Scripts, "crud-templates"), "*.razor.tmpl", SearchOption.AllDirectories);
        var offenders = SourceTree.UiFiles("*.razor").Concat(templates)
            .Where(file => RawTooltip.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(SourceTree.RepoRoot, file))
            .ToList();

        Assert.True(offenders.Count == 0,
            "這些檔案直接用 AntDesign 的 <Tooltip>，滑鼠移開後提示框可能停住，請改用 <SafeTooltip>：" + string.Join("、", offenders));
    }

    [Fact]
    public void IndexHtml_ShouldLoadTheSorterTooltipGuard_AfterAntDesign()
    {
        var html = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "index.html"));
        var antDesign = html.IndexOf("<script src=\"_content/AntDesign/js/ant-design-blazor.js\"></script>", StringComparison.Ordinal);
        var guard = html.IndexOf("<script src=\"js/sorter-tooltip-guard.js\"></script>", StringComparison.Ordinal);

        Assert.True(guard >= 0, "index.html 沒有載入 js/sorter-tooltip-guard.js（標題列的排序提示快速掃過時可能停住）。");
        Assert.True(guard > antDesign && antDesign >= 0, "js/sorter-tooltip-guard.js 要排在 ant-design-blazor.js 之後。");
    }
}
