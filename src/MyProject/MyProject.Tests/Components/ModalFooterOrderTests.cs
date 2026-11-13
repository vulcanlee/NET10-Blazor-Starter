using System.Text.RegularExpressions;

namespace MyProject.Tests.Components;

/// <summary>
/// 對話窗頁尾的按鈕順序（0.10.14 起，升級 .NET 11）：AntDesign 1.6.3-dotnet11.rc3 的預設頁尾改成先「取消」再「儲存」（PR #4857），
/// 升級時刻意維持使用者看慣的「儲存　取消」（<c>OverlayStyles</c> 以 <c>flex-direction: row-reverse</c> 反轉畫面順序），等使用者決定要不要採用新順序。
/// 反轉只對預設頁尾安全：自訂的 Footer 內容也會被反轉，所以對話窗不可以有自訂頁尾（只能用預設頁尾或 <c>Footer="null"</c>）。
/// </summary>
public sealed class ModalFooterOrderTests
{
    [Fact]
    public void OverlayStyles_ShouldKeepSaveBeforeCancel()
    {
        var styles = File.ReadAllText(Path.Combine(SourceTree.Client, "Commons", "OverlayStyles.razor"));

        Assert.Matches(@"\.ant-modal \.ant-modal-footer\s*\{[^}]*flex-direction:\s*row-reverse", styles);
    }

    /// <summary>屬性寫法（<c>Footer="…"</c>、<c>Footer=@x</c>）、子內容 <c>&lt;Footer&gt;</c>、程式裡的 <c>Footer = …</c>（ModalOptions 等）都算自訂頁尾。</summary>
    [Fact]
    public void Modals_ShouldUseOnlyTheDefaultFooterOrNoFooter()
    {
        var razor = SourceTree.UiFiles("*.razor")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\\bFooter=(?<value>\"[^\"]*\"|@\\S+)|<Footer>")
                .Where(match => match.Groups["value"].Value != "\"null\"")
                .Select(match => $"{Path.GetFileName(path)}：{match.Value}"));
        var code = SourceTree.UiFiles("*.cs")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "\\bFooter\\s*=\\s*(?!null\\b)[^;=\\s]")
                .Select(match => $"{Path.GetFileName(path)}：{match.Value.Trim()}"));
        var offenders = razor.Concat(code).ToList();

        Assert.True(offenders.Count == 0, "對話窗有自訂頁尾，會被 OverlayStyles 的頁尾反轉弄亂順序：" + string.Join("、", offenders));
    }
}
