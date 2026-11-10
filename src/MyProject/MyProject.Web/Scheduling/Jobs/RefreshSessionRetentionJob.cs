using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 刷新 Token 紀錄清理（0.10.0 起，WASM 遷移 M1）：刪除已到期的紀錄。
/// 撤銷但尚未到期的紀錄要保留 —— 已被換掉的 Token 若又被拿來用，要靠它們認出是「重用」並撤銷整串。
/// 沒有保留天數可設：紀錄的有效期由 <c>JwtSettings.RefreshTokenDays</c> 決定，到期就沒有留著的理由。
/// </summary>
public sealed class RefreshSessionRetentionJob : IScheduledJob
{
    public const string JobName = "RefreshSessionRetention";

    private readonly IRefreshTokenStore refreshTokenStore;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RefreshSessionRetentionJob> logger;

    public RefreshSessionRetentionJob(
        IRefreshTokenStore refreshTokenStore,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<RefreshSessionRetentionJob> logger)
    {
        this.refreshTokenStore = refreshTokenStore;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var removed = await refreshTokenStore.PurgeExpiredAsync(RetentionCutoffs.RefreshSession(timeProvider), cancellationToken);
        logger.LogInformation("Refresh token retention completed. Rows={Rows}", removed);
        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.RefreshSession.AutoPurge, success: true,
                targetType: "RefreshSession", targetId: "*",
                detail: $"rows={removed}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 筆已到期的登入 Token 紀錄。");
    }
}
