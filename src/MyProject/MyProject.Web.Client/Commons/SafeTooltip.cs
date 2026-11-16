using AntDesign;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 滑鼠移開後一定會收起的提示框（0.10.32 起）：全站一律用它，不直接用 AntDesign 的 <c>&lt;Tooltip&gt;</c>（<c>SafeTooltipConventionTests</c> 守門）。
/// 外觀、位置、延遲都與 AntDesign 的提示框相同，只補上 AntDesign 1.6.3 的兩個漏洞（實機以 scripts/e2e/ux-tooltips.mjs 重現）：
/// <list type="number">
/// <item>外部以 <c>@bind-Visible</c> 控制時（<see cref="ToolbarIconButton"/>／<see cref="CrudActionButton"/> 按下時收起提示框），
/// 提示框一顯示 <c>Visible</c> 就變成 true，AntDesign 之後滑鼠離開時不再自己收起 —— 滑過的按鈕全部停住。
/// 預設 <c>ComplexAutoCloseAndVisible = true</c>：綁定 <c>Visible</c> 時滑鼠離開照樣收起。</item>
/// <item>滑鼠在提示框「顯示到一半」（首次顯示要先渲染、再以 JS 定位）時離開，AntDesign 因為還沒顯示完成而跳過收起，
/// 顯示完成後也不再檢查 —— 快速掃過時偶爾停住。離開時若還沒顯示，之後短暫檢查：顯示完成而滑鼠不在就收起。</item>
/// </list>
/// 表格標題列的排序提示是 AntDesign 表格內部的提示框，換不成這個元件，另由 <c>wwwroot/js/sorter-tooltip-guard.js</c> 補救。
/// </summary>
public class SafeTooltip : Tooltip
{
    /// <summary>離開後等待「顯示到一半」的提示框顯示完成：每 50 毫秒檢查一次、最多約 1.5 秒（CPU 慢的電腦首次顯示可能接近 1 秒）。</summary>
    private const int CheckIntervalMilliseconds = 50;
    private const int MaxChecks = 30;

    private bool pointerInside;

    public SafeTooltip()
    {
        ComplexAutoCloseAndVisible = true;
    }

    protected override Task OnTriggerMouseEnter()
    {
        pointerInside = true;
        return base.OnTriggerMouseEnter();
    }

    protected override async Task OnTriggerMouseLeave()
    {
        pointerInside = false;
        var wasShown = IsOverlayShow();
        await base.OnTriggerMouseLeave();
        if (wasShown)
        {
            return; // 已顯示完成：AntDesign 自己會收起。
        }

        for (var i = 0; i < MaxChecks; i++)
        {
            await Task.Delay(CheckIntervalMilliseconds);
            if (pointerInside || IsDisposed)
            {
                return;
            }

            if (IsOverlayShow())
            {
                await Close();
                return;
            }
        }
    }
}
