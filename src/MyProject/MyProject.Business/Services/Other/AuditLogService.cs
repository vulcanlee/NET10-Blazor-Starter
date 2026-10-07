using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;

namespace MyProject.Business.Services.Other;

public sealed class AuditLogService : IAuditLogService
{
    private readonly BackendDBContext context;
    private readonly ILogger<AuditLogService> logger;
    private readonly IClientIpProvider? clientIpProvider;

    /// <param name="clientIpProvider">
    /// 來源 IP 在這裡集中取得，各呼叫點不必傳；未註冊（單元測試）時不記 IP。
    /// </param>
    public AuditLogService(BackendDBContext context, ILogger<AuditLogService> logger, IClientIpProvider? clientIpProvider = null)
    {
        this.context = context;
        this.logger = logger;
        this.clientIpProvider = clientIpProvider;
    }

    public async Task WriteAsync(
        string action,
        bool success = true,
        int? actorUserId = null,
        string? actorAccount = null,
        string? targetType = null,
        string? targetId = null,
        string? detail = null)
    {
        try
        {
            var entry = new AuditLog
            {
                OccurredAt = DateTime.UtcNow,
                Action = action,
                Success = success,
                ActorUserId = actorUserId,
                ActorAccount = actorAccount,
                TargetType = targetType,
                TargetId = targetId,
                Detail = detail,
                ClientIp = clientIpProvider?.GetClientIp(),
            };

            await context.AuditLog.AddAsync(entry);
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit log. Action={Action}", action);
        }
    }
}
