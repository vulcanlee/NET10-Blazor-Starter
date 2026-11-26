using System.Linq.Expressions;

namespace MyProject.Business.Helpers;

/// <summary>
/// 清單排序（0.10.44 起，整併自日誌類服務重複的三段式寫法）。
/// ⚠️ 同值時一定要有第二排序鍵（Id）：SQLite 不保證相同值的順序，分頁會重複或漏資料。
/// CRUD 產生器產生的服務（分類、團隊、專案、使用者、角色）維持樣板的寫法，不改用這裡。
/// </summary>
public static class QueryableSorting
{
    /// <summary>
    /// 依 <paramref name="key"/> 排序、同值依 <paramref name="id"/>：<paramref name="descending"/> 為 true 遞減、false 遞增，
    /// null（畫面沒有指定方向）回 null，由呼叫端套用預設排序。
    /// </summary>
    public static IOrderedQueryable<T>? SortWithIdTieBreak<T, TKey>(
        this IQueryable<T> source, Expression<Func<T, TKey>> key, Expression<Func<T, int>> id, bool? descending)
        => descending switch
        {
            true => source.OrderByDescending(key).ThenByDescending(id),
            false => source.OrderBy(key).ThenBy(id),
            null => null,
        };
}
