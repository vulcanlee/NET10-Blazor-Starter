using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Configuration;
using MyProject.Web.Email;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 忘記密碼、重設密碼、變更自己的密碼（0.10.8 起，WASM 遷移 M9）。規則、紅線與稽核全在 <see cref="PasswordResetService"/>、
/// <see cref="MyUserService.ChangeOwnPasswordAsync"/>（與 Server 版頁面相同，見速查表 §6.13、§6.18）。
/// ⚠️ 全部算進較嚴格的登入配額（路徑 <c>/api/v1/Auth/password</c>）；重設 Token 一律放本文，回應不快取、不帶 Referer。
/// </summary>
[Route("api/v1/Auth/password")]
[ApiController]
[ApiValidationFilter]
public sealed class AuthPasswordController : ControllerBase
{
    internal const string EmailDisabledMessage = "系統目前未啟用寄信功能，無法自助重設密碼。請聯絡系統管理員協助。";
    internal const string SupportNotAllowedMessage = "support 是系統內建帳號，不提供忘記密碼服務。其密碼由系統設定管理，請洽系統維護人員。";

    private readonly PasswordResetService passwordResetService;
    private readonly IOptions<EmailSettings> emailOptions;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ILogger<AuthPasswordController> logger;

    public AuthPasswordController(
        PasswordResetService passwordResetService,
        IOptions<EmailSettings> emailOptions,
        IPasswordPolicy passwordPolicy,
        ILogger<AuthPasswordController> logger)
    {
        this.passwordResetService = passwordResetService;
        this.emailOptions = emailOptions;
        this.passwordPolicy = passwordPolicy;
        this.logger = logger;
    }

    private bool IsEmailEnabled => emailOptions.Value.TryGetProvider(out var provider) && provider != EmailProvider.None;

    /// <summary>
    /// 申請重設：不論帳號存不存在都回同一個結果（不透露帳號是否存在）；只有輸入內建的 support 時明說不提供。
    /// 寄信未啟用時回 400；正式環境沒設定 <c>EmailSettings:PublicBaseUrl</c> 時記錯誤、照樣回中性結果（與 Server 版相同）。
    /// </summary>
    [HttpPost("forgot")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<object>>> Forgot([FromBody] ForgotPasswordRequestDto request, [FromServices] IHostEnvironment hostEnvironment)
    {
        NoStore();
        if (!IsEmailEnabled)
        {
            return BadRequest(ApiResult<object>.ValidationError(EmailDisabledMessage));
        }

        var baseUrl = PublicBaseUrlResolver.Resolve(emailOptions.Value, Request, hostEnvironment);
        if (baseUrl is null)
        {
            logger.LogError("Password reset link cannot be built because EmailSettings:PublicBaseUrl is not configured.");
            return Ok(ApiResult<object>.SuccessResult(new { }, "申請已送出"));
        }

        var result = await passwordResetService.RequestAsync(request.Identifier, $"{baseUrl}/Auths/ResetPassword");
        if (result == PasswordResetRequestResult.SupportAccountNotAllowed)
        {
            return BadRequest(ApiResult<object>.ValidationError(SupportNotAllowedMessage));
        }

        return Ok(ApiResult<object>.SuccessResult(new { }, "申請已送出"));
    }

    /// <summary>開重設頁時檢查連結（只讀，不消耗）。連結無效或寄信未啟用時 <see cref="ResetPasswordCheckDto.Valid"/> 為 false。</summary>
    [HttpPost("reset/check")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<ResetPasswordCheckDto>>> CheckReset([FromBody] ResetPasswordCheckRequestDto request)
    {
        NoStore();
        var (valid, account) = IsEmailEnabled ? await passwordResetService.ValidateTokenAsync(request.Token) : (false, null);
        if (!valid)
        {
            logger.LogInformation("Reset password link checked and found invalid or expired.");
        }

        return Ok(ApiResult<ResetPasswordCheckDto>.SuccessResult(
            new ResetPasswordCheckDto { Valid = valid, Account = account, PasswordRule = passwordPolicy.Describe() }, "檢查完成"));
    }

    /// <summary>
    /// 以連結設定新密碼。密碼規則或歷史不過時不消耗連結（<see cref="ResetPasswordResultDto.LinkInvalid"/> 為 false，改正後可再送）；
    /// 連結無效時為 true（要重新申請）。
    /// </summary>
    [HttpPost("reset")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<ResetPasswordResultDto>>> Reset([FromBody] ResetPasswordRequestDto request)
    {
        NoStore();
        if (!IsEmailEnabled)
        {
            return BadRequest(ApiResult<ResetPasswordResultDto>.ValidationError(EmailDisabledMessage));
        }

        var result = await passwordResetService.ResetAsync(request.Token, request.NewPassword, request.ConfirmPassword);
        if (result.Success)
        {
            return Ok(ApiResult<ResetPasswordResultDto>.SuccessResult(new ResetPasswordResultDto(), "密碼已重設"));
        }

        var body = ApiResult<ResetPasswordResultDto>.ValidationError(result.Message);
        body.Data = new ResetPasswordResultDto { LinkInvalid = result.Message == PasswordResetService.InvalidLinkMessage };
        return BadRequest(body);
    }

    /// <summary>變更密碼頁的資料：帳號、是否 support、被要求變更的原因、密碼規則。強制改密碼期間也能呼叫。</summary>
    [HttpGet("change")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [SignedInUser]
    [AllowDuringForcedSetup]
    public async Task<ActionResult<ApiResult<ChangePasswordStatusDto>>> ChangeStatus([FromServices] MyUserService userService, [FromServices] CurrentUserService currentUserService)
    {
        var user = await userService.GetAsync(currentUserService.CurrentUser.Id);
        if (user.Id == 0)
        {
            return NotFound(ApiResult<ChangePasswordStatusDto>.NotFoundResult("找不到目前登入使用者資料。"));
        }

        var requiredReason = !passwordPolicy.RequiresChange(user.Account, user.HasLocalPassword, user.MustChangePassword, user.PasswordChangedAtUtc)
            ? string.Empty
            : user.MustChangePassword
                ? "系統管理員要求你先變更密碼，才能繼續使用系統。"
                : "你的密碼已到期，請設定新密碼後繼續使用系統。";
        return Ok(ApiResult<ChangePasswordStatusDto>.SuccessResult(new ChangePasswordStatusDto
        {
            Account = user.Account,
            IsSupportAccount = string.Equals(user.Account, MagicObjectHelper.開發者帳號, StringComparison.OrdinalIgnoreCase),
            RequiredReason = requiredReason,
            PasswordRule = passwordPolicy.Describe(),
        }, "取得資料成功"));
    }

    /// <summary>
    /// 變更自己的密碼。成功後其他裝置都登出（工作階段版本已換），這台回新的 Token（舊的一串撤銷，沿用「記住我」與整串上限）。
    /// 強制改密碼期間也能呼叫。
    /// </summary>
    [HttpPost("change")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [SignedInUser]
    [AllowDuringForcedSetup]
    public async Task<ActionResult<ApiResult<SessionRenewalDto>>> Change(
        [FromBody] ChangePasswordRequestDto request,
        [FromServices] MyUserService userService,
        [FromServices] CurrentUserService currentUserService,
        [FromServices] SelfSessionRenewal sessionRenewal)
    {
        var userId = currentUserService.CurrentUser.Id;
        var result = await userService.ChangeOwnPasswordAsync(userId, request.CurrentPassword, request.NewPassword, request.ConfirmPassword);
        if (!result.Success)
        {
            return this.FromVerify<SessionRenewalDto>(result, () => null!, string.Empty);
        }

        var renewal = await sessionRenewal.RenewAsync<SessionRenewalDto>(User, userId, "PasswordChanged");
        return Ok(ApiResult<SessionRenewalDto>.SuccessResult(renewal, "密碼已變更。"));
    }

    /// <summary>回應裡可能有重設連結的帳號或結果：不讓瀏覽器或代理伺服器快取，也不帶 Referer。</summary>
    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
