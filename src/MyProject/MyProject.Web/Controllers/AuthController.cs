using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 登入、刷新、登出與目前工作階段（0.10.0 起刷新 Token 改存伺服器端，可輪替與撤銷，見 <see cref="IRefreshTokenStore"/>）。
/// 這組 API 是 WASM 前端與日後 App 共用的登入入口。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
public class AuthController : ControllerBase
{
    /// <summary>
    /// <c>bridge</c> 必須帶這個標頭（值不拘）。跨站請求要帶自訂標頭就得先通過 CORS 預檢，而 CORS 預設不允許任何來源，
    /// 所以別的網站無法利用使用者的 Cookie 替他換出 Token（CSRF 防護）。
    /// </summary>
    public const string ClientHeaderName = "X-MyProject-Client";

    private const string InvalidRefreshMessage = "Refresh Token 無效或已過期。";
    private const int DeviceLabelMaxLength = 200;

    private readonly MyUserServiceLogin userServiceLogin;
    private readonly IJwtTokenService jwtTokenService;
    private readonly IRefreshTokenStore refreshTokenStore;
    private readonly ITwoFactorService twoFactorService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly JwtSettings jwtSettings;
    private readonly ILogger<AuthController> logger;

    public AuthController(
        MyUserServiceLogin userServiceLogin,
        IJwtTokenService jwtTokenService,
        IRefreshTokenStore refreshTokenStore,
        ITwoFactorService twoFactorService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        IOptions<JwtSettings> jwtOptions,
        ILogger<AuthController> logger)
    {
        this.userServiceLogin = userServiceLogin;
        this.jwtTokenService = jwtTokenService;
        this.refreshTokenStore = refreshTokenStore;
        this.twoFactorService = twoFactorService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        jwtSettings = jwtOptions.Value;
        this.logger = logger;
    }

    private TimeSpan RefreshLifetime => TimeSpan.FromDays(jwtSettings.RefreshTokenDays);

    [HttpPost("login")]
    [AllowAnonymous]
    // 註：登入的較嚴格配額由 "api" policy 依路徑判斷（見 AddConfiguredRateLimiting）。
    // 這裡刻意**不用** [EnableRateLimiting("login")]：端點慣例
    // MapControllers().RequireRateLimiting("api") 套用時機晚於屬性，會把它蓋掉而靜默失效。
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Login([FromBody] LoginRequestDto request)
    {
        var attempt = await userServiceLogin.LoginAsync(request.Account, request.Password);
        var (message, user) = attempt;

        // 已啟用兩步驟驗證（0.9.104 起）：API 也要第二步，不能留後門。沒帶驗證碼只提示、不計入失敗次數。
        if (attempt.RequiresTwoFactor && attempt.User is { } pendingUser)
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                logger.LogInformation("API login requires the second factor. UserId={UserId}", pendingUser.Id);
                return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("需要兩步驟驗證碼。"));
            }

            (message, user) = await userServiceLogin.CompleteSecondFactorAsync(pendingUser.Id, request.TwoFactorCode);
        }

        if (user is null)
        {
            logger.LogWarning("API login failed. Account={Account}", request.Account);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult(message));
        }

        // 必須使用兩步驟驗證卻還沒設定：網頁會帶去設定頁，API 沒有地方設定，直接拒絕（否則等於可以用 API 繞過強制）。
        if (!user.TwoFactorEnabled && await twoFactorService.IsRequiredAsync(user.Id))
        {
            logger.LogInformation("API login refused because two-factor setup is required. UserId={UserId}", user.Id);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("請先在網頁完成兩步驟驗證設定。"));
        }

        // API 登入沒有「記住我」：以 API 登入的用戶端（App、整合程式）本來就自行保存 Token。
        var refresh = await refreshTokenStore.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, loginSessionId: null, DeviceLabel(), RefreshLifetime);
        var tokenResponse = jwtTokenService.CreateTokenResponse(user, refresh, isPersistent: true);
        logger.LogInformation("API login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "登入成功"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Refresh([FromBody] RefreshTokenRequestDto request)
    {
        var result = await refreshTokenStore.RotateAsync(request.RefreshToken, RefreshLifetime);
        switch (result.Status)
        {
            case RefreshRotationStatus.Rotated:
                var tokenResponse = jwtTokenService.CreateTokenResponse(result.User!, result.Token!, result.IsPersistent);
                return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "Token 更新成功"));

            // 剛被換掉的 Token 在寬限期內又被用（同時刷新、回應遺失後重送）：不撤銷，用戶端改用另一邊換到的新 Token。
            case RefreshRotationStatus.Superseded:
                logger.LogInformation("Refresh rejected because the token was just rotated by another request. UserId={UserId}", result.UserId);
                await this.WriteAuditAsync(AuditActions.Token.RefreshFailed, "MyUser", result.UserId?.ToString(), "reason=Superseded", success: false);
                break;

            // 已被換掉的舊 Token 在寬限期過後又被使用：可能外洩，整串已撤銷。
            case RefreshRotationStatus.Reused:
                logger.LogWarning("Refresh rejected because a rotated token was reused. UserId={UserId}", result.UserId);
                await this.WriteAuditAsync(AuditActions.Token.ReuseDetected, "MyUser", result.UserId?.ToString(), "reason=Reused", success: false);
                break;

            // 工作階段已失效（改密碼、停用、角色變更、強制登出之後，0.9.103 起）：舊的刷新 Token 不能再換發。
            case RefreshRotationStatus.SessionRevoked:
                logger.LogInformation("Refresh rejected because the session was revoked. UserId={UserId}", result.UserId);
                await this.WriteAuditAsync(AuditActions.Token.RefreshFailed, "MyUser", result.UserId?.ToString(), "reason=SessionRevoked", success: false);
                break;

            case RefreshRotationStatus.UserInactive:
                logger.LogWarning("Refresh rejected because the user no longer exists or is disabled. UserId={UserId}", result.UserId);
                await this.WriteAuditAsync(AuditActions.Token.RefreshFailed, "MyUser", result.UserId?.ToString(), "reason=UserInactive", success: false);
                break;

            default:
                // 只記失敗原因的類別，Token 本身不進稽核與日誌。
                logger.LogInformation("Refresh rejected because the token is unknown, expired or revoked. UserId={UserId}", result.UserId);
                await this.WriteAuditAsync(
                    AuditActions.Token.RefreshFailed, result.UserId is null ? "Token" : "MyUser", result.UserId?.ToString(), "reason=InvalidToken", success: false);
                break;
        }

        return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult(InvalidRefreshMessage));
    }

    /// <summary>
    /// 登出：撤銷這個刷新 Token 所屬的整串（0.10.0 起）。已發出的存取 Token 仍可用到到期為止，前端應同時丟棄。
    /// 不論 Token 認不認得都回成功，避免被拿來試探 Token 是否有效。
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [AllowDuringForcedSetup]
    public async Task<ActionResult<ApiResult<object>>> Logout([FromBody] RefreshTokenRequestDto request)
    {
        var userId = await refreshTokenStore.RevokeFamilyAsync(request.RefreshToken, "Logout");
        if (userId is not null)
        {
            await auditLogService.WriteAsync(AuditActions.Logout, success: true, actorUserId: userId, targetType: nameof(MyUser), targetId: userId.ToString(), detail: "via=api");
            logger.LogInformation("API logout. UserId={UserId}", userId);
        }

        return Ok(ApiResult<object>.SuccessResult(new { }, "已登出"));
    }

    /// <summary>
    /// 網頁 Cookie 換 Token（WASM 遷移過渡期，0.10.0 起）：已用網頁登入的使用者打開 WASM 頁面時，用 Cookie 換一組 Token，
    /// 之後只用 Token 呼叫 API。「記住我」沿用 Cookie 的設定，回應的 <see cref="TokenResponseDto.IsPersistent"/> 告訴前端要存哪裡。
    /// 純 Token 登入上線（遷移 M9）後移除。
    /// </summary>
    [HttpPost("bridge")]
    [AllowAnonymous]
    [AllowDuringForcedSetup]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Bridge()
    {
        if (!Request.Headers.ContainsKey(ClientHeaderName))
        {
            logger.LogWarning("Bridge rejected because the client header is missing.");
            return BadRequest(ApiResult<TokenResponseDto>.FailureResult("請求缺少必要的標頭。"));
        }

        // Cookie 驗證器（SecurityStampCookieEvents）在這裡一併檢查工作階段版本，被撤銷的 Cookie 不會成功。
        var cookie = await HttpContext.AuthenticateAsync(MagicObjectHelper.CookieScheme);
        if (!cookie.Succeeded || cookie.Principal is not { } principal
            || !int.TryParse(principal.FindFirstValue(ClaimTypes.Sid), out var userId)
            || await userServiceLogin.GetActiveUserAsync(userId) is not { } user)
        {
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("請先登入。"));
        }

        // ⚠️ Cookie 驗證器比對的是快取（別的行程換的版本、或快取競態時可能還是舊值），這裡再以資料庫比一次：
        // 用資料庫的新版本替過時的 Cookie 發 Token，等於讓一個剛被撤銷的登入換出一串躲過撤銷的 Token。
        if (!SecurityStamps.Matches(principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType), user.SecurityStamp))
        {
            logger.LogInformation("Bridge rejected because the cookie's session was revoked. UserId={UserId}", user.Id);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("請先登入。"));
        }

        var properties = cookie.Properties ?? new AuthenticationProperties();
        var loginSessionId = principal.FindFirstValue(CookieClaims.LoginSessionClaimType);
        if (string.IsNullOrEmpty(loginSessionId))
        {
            // 0.10.0 之前發出的 Cookie 沒有登入識別：補上後以原本的屬性（含「記住我」與到期時間）重新簽發，登出時才撤銷得到。
            loginSessionId = Guid.NewGuid().ToString("N");
            var identity = new ClaimsIdentity(principal.Claims, MagicObjectHelper.CookieScheme);
            identity.AddClaim(new Claim(CookieClaims.LoginSessionClaimType, loginSessionId));
            await HttpContext.SignInAsync(MagicObjectHelper.CookieScheme, new ClaimsPrincipal(identity), properties);
        }

        // Token 不可比 Cookie 活得久：Cookie 閒置逾時或「記住我」期限一到，WASM 端也不能再靠刷新延長登入。
        var refresh = await refreshTokenStore.IssueAsync(
            user, properties.IsPersistent, RefreshClientKinds.WebBridge, loginSessionId, DeviceLabel(), RefreshLifetime, properties.ExpiresUtc?.UtcDateTime);
        logger.LogInformation("Web session bridged to tokens. UserId={UserId}, Persistent={Persistent}", user.Id, properties.IsPersistent);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(jwtTokenService.CreateTokenResponse(user, refresh, properties.IsPersistent), "已取得 Token"));
    }

    /// <summary>
    /// 目前工作階段（0.10.0 起每次從資料庫讀，由 <see cref="ApiActorMiddleware"/> 載入）：身分、有效權限與團隊，
    /// 以及是否必須先改密碼或設定兩步驟驗證。強制設定期間仍可呼叫。
    /// </summary>
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [SignedInUser]
    [AllowDuringForcedSetup]
    public ActionResult<ApiResult<SessionInfoDto>> Me()
    {
        var session = ApiActorMiddleware.GetSession(HttpContext);
        if (session?.CurrentUser is null)
        {
            return Unauthorized(ApiResult<SessionInfoDto>.UnauthorizedResult("未提供有效的 Bearer token。"));
        }

        var current = currentUserService.CurrentUser;
        var info = new SessionInfoDto
        {
            Id = current.Id,
            Account = current.Account,
            Name = current.Name,
            Email = current.Email,
            IsAdmin = current.IsAdmin,
            Permissions = current.RoleList.ToList(),
            Teams = current.TeamList.ToList(),
            MustChangePassword = session.MustChangePassword,
            RequiresTwoFactorSetup = session.RequiresTwoFactorSetup,
            TwoFactorEnabled = session.User?.TwoFactorEnabled == true,
        };

        return Ok(ApiResult<SessionInfoDto>.SuccessResult(info, "取得目前使用者成功"));
    }

    private string? DeviceLabel()
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        return userAgent.Length > DeviceLabelMaxLength ? userAgent[..DeviceLabelMaxLength] : userAgent;
    }
}
