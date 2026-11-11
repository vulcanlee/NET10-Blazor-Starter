using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Auth;
using MyProject.Web.Components.Auths;
using MyProject.Web.Configuration;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 網頁以 Token 登入（0.10.8 起，WASM 遷移 M9）：登入頁設定、網頁登入（記住我、記住裝置）、兩步驟驗證、Google 一次性代碼兌換、新分頁取得 Token。
/// 規則與稽核都沿用 Server 版登入頁用的 <see cref="MyUserServiceLogin"/>；效期見 <see cref="WebSessionTokens"/>。
/// App 與整合程式的入口仍是 <see cref="AuthController"/> 的 <c>login</c>（契約不變）。
/// ⚠️ 除了 <c>login-settings</c>，這裡的端點都算進較嚴格的登入配額（<c>RateLimitSettings:LoginRequestsPerMinute</c>，見 <c>IsLoginRequest</c>）。
/// </summary>
[Route("api/v1/Auth")]
[ApiController]
[ApiValidationFilter]
[AllowAnonymous]
public sealed class WebAuthController : ControllerBase
{
    internal const string TicketExpiredMessage = "登入已逾時，請重新登入。";
    internal const string GoogleExpiredMessage = "Google 登入已逾時，請重新登入。";
    private const string InvalidRefreshMessage = "Refresh Token 無效或已過期。";

    private readonly MyUserServiceLogin userServiceLogin;
    private readonly WebSessionTokens webSessionTokens;
    private readonly IRefreshTokenStore refreshTokenStore;
    private readonly TwoFactorLoginTickets twoFactorTickets;
    private readonly GoogleLoginCodes googleLoginCodes;
    private readonly ISecurityStampService securityStampService;
    private readonly IAuditLogService auditLogService;
    private readonly ILogger<WebAuthController> logger;

    public WebAuthController(
        MyUserServiceLogin userServiceLogin,
        WebSessionTokens webSessionTokens,
        IRefreshTokenStore refreshTokenStore,
        TwoFactorLoginTickets twoFactorTickets,
        GoogleLoginCodes googleLoginCodes,
        ISecurityStampService securityStampService,
        IAuditLogService auditLogService,
        ILogger<WebAuthController> logger)
    {
        this.userServiceLogin = userServiceLogin;
        this.webSessionTokens = webSessionTokens;
        this.refreshTokenStore = refreshTokenStore;
        this.twoFactorTickets = twoFactorTickets;
        this.googleLoginCodes = googleLoginCodes;
        this.securityStampService = securityStampService;
        this.auditLogService = auditLogService;
        this.logger = logger;
    }

    /// <summary>登入頁、忘記／重設密碼頁、啟動頁要顯示的公開設定（不算進登入配額：每次開頁都會呼叫，而且沒有可猜的東西）。</summary>
    [HttpGet("login-settings")]
    public ActionResult<ApiResult<LoginPageSettingsDto>> LoginSettings(
        [FromServices] ISystemIdentity systemIdentity,
        [FromServices] IOptions<GoogleOAuthSettings> googleOptions,
        [FromServices] IOptions<EmailSettings> emailOptions,
        [FromServices] IOptionsMonitor<LockoutSettings> lockoutOptions,
        [FromServices] IOptionsMonitor<SessionSettings> sessionOptions,
        [FromServices] IOptionsMonitor<PasswordResetSettings> resetOptions,
        [FromServices] IPasswordPolicy passwordPolicy)
    {
        var lockout = lockoutOptions.CurrentValue;
        var settings = new LoginPageSettingsDto
        {
            SystemName = systemIdentity.Name,
            SystemDescription = systemIdentity.Description,
            GoogleEnabled = googleOptions.Value.IsConfigured,
            EmailEnabled = emailOptions.Value.TryGetProvider(out var provider) && provider != EmailProvider.None,
            MaxFailedAttempts = lockout.MaxFailedAttempts,
            LockoutMinutes = lockout.LockoutMinutes,
            RememberDeviceDays = twoFactorTickets.RememberDeviceDays,
            RememberMeDays = sessionOptions.CurrentValue.RememberMeDays,
            PasswordRule = passwordPolicy.Describe(),
            ResetLinkLifetimeMinutes = resetOptions.CurrentValue.TokenLifetimeMinutes,
        };
        return Ok(ApiResult<LoginPageSettingsDto>.SuccessResult(settings, "取得登入設定成功"));
    }

    /// <summary>
    /// 網頁登入：帳號密碼對了就發網頁管道的 Token（依「記住我」決定效期）。已啟用兩步驟驗證時，記住過的裝置直接完成，否則回待驗票證。
    /// 必須設定兩步驟驗證卻還沒設定的人照樣拿到 Token，由 <see cref="ApiSessionGateFilter"/> 擋業務 API、前端帶到設定頁（與 Server 版相同）。
    /// 失敗訊息與鎖定規則與登入頁相同（不分帳號不存在、密碼錯、鎖定中）。
    /// </summary>
    [HttpPost("login/web")]
    public async Task<ActionResult<ApiResult<WebLoginResultDto>>> WebLogin([FromBody] WebLoginRequestDto request)
    {
        var returnUrl = ReturnUrlGuard.Sanitize(request.ReturnUrl);
        var attempt = await userServiceLogin.LoginAsync(request.Account, request.Password);
        var (message, user) = attempt;

        if (attempt.RequiresTwoFactor && attempt.User is { } pendingUser)
        {
            var stamp = await securityStampService.EnsureAsync(pendingUser.Id);
            if (!twoFactorTickets.IsDeviceRemembered(request.RememberDeviceToken, pendingUser.Id, stamp))
            {
                logger.LogInformation("Web login requires the second factor. UserId={UserId}", pendingUser.Id);
                return Ok(ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto
                {
                    RequiresTwoFactor = true,
                    TwoFactorTicket = twoFactorTickets.Issue(pendingUser.Id, stamp, request.RememberMe, returnUrl, provider: null),
                    RememberDeviceDays = twoFactorTickets.RememberDeviceDays,
                    ReturnUrl = returnUrl,
                }, "請輸入兩步驟驗證碼"));
            }

            (message, user) = await userServiceLogin.CompleteSecondFactorAsync(pendingUser.Id, null, rememberedDevice: true);
        }

        if (!string.IsNullOrEmpty(message) || user is null)
        {
            logger.LogWarning("Web login failed. Account={Account}", request.Account);
            return Unauthorized(ApiResult<WebLoginResultDto>.UnauthorizedResult(string.IsNullOrEmpty(message) ? "登入失敗，請重新確認帳號與密碼。" : message));
        }

        var tokens = await webSessionTokens.IssueWebAsync(user, request.RememberMe, Request);
        logger.LogInformation("Web login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto { Tokens = tokens, ReturnUrl = returnUrl }, "登入成功"));
    }

    /// <summary>
    /// 登入第二步：待驗票證＋驗證碼（或備用碼）。驗證碼錯了計入登入失敗次數（達門檻鎖定並通知管理員），票證仍可再用；
    /// 等待期間改了密碼、被停用或強制登出，這次登入作廢（回 <see cref="ApiErrorCodes.TwoFactorTicketInvalid"/>，前端回登入頁）。
    /// 勾「記住這台裝置」時另回記住裝置 Token。
    /// </summary>
    [HttpPost("two-factor")]
    public async Task<ActionResult<ApiResult<WebLoginResultDto>>> TwoFactor([FromBody] TwoFactorLoginRequestDto request)
    {
        if (twoFactorTickets.Read(request.Ticket) is not { } ticket)
        {
            return TicketInvalid();
        }

        var state = await securityStampService.GetStateAsync(ticket.UserId, TimeSpan.Zero);
        if (!state.IsActive || !SecurityStamps.Matches(ticket.SecurityStamp, state.SecurityStamp))
        {
            logger.LogInformation("Pending two-factor login discarded because the session changed. UserId={UserId}", ticket.UserId);
            twoFactorTickets.TryConsume(ticket);
            return TicketInvalid();
        }

        var result = await userServiceLogin.CompleteSecondFactorAsync(ticket.UserId, request.Code, rememberedDevice: false, ticket.Provider);
        if (result.User is not { } user)
        {
            return Unauthorized(ApiResult<WebLoginResultDto>.UnauthorizedResult(result.Message));
        }

        // 同一張票證的兩個請求都通過（各帶一組有效的碼）時，只有先作廢的那個拿得到 Token。
        if (!twoFactorTickets.TryConsume(ticket))
        {
            return TicketInvalid();
        }

        var tokens = await webSessionTokens.IssueWebAsync(user, ticket.RememberMe, Request);
        logger.LogInformation("Two-factor web login completed. UserId={UserId}", user.Id);
        return Ok(ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto
        {
            Tokens = tokens,
            RememberDeviceToken = request.RememberDevice ? twoFactorTickets.IssueDeviceToken(user.Id, user.SecurityStamp) : null,
            ReturnUrl = ReturnUrlGuard.Sanitize(ticket.ReturnUrl),
        }, "登入成功"));
    }

    /// <summary>
    /// Google 登入的一次性代碼換 Token（代碼由 <c>/Auths/Google/Callback</c> 產生、60 秒、只能用一次、綁定發起的瀏覽器）。
    /// 帳號已啟用兩步驟驗證：記住過的裝置直接完成，否則回待驗票證（Google 登入沒有「記住我」）。
    /// </summary>
    [HttpPost("google/exchange")]
    public async Task<ActionResult<ApiResult<WebLoginResultDto>>> GoogleExchange([FromBody] GoogleExchangeRequestDto request)
    {
        if (googleLoginCodes.Redeem(request.Code, request.Verifier) is not { } grant)
        {
            return Unauthorized(ApiResult<WebLoginResultDto>.UnauthorizedResult(GoogleExpiredMessage));
        }

        var user = await userServiceLogin.GetActiveUserAsync(grant.UserId);
        if (user is null || !SecurityStamps.Matches(grant.SecurityStamp, user.SecurityStamp))
        {
            logger.LogInformation("Google login code rejected because the account changed after the callback. UserId={UserId}", grant.UserId);
            return Unauthorized(ApiResult<WebLoginResultDto>.UnauthorizedResult(GoogleExpiredMessage));
        }

        var provider = GoogleDefaults.AuthenticationScheme;
        var returnUrl = ReturnUrlGuard.Sanitize(grant.ReturnUrl);
        if (grant.RequiresSecondFactor)
        {
            if (!twoFactorTickets.IsDeviceRemembered(request.RememberDeviceToken, user.Id, user.SecurityStamp))
            {
                logger.LogInformation("Google login requires the second factor. UserId={UserId}", user.Id);
                return Ok(ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto
                {
                    RequiresTwoFactor = true,
                    TwoFactorTicket = twoFactorTickets.Issue(user.Id, user.SecurityStamp, rememberMe: false, returnUrl, provider),
                    RememberDeviceDays = twoFactorTickets.RememberDeviceDays,
                    ReturnUrl = returnUrl,
                }, "請輸入兩步驟驗證碼"));
            }

            var completed = await userServiceLogin.CompleteSecondFactorAsync(user.Id, null, rememberedDevice: true, provider);
            if (completed.User is null)
            {
                return Unauthorized(ApiResult<WebLoginResultDto>.UnauthorizedResult(completed.Message));
            }

            user = completed.User;
        }
        else
        {
            await auditLogService.WriteAsync(
                AuditActions.Login.SsoSuccess, success: true, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google");
        }

        var tokens = await webSessionTokens.IssueWebAsync(user, rememberMe: false, Request);
        logger.LogInformation("Google login succeeded. UserId={UserId}, Account={Account}.", user.Id, user.Account);
        return Ok(ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto { Tokens = tokens, ReturnUrl = returnUrl }, "登入成功"));
    }

    /// <summary>
    /// 新分頁取得 Token：沒勾「記住我」的 Token 存在 sessionStorage，新分頁拿不到；由已開的分頁以自己的刷新 Token 為它開一串新的
    /// （沿用登入識別、「記住我」與整串上限；原本那一串不輪替）。只限網頁管道、只接受目前有效的刷新 Token。
    /// </summary>
    [HttpPost("session/fork")]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Fork([FromBody] RefreshTokenRequestDto request)
    {
        var result = await refreshTokenStore.ForkAsync(request.RefreshToken, webSessionTokens.Lifetime);
        if (result.Status != RefreshRotationStatus.Rotated)
        {
            logger.LogInformation("Session fork rejected. UserId={UserId}, Status={Status}", result.UserId, result.Status);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult(InvalidRefreshMessage));
        }

        return Ok(ApiResult<TokenResponseDto>.SuccessResult(webSessionTokens.Respond(result), "已取得 Token"));
    }

    private ActionResult<ApiResult<WebLoginResultDto>> TicketInvalid()
    {
        var result = ApiResult<WebLoginResultDto>.UnauthorizedResult(TicketExpiredMessage);
        result.ErrorCode = ApiErrorCodes.TwoFactorTicketInvalid;
        return Unauthorized(result);
    }
}
