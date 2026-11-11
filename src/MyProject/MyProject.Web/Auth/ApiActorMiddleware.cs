using System.Security.Claims;
using System.Text.Json;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;

namespace MyProject.Web.Auth;

/// <summary>
/// 以存取 Token 呼叫 API 時，每個請求從資料庫載入「目前使用者」並填入 <see cref="CurrentUserService"/>（0.10.0 起，WASM 遷移 M1）。
///
/// Business 服務一律從 <see cref="CurrentUserService"/> 判斷「誰在操作」（稽核、團隊範圍、自我保護），由這裡在每個請求填入。
/// 載入規則在 <see cref="ICurrentUserLoader"/>：帳號不存在、停用或工作階段版本已換時回 401 <c>ApiResult</c>。
/// 0.10.8 起網頁管道的 Token（瀏覽器裡的 WASM 前端）另擋「沒有主要角色」的帳號（403＋<see cref="ApiErrorCodes.NoPrimaryRole"/>，
/// 沒有主要角色不能用網頁）；以 API 登入的 Token（App）照舊可用。
///
/// ⚠️ 必須放在 <c>UseAuthorization</c> 之後：JWT 不是預設驗證機制，端點的 User 要到授權中介軟體才驗證出來。
/// 只處理存取 Token（<c>token_type=access</c>）；沒帶 Token 的請求不經過這裡。
/// </summary>
public sealed class ApiActorMiddleware
{
    private const string SessionItemKey = "MyProject.ApiSession";
    private readonly RequestDelegate next;

    public ApiActorMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    /// <summary>這個請求載入的工作階段；不是以存取 Token 呼叫時為 null。</summary>
    public static CurrentUserLoadResult? GetSession(HttpContext context)
        => context.Items.TryGetValue(SessionItemKey, out var value) ? value as CurrentUserLoadResult : null;

    public async Task InvokeAsync(HttpContext context, ICurrentUserLoader loader, CurrentUserService currentUserService, ILogger<ApiActorMiddleware> logger)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true
            || principal.FindFirstValue(JwtTokenService.TokenTypeClaimType) != JwtTokenService.AccessTokenType)
        {
            await next(context);
            return;
        }

        if (!int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) || userId <= 0)
        {
            await RejectAsync(context);
            return;
        }

        var loaded = await loader.LoadAsync(userId, principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType));
        if (loaded.Status != CurrentUserLoadStatus.Ok || loaded.CurrentUser is null)
        {
            logger.LogInformation("API request rejected because the session is no longer valid. UserId={UserId}, Status={Status}", userId, loaded.Status);
            await RejectAsync(context);
            return;
        }

        if (!loaded.HasPrimaryRole && RefreshClientKinds.IsBrowser(principal.FindFirstValue(JwtTokenService.ClientKindClaimType)))
        {
            logger.LogWarning("Web API request rejected because the account has no primary role. UserId={UserId}", userId);
            await RejectNoPrimaryRoleAsync(context);
            return;
        }

        CurrentUserLoader.ApplyTo(currentUserService, loaded.CurrentUser);
        context.Items[SessionItemKey] = loaded;
        await next(context);
    }

    private static async Task RejectNoPrimaryRoleAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        var result = ApiResult.ForbiddenResult("此帳號沒有主要角色，無法使用網頁。請聯絡系統管理員。");
        result.ErrorCode = ApiErrorCodes.NoPrimaryRole;
        result.TraceId = context.TraceIdentifier;
        await context.Response.WriteAsync(JsonSerializer.Serialize(result));
    }

    private static async Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        var result = ApiResult.UnauthorizedResult("工作階段已失效，請重新登入。");
        result.TraceId = context.TraceIdentifier;
        await context.Response.WriteAsync(JsonSerializer.Serialize(result));
    }
}
