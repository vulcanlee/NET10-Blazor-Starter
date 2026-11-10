using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MyProject.Dtos.Commons;
using MyProject.Web.Auth;

namespace MyProject.Web.Filters;

/// <summary>
/// 強制改密碼／強制設定兩步驟驗證期間，以存取 Token 呼叫的 API 一律回 403（0.10.0 起，全域篩選器）。
/// 網頁版靠換頁時導到設定頁做到這件事；API 沒有頁面可導，以前只要拿到 Token 就能繞過，這裡補上伺服器端的界線。
/// 回應帶 <see cref="ApiResult{T}.ErrorCode"/>，前端與 App 據此帶使用者去完成設定；
/// 完成設定所需的端點標 <see cref="AllowDuringForcedSetupAttribute"/> 放行。
/// </summary>
public sealed class ApiSessionGateFilter : IAuthorizationFilter
{
    private readonly ILogger<ApiSessionGateFilter> logger;

    public ApiSessionGateFilter(ILogger<ApiSessionGateFilter> logger)
    {
        this.logger = logger;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var session = ApiActorMiddleware.GetSession(context.HttpContext);
        if (session is null || (!session.MustChangePassword && !session.RequiresTwoFactorSetup))
        {
            return;
        }

        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowDuringForcedSetupAttribute>().Any())
        {
            return;
        }

        var result = session.MustChangePassword
            ? ApiResult.ForbiddenResult("請先變更密碼。")
            : ApiResult.ForbiddenResult("請先設定兩步驟驗證。");
        result.ErrorCode = session.MustChangePassword ? ApiErrorCodes.PasswordChangeRequired : ApiErrorCodes.TwoFactorSetupRequired;
        result.TraceId = context.HttpContext.TraceIdentifier;
        logger.LogInformation(
            "API request blocked until forced setup is completed. UserId={UserId}, ErrorCode={ErrorCode}, Path={Path}",
            session.User?.Id, result.ErrorCode, context.HttpContext.Request.Path.Value);
        context.Result = new ObjectResult(result) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
