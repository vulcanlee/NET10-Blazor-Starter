using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 系統例外紀錄的資料存取。
///
/// ⚠️ <b>本服務位於例外記錄管線之內。</b><see cref="RecordAsync"/> 由背景寫入器呼叫，
/// 它自己的失敗絕不可再經由 <see cref="ILogger"/> 回報，否則會形成
/// 「寫入失敗 → 記錯誤 → 再寫入 → 再失敗」的無限遞迴。查詢與刪除路徑由畫面呼叫，
/// 不在管線內，可以正常記錄。
/// </summary>
public class ExceptionLogService
{
    /// <summary>
    /// 相異例外的列數上限。帶參數的例外訊息（例如「no such column: X」）可能衝出大量不同列，
    /// 達上限後改記哨兵列，資料庫就不會被塞爆。
    /// </summary>
    public const int MaxRows = 5000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ExceptionStackFileStore fileStore;

    public IMapper Mapper { get; }
    public ILogger<ExceptionLogService> Logger { get; }

    public ExceptionLogService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<ExceptionLogService> logger,
        ExceptionStackFileStore fileStore)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.fileStore = fileStore;
    }

    /// <summary>
    /// 記錄一筆例外：相同簽章就累加次數，否則新增一列並寫入堆疊檔。
    ///
    /// ⚠️ 由背景寫入器呼叫，全程不得拋出、不得走 ILogger（見類別註解）。
    /// </summary>
    public async Task RecordAsync(ExceptionLogEntry entry)
    {
        try
        {
            await RecordCoreAsync(entry);
        }
        catch (Exception)
        {
            // 刻意吞掉。例外記錄本身失敗不該影響系統，更不該回流管線造成遞迴。
        }
    }

    private async Task RecordCoreAsync(ExceptionLogEntry entry)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var message = ExceptionSignature.TruncateMessage(entry.Message);
        var signature = ExceptionSignature.Compute(entry.ExceptionType, message, entry.Page, entry.Operation);

        var existing = await context.ExceptionLog.FirstOrDefaultAsync(x => x.Signature == signature);
        if (existing is not null)
        {
            existing.OccurrenceCount++;
            existing.LastOccurredAt = entry.OccurredAt;
            await context.SaveChangesAsync();
            return;
        }

        // 達列數上限：不再新增相異列，改累加哨兵列。
        if (await context.ExceptionLog.CountAsync() >= MaxRows)
        {
            await RecordOverflowAsync(context, entry.OccurredAt);
            return;
        }

        // 堆疊檔在建立資料列之前寫，失敗只是少了全文，資料列仍必須建立。
        var stackFile = await fileStore.WriteAsync(signature, entry.OccurredAt, entry.StackTrace);

        var item = new ExceptionLog
        {
            Signature = signature,
            ExceptionType = entry.ExceptionType,
            Message = message,
            Source = entry.Source,
            Page = entry.Page,
            Operation = entry.Operation,
            LoggerName = entry.LoggerName,
            Account = entry.Account,
            UserId = entry.UserId,
            StackTraceFile = stackFile,
            OccurrenceCount = 1,
            FirstOccurredAt = entry.OccurredAt,
            LastOccurredAt = entry.OccurredAt,
        };

        try
        {
            await context.ExceptionLog.AddAsync(item);
            await context.SaveChangesAsync();
        }
        catch (Exception)
        {
            // 資料列沒建起來，剛才那個堆疊檔就是孤兒 —— 一律清掉。
            // ⚠️ 刻意攔所有例外而非只攔 DbUpdateException：任何原因造成資料列沒進去，
            // 檔案都必須一起收乾淨，否則目錄會慢慢累積對不到紀錄的孤兒檔。
            fileStore.Delete(stackFile);

            await using var retryContext = await contextFactory.CreateDbContextAsync();
            var conflicting = await retryContext.ExceptionLog.FirstOrDefaultAsync(x => x.Signature == signature);
            if (conflicting is not null)
            {
                conflicting.OccurrenceCount++;
                conflicting.LastOccurredAt = entry.OccurredAt;
                await retryContext.SaveChangesAsync();
            }
        }
    }

    private static async Task RecordOverflowAsync(BackendDBContext context, DateTime occurredAt)
    {
        var overflow = await context.ExceptionLog
            .FirstOrDefaultAsync(x => x.Signature == ExceptionSignature.OverflowSignature);

        if (overflow is null)
        {
            await context.ExceptionLog.AddAsync(new ExceptionLog
            {
                Signature = ExceptionSignature.OverflowSignature,
                ExceptionType = "(其他)",
                Message = $"相異例外已達列數上限（{MaxRows}），之後的新例外一律併入本列。請清除舊紀錄後再觀察。",
                Source = ExceptionSources.Unknown,
                OccurrenceCount = 1,
                FirstOccurredAt = occurredAt,
                LastOccurredAt = occurredAt,
            });
        }
        else
        {
            overflow.OccurrenceCount++;
            overflow.LastOccurredAt = occurredAt;
        }

        await context.SaveChangesAsync();
    }

    public async Task<DataRequestResult<ExceptionLogAdapterModel>> GetAsync(ExceptionLogQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading exception logs. Source={Source}, CurrentPage={CurrentPage}, PageSize={PageSize}",
            query.Source,
            query.CurrentPage,
            query.PageSize);

        DataRequestResult<ExceptionLogAdapterModel> result = new();
        IQueryable<ExceptionLog> dataSource = context.ExceptionLog.AsNoTracking();

        if (query.StartTime.HasValue)
        {
            dataSource = dataSource.Where(x => x.LastOccurredAt >= query.StartTime.Value);
        }

        if (query.EndTime.HasValue)
        {
            dataSource = dataSource.Where(x => x.LastOccurredAt <= query.EndTime.Value);
        }

        if (string.IsNullOrWhiteSpace(query.Source) == false)
        {
            dataSource = dataSource.Where(x => x.Source == query.Source);
        }

        if (string.IsNullOrWhiteSpace(query.Account) == false)
        {
            dataSource = dataSource.Where(x => x.Account != null && x.Account.Contains(query.Account));
        }

        if (string.IsNullOrWhiteSpace(query.Keyword) == false)
        {
            dataSource = dataSource.Where(x =>
                x.ExceptionType.Contains(query.Keyword) ||
                x.Message.Contains(query.Keyword) ||
                (x.Page != null && x.Page.Contains(query.Keyword)) ||
                (x.Operation != null && x.Operation.Contains(query.Keyword)));
        }

        IOrderedQueryable<ExceptionLog>? sorted = null;

        if (string.IsNullOrWhiteSpace(query.SortField) == false)
        {
            if (query.SortField == nameof(ExceptionLogAdapterModel.OccurrenceCount))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.OccurrenceCount).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.OccurrenceCount).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(ExceptionLogAdapterModel.LastOccurredAt))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.LastOccurredAt).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.LastOccurredAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (query.SortField == nameof(ExceptionLogAdapterModel.FirstOccurredAt))
            {
                sorted = query.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.FirstOccurredAt).ThenByDescending(x => x.Id)
                    : query.SortDescending == false
                        ? dataSource.OrderBy(x => x.FirstOccurredAt).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 之前一定要有 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.LastOccurredAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource
            .Skip((query.CurrentPage - 1) * query.PageSize)
            .Take(query.PageSize);

        List<ExceptionLog> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<ExceptionLogAdapterModel>>(records);
        return result;
    }

    /// <summary>讀取堆疊全文。檔案不存在時回 null，由畫面顯示友善訊息。</summary>
    public async Task<string?> GetStackTraceAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.ExceptionLog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        return item is null ? null : await fileStore.ReadAsync(item.StackTraceFile);
    }

    /// <summary>刪除單一列，同時刪除其堆疊檔。</summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting exception log. ExceptionLogId={ExceptionLogId}", id);

        try
        {
            var item = await context.ExceptionLog.FirstOrDefaultAsync(x => x.Id == id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的例外紀錄。");
            }

            var stackFile = item.StackTraceFile;
            context.ExceptionLog.Remove(item);
            await context.SaveChangesAsync();

            fileStore.Delete(stackFile);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete exception log. ExceptionLogId={ExceptionLogId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除例外紀錄失敗。", ex);
        }
    }

    /// <summary>清空全部紀錄與整個堆疊目錄。</summary>
    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Clearing all exception logs.");

        try
        {
            var removed = await context.ExceptionLog.ExecuteDeleteAsync();
            fileStore.DeleteAll();

            Logger.LogInformation("Cleared all exception logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear exception logs.");
            return VerifyRecordResultFactory.Build(false, "清空例外紀錄失敗。", ex);
        }
    }

    /// <summary>刪除「最後發生」早於 N 天前的紀錄，同時刪除其堆疊檔。</summary>
    public async Task<VerifyRecordResult> PurgeAsync(int days)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var threshold = DateTime.Now.AddDays(-days);
        Logger.LogInformation("Purging exception logs older than {Days} days.", days);

        try
        {
            var stale = await context.ExceptionLog
                .Where(x => x.LastOccurredAt < threshold)
                .ToListAsync();

            if (stale.Count == 0)
            {
                return VerifyRecordResultFactory.Build(true, "沒有符合條件的紀錄。");
            }

            // 先刪資料列再刪檔：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態。
            var stackFiles = stale.Select(x => x.StackTraceFile).ToList();
            context.ExceptionLog.RemoveRange(stale);
            await context.SaveChangesAsync();

            foreach (var stackFile in stackFiles)
            {
                fileStore.Delete(stackFile);
            }

            Logger.LogInformation("Purged exception logs. Rows={Rows}, Days={Days}", stale.Count, days);
            return VerifyRecordResultFactory.Build(true, $"已清除 {stale.Count} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge exception logs. Days={Days}", days);
            return VerifyRecordResultFactory.Build(false, "清除舊例外紀錄失敗。", ex);
        }
    }
}
