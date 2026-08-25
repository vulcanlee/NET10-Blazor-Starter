using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;

namespace MyProject.Web.Filters;

/// <summary>
/// API 功能級授權：要求目前使用者具備指定權限鍵，否則回傳 ApiResult 403。
/// 與 UI 共用 <see cref="IPermissionChecker"/> 作為權限判定來源。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string permissionKey;

    /// <summary>頁面級（裸鍵）授權。</summary>
    public HasPermissionAttribute(string permissionKey)
    {
        this.permissionKey = permissionKey;
    }

    /// <summary>動作級授權，權限鍵為「頁面:動作」。</summary>
    public HasPermissionAttribute(string page, string action)
    {
        this.permissionKey = PermissionKey.For(page, action);
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var logger = services.GetRequiredService<ILogger<HasPermissionAttribute>>();
        var path = context.HttpContext.Request.Path.Value;

        var principal = context.HttpContext.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            // 匿名請求在這裡就被擋掉，不會進到下面的權限檢查 ——
            // 因此稽核寫入只會發生在已登入的使用者身上，匿名流量無法灌爆稽核表。
            logger.LogWarning(
                "API request rejected because the caller is not authenticated. PermissionKey={PermissionKey}, Path={Path}",
                permissionKey, path);

            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("尚未登入或憑證無效。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idValue, out var userId))
        {
            logger.LogWarning(
                "API request rejected because the user identifier could not be parsed. PermissionKey={PermissionKey}, Path={Path}",
                permissionKey, path);

            context.Result = new ObjectResult(ApiResult.UnauthorizedResult("無法識別使用者身分。"))
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var checker = services.GetRequiredService<IPermissionChecker>();
        if (!await checker.HasPermissionAsync(userId, permissionKey))
        {
            var account = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

            // 權限被拒屬於「不尋常」的事件：正常使用者不會去打自己沒有權限的端點。
            logger.LogWarning(
                "API request denied by permission check. UserId={UserId}, Account={Account}, PermissionKey={PermissionKey}, Path={Path}",
                userId, account, permissionKey, path);

            await WriteAuditAsync(services, logger, userId, account, path);

            context.Result = new ObjectResult(ApiResult.ForbiddenResult($"目前使用者沒有「{permissionKey}」權限。"))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        logger.LogDebug(
            "API request passed permission check. UserId={UserId}, PermissionKey={PermissionKey}, Path={Path}",
            userId, permissionKey, path);
    }

    /// <summary>
    /// 稽核寫入失敗不應影響「請求已被拒絕」這個結果，因此只記錯誤不向外拋。
    /// </summary>
    private async Task WriteAuditAsync(
        IServiceProvider services, ILogger logger, int userId, string account, string? path)
    {
        try
        {
            var auditLogService = services.GetRequiredService<IAuditLogService>();
            await auditLogService.WriteAsync(
                "Permission.Denied",
                success: false,
                actorUserId: userId,
                actorAccount: account,
                targetType: "ApiEndpoint",
                targetId: path,
                detail: $"缺少權限：{permissionKey}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write permission-denied audit log. UserId={UserId}", userId);
        }
    }
}
