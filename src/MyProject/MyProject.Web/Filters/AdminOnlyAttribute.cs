using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Filters;

/// <summary>
/// 只有管理員可以呼叫（0.10.4 起，WASM 遷移 M5）：使用者、角色、稽核紀錄、寄信紀錄這類頁面只看 <c>IsAdmin</c>，
/// 不走角色矩陣（權限鍵刻意不放進矩陣，見 AdminOnlyPermissionTests）。
///
/// 目前使用者由 <c>ApiActorMiddleware</c> 依存取 Token 從資料庫載入，所以剛被取消管理員身分的人下一個請求就會被擋。
/// 沒登入回 401；不是管理員回 403 <see cref="ApiResult"/>，並與 <see cref="HasPermissionAttribute"/> 一樣記一筆 <c>Permission.Denied</c> 稽核。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string DefaultMessage = "只有管理員可以使用這個功能。";

    public AdminOnlyAttribute(string message = DefaultMessage)
    {
        Message = message;
    }

    /// <summary>不是管理員時回給呼叫端的訊息。</summary>
    public string Message { get; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILogger<AdminOnlyAttribute>>();
        var path = context.HttpContext.Request.Path.Value;

        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            logger.LogWarning("Admin-only API request rejected because the caller is not authenticated. Path={Path}", path);
            context.Result = Result(context, StatusCodes.Status401Unauthorized, ApiResult.UnauthorizedResult("尚未登入或憑證無效。"));
            return;
        }

        var user = services.GetRequiredService<CurrentUserService>().CurrentUser;
        if (user.IsAdmin)
        {
            return;
        }

        logger.LogWarning("Admin-only API request denied. UserId={UserId}, Account={Account}, Path={Path}", user.Id, user.Account, path);
        try
        {
            await services.GetRequiredService<IAuditLogService>().WriteAsync(
                AuditActions.Permission.Denied,
                success: false,
                actorUserId: user.Id > 0 ? user.Id : null,
                actorAccount: user.Id > 0 ? user.Account : null,
                targetType: "ApiEndpoint",
                targetId: path,
                detail: "需要管理員");
        }
        catch (Exception ex)
        {
            // 稽核寫入失敗不影響「已拒絕」這個結果。
            logger.LogError(ex, "Failed to write admin-only denied audit log. UserId={UserId}", user.Id);
        }

        context.Result = Result(context, StatusCodes.Status403Forbidden, ApiResult.ForbiddenResult(Message));
    }

    private static ObjectResult Result(AuthorizationFilterContext context, int status, ApiResult<object> body)
    {
        body.TraceId = context.HttpContext.TraceIdentifier;
        return new ObjectResult(body) { StatusCode = status };
    }
}

/// <summary>
/// 只要登入就能呼叫（0.10.4 起）：不屬於任何頁面權限的端點，例如自己的個人資料、外框、通知、登出。
/// 本身沒有行為（登入由 <c>[Authorize]</c> 擋），只是明確標出「這裡刻意不檢查頁面權限」，讓守門測試分得出「忘了標」與「本來就不需要」。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SignedInUserAttribute : Attribute
{
}
