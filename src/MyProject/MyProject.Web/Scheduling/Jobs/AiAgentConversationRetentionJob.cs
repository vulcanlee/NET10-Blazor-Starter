using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// Agent 對話的自動過期（0.9.143 起）：最後一次對話早於 <see cref="AiAgentConversationSettings.RetentionDays"/> 天前的對話連同內容檔刪除
/// （1～3650，沒有「不清除」）。內容檔由 <c>AiAgentConversationService</c> 經 <c>AiAgentConversationFileStore</c> 刪除。
/// 服務失敗時會自己記錄錯誤並回 null，這裡回報失敗即可，不要再丟例外（同 <see cref="AiCallLogRetentionJob"/>）。
/// </summary>
public sealed class AiAgentConversationRetentionJob : IScheduledJob
{
    public const string JobName = "AiAgentConversationRetention";

    private readonly AiAgentConversationService conversationService;
    private readonly IAuditLogService auditLogService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AiAgentConversationRetentionJob> logger;

    public AiAgentConversationRetentionJob(
        AiAgentConversationService conversationService,
        IAuditLogService auditLogService,
        TimeProvider timeProvider,
        ILogger<AiAgentConversationRetentionJob> logger)
    {
        this.conversationService = conversationService;
        this.auditLogService = auditLogService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var days = conversationService.RetentionDays;
        var removed = await conversationService.PurgeExpiredAsync(timeProvider.GetLocalNow().DateTime);
        if (removed is null)
        {
            return ScheduledJobResult.Failure("清除 Agent 對話失敗，詳細內容請看「系統例外紀錄」。");
        }

        logger.LogInformation("AI agent conversation retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
        if (removed > 0)
        {
            await auditLogService.WriteAsync(
                AuditActions.AiAgentConversation.AutoPurge, success: true,
                targetType: "AiAgentConversation", targetId: "*", detail: $"rows={removed}; days={days}; trigger={context.Trigger}");
        }

        return ScheduledJobResult.Success($"刪除 {removed} 段最後一次對話超過 {days} 天的 Agent 對話。");
    }
}
