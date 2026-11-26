using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.Other;
using MyProject.Web.Auth;

namespace MyProject.Web.Controllers;

/// <summary>
/// Web API 寫稽核（LOG-14）：操作者由這個請求的 JWT 身分經 <see cref="RequestActorResolver"/> 解析。
///
/// 已登入的請求，<see cref="CurrentUserService"/> 由 <c>ApiActorMiddleware</c> 載入，用 <c>Auditing.ViewAudit</c> 結果相同；
/// 這個版本另外適用於登入這類匿名端點（沒有載入目前使用者，只看得到請求本身的身分）。
/// 稽核寫入失敗不拋出（<see cref="AuditLogService"/> 本身即如此），不影響 API 回應，呼叫端不必再包 try/catch。
/// </summary>
public static class ControllerAuditExtensions
{
    public static Task WriteAuditAsync(
        this ControllerBase controller,
        string action,
        string targetType,
        string? targetId,
        string? detail = null,
        bool success = true,
        string? content = null)
    {
        var (account, userId) = RequestActorResolver.Resolve(controller.User);
        var auditLogService = controller.HttpContext.RequestServices.GetRequiredService<IAuditLogService>();

        return auditLogService.WriteAsync(
            action,
            success: success,
            actorUserId: userId,
            actorAccount: account,
            targetType: targetType,
            targetId: targetId,
            detail: detail,
            content: content);
    }
}
