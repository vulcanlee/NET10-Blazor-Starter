using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 巡檢報告與容量快照的自動過期（0.9.161 起，預設每天 03:00；0.10.39 起名稱改為「巡檢報告與容量快照清理」，作業名稱不變）：
/// <list type="bullet">
/// <item>系統巡檢報告與舊版巡檢報告：產出早於 <see cref="SystemReportSettings.DailyRetentionDays"/> 天前的連同內容檔刪除（1～3650，沒有「不清除」）。</item>
/// <item>容量快照：早於 <see cref="SystemReportSettings.CapacitySnapshotRetentionDays"/> 天前的刪除。</item>
/// </list>
/// 服務失敗時會自己記錄錯誤並回 null，這裡回報失敗即可（同 <see cref="AiAgentConversationRetentionJob"/>）。
/// </summary>
public sealed class AdminPatrolReportRetentionJob : IScheduledJob
{
    public const string JobName = "AdminPatrolReportRetention";

    private readonly AdminPatrolReportService legacyReportService;
    private readonly SystemReportService reportService;
    private readonly CapacitySnapshotService snapshotService;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AdminPatrolReportRetentionJob> logger;

    public AdminPatrolReportRetentionJob(
        AdminPatrolReportService legacyReportService,
        SystemReportService reportService,
        CapacitySnapshotService snapshotService,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<AdminPatrolReportRetentionJob> logger)
    {
        this.legacyReportService = legacyReportService;
        this.reportService = reportService;
        this.snapshotService = snapshotService;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetLocalNow().DateTime;
        var days = reportService.DailyRetentionDays;
        var legacy = await legacyReportService.PurgeExpiredAsync(now);
        var reports = await reportService.PurgeExpiredAsync(now);
        var snapshotDays = snapshotService.RetentionDays;
        var snapshots = await snapshotService.PurgeExpiredAsync(now);
        if (legacy is null || reports is null || snapshots is null)
        {
            return ScheduledJobResult.Failure("清除巡檢報告或容量快照失敗，詳細內容請看「系統例外紀錄」。");
        }

        logger.LogInformation(
            "System report retention completed. Reports={Reports}, LegacyReports={LegacyReports}, Snapshots={Snapshots}, RetentionDays={RetentionDays}",
            reports, legacy, snapshots, days);
        if (legacy > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.AdminPatrol.AutoPurge, success: true,
                targetType: "AdminPatrolReport", targetId: "*", detail: $"rows={legacy}; days={days}; trigger={context.Trigger}");
        }

        if (reports > 0 || snapshots > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.SystemReport.AutoPurge, success: true,
                targetType: "SystemReport", targetId: "*",
                detail: $"reports={reports}; days={days}; snapshots={snapshots}; snapshotDays={snapshotDays}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success(
            $"刪除 {reports + legacy} 份超過 {days} 天的巡檢報告、{snapshots} 筆超過 {snapshotDays} 天的容量快照。");
    }
}
