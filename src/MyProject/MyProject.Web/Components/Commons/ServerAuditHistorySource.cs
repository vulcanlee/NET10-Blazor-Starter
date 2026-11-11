using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// Server 頁面的異動歷程資料來源（0.10.2 起從 <see cref="AuditHistoryDialog"/> 抽出，邏輯不變）：直接查資料庫，
/// 操作者是電路的 <see cref="CurrentUserService"/>。WASM 頁面改走 <c>/api/v1/audit-logs</c>（同一套稽核寫法，見 <c>AuditLogsController</c>）。
/// </summary>
public sealed class ServerAuditHistorySource : IAuditHistorySource
{
    private readonly AuditLogQueryService queryService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<ServerAuditHistorySource> logger;

    public ServerAuditHistorySource(
        AuditLogQueryService queryService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<ServerAuditHistorySource> logger)
    {
        this.queryService = queryService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    public async Task<AuditHistoryPage> LoadAsync(string targetType, string targetId, int pageIndex, int pageSize, bool opening)
    {
        var result = await queryService.GetTargetHistoryAsync(targetType, targetId, pageIndex, pageSize);
        if (opening)
        {
            await WriteOpenedAuditAsync(auditLogService, currentUserService, targetType, targetId);
        }

        return new AuditHistoryPage(result.Result.ToList(), result.Count);
    }

    public async Task<string> ReadContentAsync(AuditLogAdapterModel item)
    {
        var text = string.Empty;
        if (item.ContentFile is not null)
        {
            try
            {
                text = await queryService.GetContentAsync(item.Id) ?? MissingContentText;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read audit content file. AuditLogId={AuditLogId}", item.Id);
                text = "讀取詳細內容失敗。" + TraceCode.Suffix(TraceCode.Current);
            }
        }

        await WriteViewedAuditAsync(auditLogService, currentUserService, item);
        return text;
    }

    internal const string MissingContentText = "內容檔不存在（可能已被清除，或當初寫檔失敗）。";

    /// <summary>打開異動歷程：一筆稽核紀錄的「搜尋」（與 API 共用寫法）。</summary>
    internal static Task WriteOpenedAuditAsync(IAuditLogService auditLogService, CurrentUserService currentUserService, string targetType, string targetId)
        => ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Audit.Search, "AuditLog",
            AuditSearchCondition.Of("targetType", targetType), AuditSearchCondition.Of("targetId", targetId),
            AuditSearchCondition.Of("via", "history"));

    /// <summary>點開一筆：一筆稽核紀錄的「查看」（與 API 共用寫法）。</summary>
    internal static Task WriteViewedAuditAsync(IAuditLogService auditLogService, CurrentUserService currentUserService, AuditLogAdapterModel item)
        => ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Audit.View, "AuditLog", item.Id.ToString(), $"action={item.Action}; via=history");
}
