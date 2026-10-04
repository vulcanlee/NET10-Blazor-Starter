using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 站內通知的保存期限（0.9.100 起）：建立時間早於 <see cref="NotificationSettings.RetentionDays"/> 天前的通知（不論已讀未讀）分批刪除；0＝不清除。
/// 通知存 UTC，門檻用 UTC 時鐘算。
/// </summary>
public sealed class NotificationRetentionJob : IScheduledJob
{
    public const string JobName = "NotificationRetention";

    private readonly NotificationQueryService queryService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<NotificationSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationRetentionJob> logger;

    public NotificationRetentionJob(
        NotificationQueryService queryService,
        IAuditLogService auditLogService,
        IOptionsMonitor<NotificationSettings> options,
        TimeProvider timeProvider,
        ILogger<NotificationRetentionJob> logger)
    {
        this.queryService = queryService;
        this.auditLogService = auditLogService;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = options.CurrentValue.RetentionDays;
        if (days <= 0)
        {
            return ScheduledJobResult.Success("保留天數設為 0，不自動清除。");
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        var removed = await queryService.PurgeBeforeAsync(cutoff, cancellationToken);
        logger.LogInformation("Notification retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.Notification.AutoPurge, success: true,
                targetType: "Notification", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 則超過 {days} 天的站內通知。");
    }
}
