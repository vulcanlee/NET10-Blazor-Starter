using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 寄信紀錄的保存期限（0.9.167 起）：建立時間早於 <see cref="EmailDeliverySettings.RetentionDays"/> 天前、
/// 而且不是「待寄」的紀錄分批刪除；0＝不清除。紀錄存 UTC，門檻用 UTC 時鐘算。
/// </summary>
public sealed class EmailLogRetentionJob : IScheduledJob
{
    public const string JobName = "EmailLogRetention";

    private readonly EmailOutboxQueryService queryService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<EmailDeliverySettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EmailLogRetentionJob> logger;

    public EmailLogRetentionJob(
        EmailOutboxQueryService queryService,
        IAuditLogService auditLogService,
        IOptionsMonitor<EmailDeliverySettings> options,
        TimeProvider timeProvider,
        ILogger<EmailLogRetentionJob> logger)
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

        var removed = await queryService.PurgeBeforeAsync(RetentionCutoffs.EmailLog(days, timeProvider), cancellationToken);
        logger.LogInformation("Email log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.EmailLog.AutoPurge, success: true,
                targetType: "EmailOutbox", targetId: "*",
                detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 筆建立超過 {days} 天的寄信紀錄（待寄的不刪）。");
    }
}
