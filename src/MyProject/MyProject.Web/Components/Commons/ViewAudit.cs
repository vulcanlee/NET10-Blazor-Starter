using MyProject.Business.Services.Other;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 檢視層寫稽核的共用樣板（LOG-14）：操作者一律取自 <see cref="CurrentUserService"/>。
///
/// 只在動作<b>成功之後</b>呼叫，Detail 只放非敏感摘要（筆數、天數、匯出格式），不放查詢內容或匯出內容。
/// <see cref="IAuditLogService"/> 寫入失敗只記 Warning、不拋出 —— 不推翻「已經完成」的動作。
/// </summary>
public static class ViewAudit
{
    public static Task WriteAsync(
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        string action,
        string targetType,
        string? targetId,
        string? detail)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: targetType,
            targetId: targetId,
            detail: detail);
    }
}
