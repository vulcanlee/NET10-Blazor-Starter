using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 定時巡檢報告的自動過期（0.9.161 起，預設每天 03:00）：產出早於 <see cref="AdminPatrolSettings.RetentionDays"/> 天前的報告
/// 連同內容檔刪除（1～3650，沒有「不清除」）。服務失敗時會自己記錄錯誤並回 null，這裡回報失敗即可（同 <see cref="AiAgentConversationRetentionJob"/>）。
/// </summary>
public sealed class AdminPatrolReportRetentionJob : IScheduledJob
{
    public const string JobName = "AdminPatrolReportRetention";

    private readonly AdminPatrolReportService reportService;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AdminPatrolReportRetentionJob> logger;

    public AdminPatrolReportRetentionJob(
        AdminPatrolReportService reportService,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<AdminPatrolReportRetentionJob> logger)
    {
        this.reportService = reportService;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = reportService.RetentionDays;
        var removed = await reportService.PurgeExpiredAsync(timeProvider.GetLocalNow().DateTime);
        if (removed is null)
        {
            return ScheduledJobResult.Failure("清除巡檢報告失敗，詳細內容請看「系統例外紀錄」。");
        }

        logger.LogInformation("Admin patrol report retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.AdminPatrol.AutoPurge, success: true,
                targetType: "AdminPatrolReport", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 份超過 {days} 天的巡檢報告。");
    }
}
