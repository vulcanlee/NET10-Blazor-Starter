using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MyProject.Business.Services.DataAccess;

namespace MyProject.Business.Helpers;

/// <summary>
/// 資料保留與清理共用的查詢（0.10.44 起，整併自各資料服務重複的寫法）。
/// 各服務仍各自定義自己的清理條件（<c>OlderThan</c>／<c>Expired</c>，速查表 §6.29.1），傳進來給統計與刪除共用，
/// 「資料保留與清理」頁的「下次會清掉幾筆」才會等於實際刪掉的筆數。
/// </summary>
public static class RetentionQueries
{
    /// <summary>筆數、最舊一筆（<paramref name="timeOf"/>）與符合 <paramref name="expired"/> 的筆數；<paramref name="expired"/> 是 null（不自動清理）時 Expired 為 null。</summary>
    public static async Task<RetentionStats> StatsAsync<T>(
        IQueryable<T> source,
        Expression<Func<T, DateTime?>> timeOf,
        Expression<Func<T, bool>>? expired,
        CancellationToken cancellationToken = default)
    {
        var total = await source.CountAsync(cancellationToken);
        var oldest = await source.MinAsync(timeOf, cancellationToken);
        int? count = expired is null ? null : await source.CountAsync(expired, cancellationToken);
        return new RetentionStats(total, oldest, count);
    }

    /// <summary>
    /// 刪除 <paramref name="stale"/> 的資料列，再逐一刪掉它們的內容檔：先取出檔案路徑 → 刪資料列 → 刪檔。
    /// ⚠️ 順序不可對調（速查表 §6.7）：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態；反過來只會留下孤兒檔，月份清理會收掉。
    /// ⚠️ <b>只適用於「寫入後不再更新清理條件欄位」的資料表</b>（AI 對話紀錄、Token 用量、巡檢報告）：兩次查詢之間不會有資料列
    /// 從「符合」變成「不符合」。會更新時間的資料表（例如例外紀錄重複發生時更新最後發生時間）不可用這裡，否則可能刪掉仍留著的資料列的檔，
    /// 要依主鍵刪除。失敗時拋出，由呼叫端決定怎麼回報。
    /// </summary>
    /// <returns>刪除的資料列數；沒有任何符合的資料時回 null（不刪任何檔）。</returns>
    public static async Task<int?> DeleteRowsThenFilesAsync<T>(
        IQueryable<T> stale, Expression<Func<T, string?>> fileOf, Action<string?> deleteFile)
    {
        var files = await stale.Select(fileOf).ToListAsync();
        if (files.Count == 0)
        {
            return null;
        }

        var removed = await stale.ExecuteDeleteAsync();
        foreach (var file in files)
        {
            deleteFile(file);
        }

        return removed;
    }
}
