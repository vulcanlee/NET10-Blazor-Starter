using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 容量快照的資料存取（0.10.39 起）：每天一份、讀歷史給系統巡檢報告預估成長、自動過期。
/// 只存固定代碼與數字，沒有個人資料。
/// </summary>
public class CapacitySnapshotService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IOptionsMonitor<SystemReportSettings> settings;
    private readonly ILogger<CapacitySnapshotService> logger;

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public CapacitySnapshotService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IOptionsMonitor<SystemReportSettings> settings,
        ILogger<CapacitySnapshotService> logger)
    {
        this.contextFactory = contextFactory;
        this.settings = settings;
        this.logger = logger;
    }

    public int RetentionDays => settings.CurrentValue.CapacitySnapshotRetentionDays;

    /// <summary>
    /// 存一天的快照：同一天已經有的整批換掉（同一個交易），所以重跑、手動執行都只會留一份。
    /// </summary>
    public async Task SaveDayAsync(DateTime snapshotDate, DateTime capturedAt, IReadOnlyCollection<CapacitySnapshotRow> rows)
    {
        var date = snapshotDate.Date;
        await using var context = await contextFactory.CreateDbContextAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.CapacitySnapshot.Where(x => x.SnapshotDate == date).ExecuteDeleteAsync();
        context.CapacitySnapshot.AddRange(rows
            .GroupBy(x => (x.Category, x.Key))
            .Select(g => g.Last())
            .Select(x => new CapacitySnapshot
            {
                SnapshotDate = date,
                CapturedAt = capturedAt,
                Category = x.Category,
                Key = x.Key,
                UsedBytes = x.UsedBytes,
                TotalBytes = x.TotalBytes,
                FreeBytes = x.FreeBytes,
                ItemCount = x.ItemCount,
                Status = x.Status,
            }));
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        logger.LogInformation("Capacity snapshot saved. SnapshotDate={SnapshotDate}, Rows={Rows}", date, rows.Count);
    }

    public async Task<bool> HasDayAsync(DateTime snapshotDate)
    {
        var date = snapshotDate.Date;
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.CapacitySnapshot.AnyAsync(x => x.SnapshotDate == date);
    }

    /// <summary>從 <paramref name="fromDate"/>（含）起的快照，依日期排序。</summary>
    public async Task<IReadOnlyList<CapacitySnapshotRow>> GetHistoryAsync(DateTime fromDate)
    {
        var from = fromDate.Date;
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.CapacitySnapshot.AsNoTracking()
            .Where(x => x.SnapshotDate >= from)
            .OrderBy(x => x.SnapshotDate)
            .Select(x => new CapacitySnapshotRow(x.SnapshotDate, x.Category, x.Key, x.UsedBytes, x.TotalBytes, x.FreeBytes, x.ItemCount, x.Status))
            .ToListAsync();
    }

    /// <summary>自動過期：刪除早於保留天數的快照。失敗回 null。</summary>
    public async Task<int?> PurgeExpiredAsync(DateTime nowLocal)
    {
        var threshold = ExpiryThreshold(nowLocal);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var removed = await context.CapacitySnapshot.Where(OlderThan(threshold)).ExecuteDeleteAsync();
            if (removed > 0)
            {
                logger.LogInformation("Purged capacity snapshots. Rows={Rows}, Threshold={Threshold}", removed, threshold);
            }

            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to purge capacity snapshots.");
            return null;
        }
    }

    public DateTime ExpiryThreshold(DateTime nowLocal) => nowLocal.Date.AddDays(-RetentionDays);

    /// <summary>筆數、最舊一筆與自動過期會刪掉的筆數（資料保留頁）。</summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime nowLocal)
    {
        var threshold = ExpiryThreshold(nowLocal);
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.CapacitySnapshot.CountAsync();
        var oldest = await context.CapacitySnapshot.MinAsync(x => (DateTime?)x.SnapshotDate);
        var expired = await context.CapacitySnapshot.CountAsync(OlderThan(threshold));
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>清理條件：自動過期與統計共用（速查表 §6.29）。</summary>
    private static Expression<Func<CapacitySnapshot, bool>> OlderThan(DateTime threshold)
        => x => x.SnapshotDate < threshold;
}
