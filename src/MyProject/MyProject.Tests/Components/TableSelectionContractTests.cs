using AntDesign;
using AntDesign.TableModels;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace MyProject.Tests.Components;

/// <summary>
/// 鎖住「刪除選取」（0.9.132 起）依賴的 AntDesign Table 行為，套件升級改了行為時這裡會先紅：
/// <list type="bullet">
/// <item>表頭全選只選目前資料、略過 <c>Disabled</c> 的列（使用者頁的自己與 support、預設角色）。</item>
/// <item>各頁在 <c>ReloadAsync</c> 把綁定的 <c>SelectedRows</c> 設為空、再換 <c>DataSource</c>，勾選就會清掉。</item>
/// <item>畫面上分頁的表格（公告）：全選只選目前這一頁；換頁時以 <c>SetSelection</c>（空集合）清掉勾選。</item>
/// <item>例外紀錄自訂的勾選格（擋掉列點擊）照樣會更新綁定的勾選。</item>
/// </list>
/// AntDesign 回報勾選（<c>SelectedRowsChanged</c>）不一定在點擊的同一輪完成，斷言一律用 <c>WaitForAssertion</c>；
/// 要驗「清空」時先等前一步的勾選確實回報，否則晚到的回報會在斷言之後才把勾選填回去。
/// </summary>
public sealed class TableSelectionContractTests : ComponentTestBase
{
    public sealed record Row(int Id, string Name, bool Locked);

    private IEnumerable<Row> selected = [];

    private IRenderedComponent<Table<Row>> RenderTable(List<Row> rows, int pageSize = 10, bool customCell = false)
        => Render<Table<Row>>(p => p
            .Add(x => x.DataSource, rows)
            .Add(x => x.PageSize, pageSize)
            .Add(x => x.SelectedRows, selected)
            .Add(x => x.SelectedRowsChanged, EventCallback.Factory.Create<IEnumerable<Row>>(this, v => selected = v))
            .Add(x => x.ChildContent, row => builder =>
            {
                builder.OpenComponent<Selection>(0);
                builder.AddComponentParameter(1, nameof(Selection.Disabled), row.Locked);
                if (customCell)
                {
                    builder.AddComponentParameter(2, nameof(Selection.CellRender), CustomCell());
                }

                builder.CloseComponent();
            }));

    /// <summary>同例外紀錄頁的勾選格：外包一層（頁面上是擋掉列點擊的 span），裡面自己放 Checkbox。</summary>
    private RenderFragment<CellData> CustomCell()
        => cell => b =>
        {
            b.OpenElement(0, "span");
            b.AddAttribute(1, "class", "custom-cell");
            b.OpenComponent<Checkbox>(2);
            b.AddComponentParameter(3, nameof(Checkbox.Checked), cell.RowData.Selected);
            b.AddComponentParameter(4, nameof(Checkbox.CheckedChanged),
                EventCallback.Factory.Create<bool>(this, v => cell.RowData.Selected = v));
            b.CloseComponent();
            b.CloseElement();
        };

    [Fact]
    public void SelectAll_ShouldSkipDisabledRows()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "自己", true), new(3, "丙", false) };
        var table = RenderTable(rows);

        table.Find("thead .ant-checkbox-input").Change(true);

        table.WaitForAssertion(() => Assert.Equal([1, 3], selected.Select(x => x.Id).Order()));
    }

    [Fact]
    public void SelectAll_WithClientSidePaging_ShouldOnlySelectTheCurrentPage()
    {
        var rows = Enumerable.Range(1, 3).Select(i => new Row(i, $"公告{i}", false)).ToList();
        var table = RenderTable(rows, pageSize: 2);

        table.Find("thead .ant-checkbox-input").Change(true);

        table.WaitForAssertion(() => Assert.Equal([1, 2], selected.Select(x => x.Id).Order()));
    }

    [Fact]
    public async Task SetSelectionWithEmpty_ShouldClearAndNotify()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "乙", false) };
        var table = RenderTable(rows);
        table.Find("thead .ant-checkbox-input").Change(true);
        table.WaitForAssertion(() => Assert.Equal(2, selected.Count()));

        await table.InvokeAsync(() => table.Instance.SetSelection(Array.Empty<Row>()));

        table.WaitForAssertion(() => Assert.Empty(selected));
    }

    [Fact]
    public void CustomCheckboxCell_ShouldUpdateTheBoundSelection()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "乙", false) };
        var table = RenderTable(rows, customCell: true);

        table.FindAll("tbody .custom-cell .ant-checkbox-input")[1].Change(true);

        table.WaitForAssertion(() => Assert.Equal([2], selected.Select(x => x.Id)));
    }

    /// <summary>同 RequirePermission：同步判斷、有權限就渲染子內容。</summary>
    public sealed class Wrapper : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
            => builder.AddContent(0, ChildContent);
    }

    /// <summary>勾選欄＋一般欄＋右側固定的操作欄（各列表頁的形狀）。</summary>
    private IRenderedComponent<Table<Row>> RenderWithFixedActionColumn(int rowCount, bool wrapSelection)
        => Render<Table<Row>>(p => p
            .Add(x => x.DataSource, Enumerable.Range(1, rowCount).Select(i => new Row(i, $"列{i}", false)).ToList())
            .Add(x => x.ChildContent, row => builder =>
            {
                if (wrapSelection)
                {
                    builder.OpenComponent<Wrapper>(0);
                    builder.AddComponentParameter(1, nameof(Wrapper.ChildContent), (RenderFragment)(b =>
                    {
                        b.OpenComponent<Selection>(0);
                        b.CloseComponent();
                    }));
                    builder.CloseComponent();
                }
                else
                {
                    builder.OpenComponent<Selection>(2);
                    builder.CloseComponent();
                }

                builder.OpenComponent<Column<string>>(3);
                builder.AddComponentParameter(4, nameof(Column<string>.Title), "名稱");
                builder.AddComponentParameter(5, nameof(Column<string>.DataIndex), nameof(Row.Name));
                builder.CloseComponent();

                builder.OpenComponent<ActionColumn>(6);
                builder.AddComponentParameter(7, nameof(ActionColumn.Title), "操作");
                builder.AddComponentParameter(8, nameof(ActionColumn.Fixed), (ColumnFixPlacement?)ColumnFixPlacement.Right);
                builder.CloseComponent();
            }));

    /// <summary>各頁的寫法（0.9.150 起）：勾選欄直接放在表格裡（權限用 @if），資料再多也正常。</summary>
    [Fact]
    public void SelectionDirectlyInTable_WithRightFixedColumn_ShouldRenderManyRows()
    {
        var table = RenderWithFixedActionColumn(10, wrapSelection: false);

        table.WaitForAssertion(() => Assert.Equal(10, table.FindAll("tbody tr.ant-table-row").Count));
    }

    /// <summary>
    /// ⚠️ 為什麼勾選欄不能包在 RequirePermission 裡（0.9.150 實際故障：專案清單頁整頁空白）：
    /// 包起來後第 3 列起欄位序號錯位，右側固定的操作欄計算 sticky 位置時讀到沒寬度的欄 →
    /// <c>CssSizeLength</c> 丟 ArgumentNullException。2 列以內不會發生，所以資料少時看不出來。
    /// AntDesign 修好之後這個測試會紅 —— 屆時可以放寬 <c>BatchDeleteConventionTests</c> 的限制。
    /// </summary>
    [Fact]
    public void SelectionWrappedInComponent_WithRightFixedColumn_ShouldThrowFromThirdRow()
    {
        var twoRows = RenderWithFixedActionColumn(2, wrapSelection: true);
        Assert.Equal(2, twoRows.FindAll("tbody tr.ant-table-row").Count);

        Assert.ThrowsAny<ArgumentNullException>(() => RenderWithFixedActionColumn(3, wrapSelection: true));
    }

    [Fact]
    public void ResettingSelectedRows_WithNewData_ShouldClearTheCheckboxes()
    {
        var rows = new List<Row> { new(1, "甲", false), new(2, "乙", false) };
        var table = RenderTable(rows);
        table.Find("thead .ant-checkbox-input").Change(true);
        table.WaitForAssertion(() => Assert.Equal(2, selected.Count()));

        selected = [];
        table.Render(p => p
            .Add(x => x.SelectedRows, selected)
            .Add(x => x.DataSource, new List<Row>(rows)));

        table.WaitForAssertion(() =>
        {
            Assert.Empty(table.FindAll("tbody .ant-checkbox-checked"));
            Assert.Empty(selected);
        });
    }
}
