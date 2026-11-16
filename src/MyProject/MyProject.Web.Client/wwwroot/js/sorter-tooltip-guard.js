// 表格標題列的排序提示滑鼠移開後不殘留（0.10.32 起）。
// AntDesign 1.6.3 的提示框在「顯示到一半」時滑鼠就離開，會因為還沒顯示完成而跳過收起，之後也不再檢查，提示框就停在畫面上
// （快速掃過標題列時偶爾發生；實機以 scripts/e2e/ux-tooltips.mjs 重現）。我們自己的提示框改用 SafeTooltip 處理，
// 但排序提示（「按一下依遞增排序」等）是 AntDesign 表格內部的提示框，換不掉 —— 它以 JS 監聽標題（.ant-table-column-sorters）的 mouseout，
// 所以這裡在滑鼠真的離開某個標題後短暫檢查：畫面上還有提示框、滑鼠也沒回到該標題時，對該標題補送一次 mouseout，
// 此時提示框已顯示完成，AntDesign 會正常收起；那個標題的提示框若沒顯示，補送不會有任何作用。
// ⚠️ 排在 index.html 的 ant-design-blazor.js 之後（SafeTooltipConventionTests 守門）；外部檔案，因為 CSP 不允許行內腳本。
(() => {
    const sorterSelector = '.ant-table-column-sorters';
    const checkIntervalMs = 200;
    const maxChecks = 8;

    const anyTooltipVisible = () => [...document.querySelectorAll('.ant-tooltip')].some((el) => {
        if (el.classList.contains('ant-tooltip-hidden')) {
            return false;
        }
        const style = getComputedStyle(el);
        return style.display !== 'none' && style.visibility !== 'hidden';
    });

    document.addEventListener('mouseout', (e) => {
        if (!e.isTrusted || !(e.target instanceof Element)) {
            return; // 不理會自己補送的事件
        }
        const sorter = e.target.closest(sorterSelector);
        if (!sorter || (e.relatedTarget instanceof Node && sorter.contains(e.relatedTarget))) {
            return; // 不是標題，或只是在同一個標題裡移動
        }

        let checks = 0;
        const check = () => {
            if (!sorter.isConnected || sorter.matches(':hover')) {
                return;
            }
            if (anyTooltipVisible()) {
                sorter.dispatchEvent(new MouseEvent('mouseout', { bubbles: true }));
            }
            if (++checks < maxChecks) {
                setTimeout(check, checkIntervalMs);
            }
        };
        setTimeout(check, checkIntervalMs);
    }, true);
})();
