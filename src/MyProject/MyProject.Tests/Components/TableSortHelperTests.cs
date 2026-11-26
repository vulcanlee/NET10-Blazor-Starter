using AntDesign;
using AntDesign.TableModels;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// <see cref="TableSortHelper.ResolveSort"/>（0.10.45 起，整併 9 個清單頁與 CRUD 樣板相同的 if／else）：
/// 結果必須和整併前各頁的寫法相同 —— 沒有排序模型 → ("", "None")；有 → 第一個有方向的欄位（都沒有方向時取最後一個）與方向文字。
/// </summary>
public sealed class TableSortHelperTests
{
    [Fact]
    public void NoSortModels_ShouldMeanNoSorting()
    {
        Assert.Equal((string.Empty, "None"), TableSortHelper.ResolveSort(null));
        Assert.Equal((string.Empty, "None"), TableSortHelper.ResolveSort([]));
    }

    [Fact]
    public void SortedColumn_ShouldWinOverUnsortedOnes()
    {
        var result = TableSortHelper.ResolveSort([FakeSort.Create("Name", SortDirection.None), FakeSort.Create("CreatedAt", SortDirection.Descending)]);

        Assert.Equal(("CreatedAt", SortDirection.Descending.ToString()), result);
    }

    [Fact]
    public void NothingSorted_ShouldFallBackToTheLastColumn()
    {
        var result = TableSortHelper.ResolveSort([FakeSort.Create("Name", SortDirection.None), FakeSort.Create("Code", SortDirection.None)]);

        Assert.Equal(("Code", SortDirection.None.ToString()), result);
    }

    /// <summary>AntDesign 的排序模型（換頁／排序事件帶的就是它）；只用到欄位名稱與方向。</summary>
    private static class FakeSort
    {
        public static ITableSortModel Create(string fieldName, SortDirection direction) => new SortModel<string>(0, 0, fieldName, direction);
    }
}
