using AntDesign;
using Bunit;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 提示框滑鼠移開後一定會收起（0.10.32 起）。實機（scripts/e2e/ux-tooltips.mjs）發現專案項目頁滑過的「修改」「刪除」提示框全部停住：
/// 按鈕以 <c>@bind-Visible</c> 在按下時收起提示框，但提示框一顯示 <c>Visible</c> 就變成 true，
/// AntDesign 的提示框在 <c>Visible</c> 為 true、又沒有 <c>ComplexAutoCloseAndVisible</c> 時，滑鼠離開不會自己收起。
/// 「顯示到一半就離開」的另一個漏洞要有真的瀏覽器才能重現，由 e2e 腳本驗證。
/// </summary>
public sealed class SafeTooltipTests : ComponentTestBase
{
    [Fact]
    public void SafeTooltip_ShouldCloseOnMouseLeave_EvenWhenVisibleIsBound()
    {
        Assert.True(new SafeTooltip().ComplexAutoCloseAndVisible);
    }

    [Fact]
    public void Buttons_ShouldUseSafeTooltip()
    {
        var crud = Render<CrudActionButton>(parameters => parameters.Add(p => p.Title, "修改").Add(p => p.Icon, "edit"));
        var toolbar = Render<ToolbarIconButton>(parameters => parameters.Add(p => p.Title, "新增").Add(p => p.Icon, "add"));

        Assert.Single(crud.FindComponents<SafeTooltip>());
        Assert.Single(toolbar.FindComponents<SafeTooltip>());
    }

    [Fact]
    public async Task CrudActionButton_ShouldHideItsTooltip_WhenTheMouseLeaves()
    {
        var cut = Render<CrudActionButton>(parameters => parameters
            .Add(p => p.Title, "修改")
            .Add(p => p.Icon, "edit"));

        var tooltip = cut.FindComponent<SafeTooltip>();
        await cut.InvokeAsync(() => tooltip.Instance.VisibleChanged.InvokeAsync(true)); // 提示框顯示 → 綁定的 Visible 變成 true
        cut.WaitForAssertion(() => Assert.True(tooltip.Instance.IsOverlayShow()), TimeSpan.FromSeconds(5));

        await cut.Find("div").TriggerEventAsync("onmouseleave", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            Assert.False(tooltip.Instance.IsOverlayShow());
            Assert.False(tooltip.Instance.Visible);
        }, TimeSpan.FromSeconds(5));
    }
}
