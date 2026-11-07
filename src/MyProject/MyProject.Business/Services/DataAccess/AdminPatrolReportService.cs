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
/// 定時巡檢報告的資料存取（0.9.161 起）：存檔、清單、讀取與自動過期。
///
/// 資料表只放固定用語的中繼資料，報告內文一律寫進 <see cref="AdminPatrolReportFileStore"/>（速查表 §6.7）。
/// ⚠️ <b>不分使用者</b>：巡檢報告是全體管理員共用的。呼叫端只有排程作業與已做過管理員檢查的系統管理助理頁，
/// 不要從其他頁面直接呼叫。日誌只記 ReportId，絕不記內容。
/// </summary>
public class AdminPatrolReportService
{
    /// <summary>清單最多列幾份（最新的在前）。</summary>
    public const int MaxListCount = 100;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AdminPatrolReportFileStore fileStore;
    private readonly IOptionsMonitor<AdminPatrolSettings> settings;
    private readonly ILogger<AdminPatrolReportService> logger;

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public AdminPatrolReportService(
        IDbContextFactory<BackendDBContext> contextFactory,
        AdminPatrolReportFileStore fileStore,
        IOptionsMonitor<AdminPatrolSettings> settings,
        ILogger<AdminPatrolReportService> logger)
    {
        this.contextFactory = contextFactory;
        this.fileStore = fileStore;
        this.settings = settings;
        this.logger = logger;
    }

    public int RetentionDays => settings.CurrentValue.RetentionDays;

    /// <summary>
    /// 存一份報告：先寫內容檔再寫資料列；寫資料列失敗就刪掉剛寫的檔。
    /// </summary>
    /// <returns>存好的清單資料；任何一步失敗回 null（已記錯誤）。</returns>
    public async Task<AdminPatrolReportSummary?> SaveAsync(AdminPatrolReportDraft draft)
    {
        var contentFile = await fileStore.WriteAsync(draft.CreatedAt, draft.ReportId, draft.Markdown);
        if (contentFile is null)
        {
            logger.LogError("Failed to write admin patrol report content. ReportId={ReportId}", draft.ReportId);
            return null;
        }

        var item = new AdminPatrolReport
        {
            ReportId = draft.ReportId,
            CreatedAt = draft.CreatedAt,
            Status = draft.Status,
            AttentionSections = draft.AttentionSections,
            Trigger = draft.Trigger,
            TriggeredByAccount = draft.TriggeredByAccount,
            JobRunId = draft.JobRunId,
            ModelName = draft.ModelName,
            IsTruncated = draft.IsTruncated,
            ContentFile = contentFile,
        };

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            context.AdminPatrolReport.Add(item);
            await context.SaveChangesAsync();
            return ToSummary(item);
        }
        catch (Exception ex)
        {
            fileStore.Delete(contentFile);
            logger.LogError(ex, "Failed to save admin patrol report. ReportId={ReportId}", draft.ReportId);
            return null;
        }
    }

    /// <summary>最新的 <see cref="MaxListCount"/> 份報告（不含內容）。</summary>
    public async Task<IReadOnlyList<AdminPatrolReportSummary>> ListAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var items = await context.AdminPatrolReport.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(MaxListCount)
            .ToListAsync();
        return items.Select(ToSummary).ToList();
    }

    /// <summary>一份報告與內容；不存在回 null，內容檔不見時 Markdown 為 null。</summary>
    public async Task<AdminPatrolReportDetail?> GetAsync(Guid reportId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.AdminPatrolReport.AsNoTracking().FirstOrDefaultAsync(x => x.ReportId == reportId);
        return item is null ? null : new AdminPatrolReportDetail(ToSummary(item), await fileStore.ReadAsync(item.ContentFile));
    }

    /// <summary>自動過期：刪除產出早於 <c>RetentionDays</c> 天前（當天 0 點）的報告與內容檔。失敗回 null。</summary>
    public async Task<int?> PurgeExpiredAsync(DateTime nowLocal)
    {
        var threshold = ExpiryThreshold(nowLocal);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var stale = context.AdminPatrolReport.Where(OlderThan(threshold));
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
            logger.LogInformation("Purged admin patrol reports. Rows={Rows}, Threshold={Threshold}", removed, threshold);
            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to purge admin patrol reports.");
            return null;
        }
    }

    /// <summary>自動過期的門檻：今天往前 <c>RetentionDays</c> 天的 0 點（本地時間，同 Agent 對話）。</summary>
    public DateTime ExpiryThreshold(DateTime nowLocal) => nowLocal.Date.AddDays(-RetentionDays);

    /// <summary>筆數、最舊一份與自動過期會刪掉的份數（資料保留頁）。</summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime nowLocal)
    {
        var threshold = ExpiryThreshold(nowLocal);
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.AdminPatrolReport.CountAsync();
        var oldest = await context.AdminPatrolReport.MinAsync(x => (DateTime?)x.CreatedAt);
        var expired = await context.AdminPatrolReport.CountAsync(OlderThan(threshold));
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>清理條件：自動過期與統計共用，「下次會清掉幾筆」才會等於實際刪掉的筆數。</summary>
    private static Expression<Func<AdminPatrolReport, bool>> OlderThan(DateTime threshold)
        => x => x.CreatedAt < threshold;

    private static AdminPatrolReportSummary ToSummary(AdminPatrolReport item)
        => new(item.ReportId, item.CreatedAt, item.Status, item.AttentionSections, item.Trigger,
            item.TriggeredByAccount, item.ModelName, item.IsTruncated);
}
