using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 系統巡檢報告的資料存取（0.10.39 起）：存檔、清單、讀取、通知狀態與自動過期。
///
/// 資料表只放中繼資料；內容（JSON，含 AI 說明）一律寫進 <see cref="AdminPatrolReportFileStore"/>（速查表 §6.7）。
/// ⚠️ <b>不分使用者</b>：報告是全體管理員共用的。呼叫端只有排程作業、只限管理員的 API 與系統管理助理的工具，
/// 不要從其他頁面直接呼叫。日誌只記 ReportId，絕不記內容。
/// </summary>
public class SystemReportService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AdminPatrolReportFileStore fileStore;
    private readonly IOptionsMonitor<SystemReportSettings> settings;
    private readonly ILogger<SystemReportService> logger;

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public SystemReportService(
        IDbContextFactory<BackendDBContext> contextFactory,
        AdminPatrolReportFileStore fileStore,
        IOptionsMonitor<SystemReportSettings> settings,
        ILogger<SystemReportService> logger)
    {
        this.contextFactory = contextFactory;
        this.fileStore = fileStore;
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>某一種報告的保留天數（週報、月報各自；日報與指定區間用日報的天數）。</summary>
    public int RetentionDays(string frequency) => settings.CurrentValue.RetentionDays(frequency);

    /// <summary>存一份報告：先寫內容檔再寫資料列；寫資料列失敗就刪掉剛寫的檔。任何一步失敗回 null（已記錯誤）。</summary>
    public async Task<SystemReportSummary?> SaveAsync(SystemReportDraft draft)
    {
        var contentFile = await fileStore.WriteContentAsync(draft.CreatedAt, draft.ReportId, "json", draft.ContentJson);
        if (contentFile is null)
        {
            logger.LogError("Failed to write system report content. ReportId={ReportId}", draft.ReportId);
            return null;
        }

        var item = new SystemReport
        {
            ReportId = draft.ReportId,
            Frequency = draft.Frequency,
            PeriodStart = draft.PeriodStart,
            PeriodEnd = draft.PeriodEnd,
            CreatedAt = draft.CreatedAt,
            Level = draft.Level,
            RedCount = draft.RedCount,
            OrangeCount = draft.OrangeCount,
            YellowCount = draft.YellowCount,
            Trigger = draft.Trigger,
            TriggeredByAccount = draft.TriggeredByAccount,
            JobRunId = draft.JobRunId,
            NarrativeStatus = draft.NarrativeStatus,
            ModelName = draft.ModelName,
            DeliveryStatus = draft.DeliveryStatus,
            ContentFile = contentFile,
        };

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            context.SystemReport.Add(item);
            await context.SaveChangesAsync();
            return ToSummary(item);
        }
        catch (Exception ex)
        {
            fileStore.Delete(contentFile);
            logger.LogError(ex, "Failed to save system report. ReportId={ReportId}", draft.ReportId);
            return null;
        }
    }

    /// <summary>
    /// 同一頻率、同一區間是否已經有排程（含補跑）產生的報告。補跑與排程重疊、或兩台機器各跑一次時，避免重複寄信。
    /// 手動產生的不算。
    /// </summary>
    public async Task<bool> ScheduledReportExistsAsync(string frequency, DateTime periodStart)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.SystemReport.AnyAsync(x => x.Frequency == frequency && x.PeriodStart == periodStart
            && (x.Trigger == SystemReportTriggers.Schedule || x.Trigger == SystemReportTriggers.CatchUp));
    }

    /// <summary>清單（不含內容），最新的在前。</summary>
    public async Task<(IReadOnlyList<SystemReportSummary> Items, int TotalCount)> SearchAsync(SystemReportFilter filter)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var query = context.SystemReport.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Frequency))
        {
            query = query.Where(x => x.Frequency == filter.Frequency);
        }

        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            query = query.Where(x => x.Level == filter.Level);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.CreatedAt >= from.Date);
        }

        if (filter.To is { } to)
        {
            var end = to.Date.AddDays(1);
            query = query.Where(x => x.CreatedAt < end);
        }

        var total = await query.CountAsync();
        var pageIndex = Math.Max(1, filter.PageIndex);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return (items.Select(ToSummary).ToList(), total);
    }

    /// <summary>一份報告與內容；不存在回 null，內容檔不見時內容為 null。</summary>
    public async Task<SystemReportDetail?> GetAsync(Guid reportId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.SystemReport.AsNoTracking().FirstOrDefaultAsync(x => x.ReportId == reportId);
        return item is null ? null : new SystemReportDetail(ToSummary(item), await fileStore.ReadAsync(item.ContentFile));
    }

    /// <summary>更新通知狀態（報告先存、再通知；通知完才知道結果）。失敗只記錯誤。</summary>
    public async Task UpdateDeliveryStatusAsync(Guid reportId, string deliveryStatus)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            await context.SystemReport.Where(x => x.ReportId == reportId)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.DeliveryStatus, deliveryStatus));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update system report delivery status. ReportId={ReportId}", reportId);
        }
    }

    /// <summary>自動過期：刪除產出早於各種報告保留天數（當天 0 點）的報告與內容檔。失敗回 null。</summary>
    public async Task<int?> PurgeExpiredAsync(DateTime nowLocal)
    {
        var expired = Expired(nowLocal);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var stale = context.SystemReport.Where(expired);
            var contentFiles = await stale.Select(x => x.ContentFile).ToListAsync();
            if (contentFiles.Count == 0)
            {
                return 0;
            }

            // 先刪資料列再刪檔：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態。
            var removed = await stale.ExecuteDeleteAsync();
            foreach (var contentFile in contentFiles)
            {
                fileStore.Delete(contentFile);
            }

            fileStore.DeleteEmptyMonthFolders();
            logger.LogInformation("Purged system reports. Rows={Rows}, Now={Now}", removed, nowLocal);
            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to purge system reports.");
            return null;
        }
    }

    /// <summary>某一種報告自動過期的門檻：今天往前保留天數的 0 點（本地時間）。</summary>
    public DateTime ExpiryThreshold(string frequency, DateTime nowLocal) => nowLocal.Date.AddDays(-RetentionDays(frequency));

    /// <summary>筆數、最舊一份與自動過期會刪掉的份數（資料保留頁）。</summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime nowLocal)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.SystemReport.CountAsync();
        var oldest = await context.SystemReport.MinAsync(x => (DateTime?)x.CreatedAt);
        var expired = await context.SystemReport.CountAsync(Expired(nowLocal));
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>
    /// 清理條件：自動過期與統計共用，「下次會清掉幾筆」才會等於實際刪掉的筆數（速查表 §6.29）。
    /// 週報、月報各自的保留天數（0.10.41 起）；日報與指定區間用日報的天數。
    /// </summary>
    private Expression<Func<SystemReport, bool>> Expired(DateTime nowLocal)
    {
        var daily = ExpiryThreshold(SystemReportFrequencies.Daily, nowLocal);
        var weekly = ExpiryThreshold(SystemReportFrequencies.Weekly, nowLocal);
        var monthly = ExpiryThreshold(SystemReportFrequencies.Monthly, nowLocal);
        return x => x.Frequency == SystemReportFrequencies.Weekly ? x.CreatedAt < weekly
            : x.Frequency == SystemReportFrequencies.Monthly ? x.CreatedAt < monthly
            : x.CreatedAt < daily;
    }

    private static SystemReportSummary ToSummary(SystemReport item)
        => new(item.ReportId, item.Frequency, item.PeriodStart, item.PeriodEnd, item.CreatedAt, item.Level,
            item.RedCount, item.OrangeCount, item.YellowCount, item.Trigger, item.TriggeredByAccount,
            item.NarrativeStatus, item.ModelName, item.DeliveryStatus);
}
