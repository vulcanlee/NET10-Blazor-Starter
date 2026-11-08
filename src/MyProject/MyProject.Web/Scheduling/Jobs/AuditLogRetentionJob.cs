using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 稽核紀錄的保存期限（0.9.78 起 LOG-13，0.9.96 起改由排程執行）。
///
/// 0.9.174 起分兩類清：瀏覽類（換頁、搜尋、查看）讀 <see cref="LogRetentionSettings.AuditBrowsingLogDays"/>（預設 90），
/// 其他（異動與安全事件）讀 <see cref="LogRetentionSettings.AuditLogDays"/>（預設 365）；各自 0＝不清理。
/// 兩類都清過之後，才用較舊的那個門檻收內容檔的月份目錄 —— 用瀏覽類的門檻收，會刪掉還沒過期的修改差異檔。
///
/// ⚠️ 稽核紀錄存 UTC，門檻用 UTC 時鐘算（系統例外紀錄是本地時間，兩者不可對調）。
/// 真的刪到資料才寫 <c>Audit.AutoPurge</c>（每一類一筆，摘要帶 kind），而且寫在刪除之後：稽核軌跡被清理這件事本身也必須查得到。
/// </summary>
public sealed class AuditLogRetentionJob : IScheduledJob
{
    public const string JobName = "AuditLogRetention";

    private readonly AuditLogQueryService auditLogQueryService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<LogRetentionSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AuditLogRetentionJob> logger;

    public AuditLogRetentionJob(
        AuditLogQueryService auditLogQueryService,
        IAuditLogService auditLogService,
        IOptionsMonitor<LogRetentionSettings> options,
        TimeProvider timeProvider,
        ILogger<AuditLogRetentionJob> logger)
    {
        this.auditLogQueryService = auditLogQueryService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var changeDays = settings.AuditLogDays;
        var browsingDays = settings.AuditBrowsingLogDays;
        if (changeDays <= 0 && browsingDays <= 0)
        {
            return ScheduledJobResult.Success("保留天數都設為 0，不自動清除。");
        }

        var changeRemoved = await PurgeAsync(AuditLogKind.Change, changeDays, "change", context);
        var browsingRemoved = await PurgeAsync(AuditLogKind.Browsing, browsingDays, "browsing", context);

        // 兩類都會清時，才用較舊的門檻收月份目錄（空目錄與孤兒檔）；任一類不清理，就沒有「一定可以收」的門檻。
        if (changeDays > 0 && browsingDays > 0)
        {
            auditLogQueryService.DeleteContentMonthsBefore(RetentionCutoffs.AuditLog(Math.Max(changeDays, browsingDays), timeProvider));
        }

        return ScheduledJobResult.Success(
            $"{Describe(changeRemoved, changeDays, "異動與安全紀錄")}；{Describe(browsingRemoved, browsingDays, "瀏覽紀錄")}。");
    }

    private async Task<int> PurgeAsync(AuditLogKind kind, int days, string kindName, ScheduledJobContext context)
    {
        if (days <= 0)
        {
            return 0;
        }

        var removed = await auditLogQueryService.PurgeBeforeAsync(RetentionCutoffs.AuditLog(days, timeProvider), kind);
        logger.LogInformation("Audit log retention completed. Kind={Kind}, Rows={Rows}, RetentionDays={RetentionDays}", kindName, removed, days);

        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.Audit.AutoPurge, success: true,
                targetType: "AuditLog", targetId: "*", detail: $"rows={removed}; days={days}; kind={kindName}; trigger={context.Trigger}");
        }

        return removed;
    }

    private static string Describe(int removed, int days, string what)
        => days > 0 ? $"刪除 {removed} 筆超過 {days} 天的{what}" : $"{what}不自動清除（保留天數為 0）";
}
