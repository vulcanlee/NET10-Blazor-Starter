using AntDesign;
using Bunit;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 按鈕的提示框在按下後收起（0.10.11 最終巡檢發現）：AI 對話窗按「產生 PDF 報告並下載」後，按鈕在產生期間重新渲染，
/// 滑鼠離開的事件沒有觸發，提示框一直停在對話窗右上角、蓋住關閉鈕 —— 使用者按不到關閉鈕。
/// 工具列按鈕與操作欄按鈕按下時一律收起自己的提示框；另以 <see cref="OverlayStyles"/> 讓提示框不攔截點擊（見下方慣例測試）。
/// </summary>
public sealed class TooltipDismissTests : ComponentTestBase
{
    [Fact]
    public async Task ToolbarIconButton_ShouldHideItsTooltip_WhenClicked()
    {
        var cut = Render<ToolbarIconButton>(parameters => parameters
            .Add(p => p.Title, "產生 PDF 報告並下載")
            .Add(p => p.Icon, "picture_as_pdf"));

        var shown = cut.FindComponent<Tooltip>();
        await cut.InvokeAsync(() => shown.Instance.VisibleChanged.InvokeAsync(true)); // 模擬滑鼠移入
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));

        cut.Find("button.toolbar-icon-button").Click();

        // AntDesign 的 Button 以非同步處理點擊，忙的時候重新渲染會晚一點：等到生效（最多 5 秒）。
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CrudActionButton_ShouldHideItsTooltip_WhenClicked()
    {
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "修改")
            .Add(p => p.Icon, "edit"));

        var shown = cut.FindComponent<Tooltip>();
        await cut.InvokeAsync(() => shown.Instance.VisibleChanged.InvokeAsync(true)); // 模擬滑鼠移入
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));

        cut.Find("button.crud-action-button").Click();

        // AntDesign 的 Button 以非同步處理點擊，忙的時候重新渲染會晚一點：等到生效（最多 5 秒）。
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ToolbarIconButton_ShouldHideItsTooltipAgain_WhenTheClickFinishes()
    {
        // 0.10.16 起（.NET 11 全面實機探索發現，.NET 10 也一樣）：按「匯出 Excel」後，處理期間滑鼠還停在按鈕上，提示框又被打開；
        // 處理完之後滑鼠離開的事件沒有觸發，提示框就一直停在畫面上。處理完成後再收起一次。
        var finish = new TaskCompletionSource();
        Tooltip? tooltip = null;
        var cut = Render<ToolbarIconButton>(parameters => parameters
            .Add(p => p.Title, "匯出 Excel")
            .Add(p => p.Icon, "file_download")
            .Add(p => p.OnClick, async () =>
            {
                await tooltip!.VisibleChanged.InvokeAsync(true); // 處理期間提示框又被打開
                await finish.Task;
            }));
        tooltip = cut.FindComponent<Tooltip>().Instance;

        cut.Find("button.toolbar-icon-button").Click();
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(finish.SetResult);

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CrudActionButton_ShouldHideItsTooltipAgain_WhenTheClickFinishes()
    {
        var finish = new TaskCompletionSource();
        Tooltip? tooltip = null;
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "修改")
            .Add(p => p.Icon, "edit")
            .Add(p => p.OnClick, async () =>
            {
                await tooltip!.VisibleChanged.InvokeAsync(true);
                await finish.Task;
            }));
        tooltip = cut.FindComponent<Tooltip>().Instance;

        cut.Find("button.crud-action-button").Click();
        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(finish.SetResult);

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<Tooltip>().Instance.Visible), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Tooltips_ShouldNotInterceptClicks()
    {
        var styles = File.ReadAllText(Path.Combine(SourceTree.Client, "Commons", "OverlayStyles.razor"));

        Assert.Matches(@"\.ant-tooltip\s*\{[^}]*pointer-events:\s*none", styles);
    }
}
