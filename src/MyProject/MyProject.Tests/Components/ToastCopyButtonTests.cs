using Bunit;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 通知裡的「複製」按鈕：結果直接顯示在按鈕文字上，幾秒後恢復成「複製」。
/// </summary>
public sealed class ToastCopyButtonTests : ComponentTestBase
{
    private const string CopyFunction = "appClipboard.copyText";

    [Fact]
    public void Click_WhenCopied_ShouldShowCopied_ThenRestore()
    {
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(true);
        var cut = RenderButton();

        cut.Find("button").Click();

        Assert.Equal("已複製", cut.Find("button").TextContent.Trim());
        cut.WaitForAssertion(() => Assert.Equal("複製", cut.Find("button").TextContent.Trim()), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Click_WhenBrowserRefuses_ShouldShowCopyFailed()
    {
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(false);
        var cut = RenderButton();

        cut.Find("button").Click();

        Assert.Equal("複製失敗", cut.Find("button").TextContent.Trim());
    }

    [Fact]
    public void Click_ShouldCopyTitleTextTraceCodeTimeAndThePageItAppearedOn()
    {
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(true);
        var cut = RenderButton();

        cut.Find("button").Click();

        var copied = Assert.IsType<string>(Assert.Single(JSInterop.Invocations[CopyFunction]).Arguments[0]);
        Assert.Equal(
            string.Join(Environment.NewLine,
                "系統訊息",
                "名稱重複",
                "錯誤追蹤碼：ABCD1234",
                "時間：2026/10/19 14:03:22",
                "頁面：http://localhost/"),
            copied);
    }

    private IRenderedComponent<ToastCopyButton> RenderButton()
        => Render<ToastCopyButton>(parameters => parameters
            .Add(p => p.Title, "系統訊息")
            .Add(p => p.Description, "名稱重複")
            .Add(p => p.TraceCode, "ABCD1234")
            .Add(p => p.OccurredAt, new DateTime(2026, 10, 19, 14, 3, 22)));
}
