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
/// 這組 API 是 WASM 前端與日後 App 共用的登入入口；網頁專用的登入、兩步驟驗證、Google 兌換、新分頁取得 Token 在 <see cref="WebAuthController"/>（0.10.8 起）。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
public class AuthController : ControllerBase
{
    private const string InvalidRefreshMessage = "Refresh Token 無效或已過期。";

    private readonly MyUserServiceLogin userServiceLogin;
    private readonly IJwtTokenService jwtTokenService;
    private readonly IRefreshTokenStore refreshTokenStore;
    private readonly ITwoFactorService twoFactorService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly WebSessionTokens webSessionTokens;
    private readonly JwtSettings jwtSettings;
    private readonly ILogger<AuthController> logger;

    public AuthController(
        MyUserServiceLogin userServiceLogin,
        IJwtTokenService jwtTokenService,
        IRefreshTokenStore refreshTokenStore,
        ITwoFactorService twoFactorService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        WebSessionTokens webSessionTokens,
        IOptions<JwtSettings> jwtOptions,
        ILogger<AuthController> logger)
    {
        this.userServiceLogin = userServiceLogin;
        this.jwtTokenService = jwtTokenService;
        this.refreshTokenStore = refreshTokenStore;
        this.twoFactorService = twoFactorService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.webSessionTokens = webSessionTokens;
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
        var refresh = await refreshTokenStore.IssueAsync(user, isPersistent: true, RefreshClientKinds.Api, loginSessionId: null, WebSessionTokens.DeviceLabel(Request), RefreshLifetime);
        var tokenResponse = jwtTokenService.CreateTokenResponse(user, refresh, isPersistent: true);
        logger.LogInformation("API login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "登入成功"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Refresh([FromBody] RefreshTokenRequestDto request)
    {
        // 新 Token 的效期依這一串的發出管道（0.10.8 起網頁管道依「記住我」，見 WebSessionTokens）。
        var result = await refreshTokenStore.RotateAsync(request.RefreshToken, webSessionTokens.Lifetime);
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

                // 與 Server 版相同再記一筆「其他地方變更了帳號，被登出」（0.10.8 獨立審查）：個人資料頁的登入紀錄靠它（以操作者篩選），
                // Server 版在 Blazor 電路發現版本換掉時寫，純 Token 之後在這裡（前端換不到新的 Token、被要求重新登入）寫。
                if (result.UserId is { } revokedUserId)
                {
                    await auditLogService.WriteAsync(
                        AuditActions.Login.SessionExpired, success: false, actorUserId: revokedUserId, actorAccount: null, detail: "reason=SessionRevoked");
                }

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
    /// 登出（0.10.0 起）：網頁登入（0.10.8 起）撤銷這次登入的每一串（每個分頁各一串，以登入識別串起來），其他管道撤銷這一串。
    /// 存取 Token 帶串的識別，驗證時發現已撤銷就拒絕，已發出的存取 Token 也立即失效。
    /// 不論 Token 認不認得都回成功，避免被拿來試探 Token 是否有效。
    /// 稽核：網頁登入與 Server 版登出頁相同（只記操作者）；API 登入另記 <c>via=api</c>。
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [AllowDuringForcedSetup]
    public async Task<ActionResult<ApiResult<object>>> Logout([FromBody] RefreshTokenRequestDto request)
    {
        var (userId, clientKind) = await refreshTokenStore.RevokeLoginAsync(request.RefreshToken, "Logout");
        if (userId is not null)
        {
            if (RefreshClientKinds.IsBrowser(clientKind))
            {
                var account = (await userServiceLogin.GetActiveUserAsync(userId.Value))?.Account;
                await auditLogService.WriteAsync(AuditActions.Logout, success: true, actorUserId: userId, actorAccount: account);
            }
            else
            {
                await auditLogService.WriteAsync(AuditActions.Logout, success: true, actorUserId: userId, targetType: nameof(MyUser), targetId: userId.ToString(), detail: "via=api");
            }

            logger.LogInformation("Logout. UserId={UserId}, ClientKind={ClientKind}", userId, clientKind);
        }

        return Ok(ApiResult<object>.SuccessResult(new { }, "已登出"));
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
}
