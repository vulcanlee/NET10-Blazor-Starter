using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Web.Auth;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 自己的兩步驟驗證設定（0.10.8 起，WASM 遷移 M9；Server 版 <c>TwoFactorSetupView</c> 的資料來源）：狀態、開始設定（伺服器產生 QR PNG）、
/// 啟用、重新產生備用碼、停用。規則與稽核都在 <see cref="ITwoFactorService"/>（與 Server 版相同）。
/// 金鑰在確認驗證碼之前不存進資料庫，以設定票證（<see cref="TwoFactorSetupTickets"/>）交給前端。
/// 啟用與停用會換工作階段版本：其他裝置登出，這台回新的 Token。
/// ⚠️ 強制設定兩步驟驗證期間也要能用（<see cref="AllowDuringForcedSetupAttribute"/>）；重新產生與停用時驗證碼錯了不計入鎖定，
/// 所以整組算進較嚴格的登入配額（路徑 <c>/api/v1/Auth/two-factor</c>）。
/// </summary>
[Route("api/v1/Auth/two-factor/setup")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[SignedInUser]
[AllowDuringForcedSetup]
public sealed class TwoFactorSetupController : ControllerBase
{
    private readonly ITwoFactorService twoFactorService;
    private readonly ProfileService profileService;
    private readonly TwoFactorSetupTickets setupTickets;
    private readonly SelfSessionRenewal sessionRenewal;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<TwoFactorSetupController> logger;

    public TwoFactorSetupController(
        ITwoFactorService twoFactorService,
        ProfileService profileService,
        TwoFactorSetupTickets setupTickets,
        SelfSessionRenewal sessionRenewal,
        CurrentUserService currentUserService,
        ILogger<TwoFactorSetupController> logger)
    {
        this.twoFactorService = twoFactorService;
        this.profileService = profileService;
        this.setupTickets = setupTickets;
        this.sessionRenewal = sessionRenewal;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    private int UserId => currentUserService.CurrentUser.Id;

    [HttpGet]
    public async Task<ActionResult<ApiResult<TwoFactorSetupStatusDto>>> Status()
    {
        var profile = await profileService.GetAsync(UserId);
        var isEnabled = profile?.TwoFactorEnabled ?? false;
        return Ok(ApiResult<TwoFactorSetupStatusDto>.SuccessResult(new TwoFactorSetupStatusDto
        {
            Account = profile?.Account ?? string.Empty,
            HasLocalPassword = profile?.HasLocalPassword ?? false,
            IsEnabled = isEnabled,
            IsRequired = await twoFactorService.IsRequiredAsync(UserId),
            RemainingBackupCodes = isEnabled ? await twoFactorService.CountUnusedBackupCodesAsync(UserId) : 0,
        }, "取得兩步驟驗證狀態成功"));
    }

    /// <summary>開始設定：產生金鑰、QR Code（PNG data URI）與設定票證。已啟用或沒有本機密碼（Google 帳號）時回 400。</summary>
    [HttpPost("start")]
    public async Task<ActionResult<ApiResult<TwoFactorSetupStartDto>>> Start()
    {
        var profile = await profileService.GetAsync(UserId);
        if (profile is null)
        {
            return NotFound(ApiResult<TwoFactorSetupStartDto>.NotFoundResult("找不到你的帳號資料。"));
        }

        if (!profile.HasLocalPassword)
        {
            return BadRequest(ApiResult<TwoFactorSetupStartDto>.ValidationError("你使用 Google 登入，沒有系統內的密碼，不需要設定兩步驟驗證。"));
        }

        if (profile.TwoFactorEnabled)
        {
            return BadRequest(ApiResult<TwoFactorSetupStartDto>.ValidationError("兩步驟驗證已啟用。"));
        }

        var enrollment = twoFactorService.BeginEnrollment(profile.Account);
        return Ok(ApiResult<TwoFactorSetupStartDto>.SuccessResult(new TwoFactorSetupStartDto
        {
            QrDataUri = QrCodeImage.ToPngDataUri(enrollment.ProvisioningUri),
            Secret = enrollment.Secret,
            SetupTicket = setupTickets.Issue(UserId, enrollment.Secret),
        }, "請用驗證器 App 掃描"));
    }

    /// <summary>以 App 上的驗證碼確認並啟用：回 10 組備用碼（只出現這一次）與這台裝置的新 Token。</summary>
    [HttpPost("enable")]
    public async Task<ActionResult<ApiResult<TwoFactorBackupCodesDto>>> Enable([FromBody] TwoFactorEnableRequestDto request)
    {
        if (setupTickets.Read(request.SetupTicket, UserId) is not { } ticket)
        {
            return BadRequest(ApiResult<TwoFactorBackupCodesDto>.ValidationError("設定已逾時，請重新開始設定。"));
        }

        var result = await twoFactorService.EnableAsync(UserId, ticket.Secret, request.Code);
        if (!result.Success)
        {
            return BadRequest(ApiResult<TwoFactorBackupCodesDto>.ValidationError(result.Message ?? "啟用失敗。"));
        }

        setupTickets.TryConsume(ticket.Id);
        var renewal = await sessionRenewal.RenewAsync<TwoFactorBackupCodesDto>(User, UserId, "TwoFactorEnabled");
        renewal.BackupCodes = result.BackupCodes.ToList();
        logger.LogInformation("Two-factor authentication enabled through the API. UserId={UserId}", UserId);
        return Ok(ApiResult<TwoFactorBackupCodesDto>.SuccessResult(renewal, "已啟用兩步驟驗證"));
    }

    /// <summary>重新產生備用碼（舊的全部失效）；要輸入目前的驗證碼或一組備用碼。不換工作階段版本。</summary>
    [HttpPost("backup-codes")]
    public async Task<ActionResult<ApiResult<TwoFactorBackupCodesDto>>> RegenerateBackupCodes([FromBody] TwoFactorCodeRequestDto request)
    {
        var result = await twoFactorService.RegenerateBackupCodesAsync(UserId, request.Code);
        if (!result.Success)
        {
            return BadRequest(ApiResult<TwoFactorBackupCodesDto>.ValidationError(result.Message ?? "重新產生失敗。"));
        }

        return Ok(ApiResult<TwoFactorBackupCodesDto>.SuccessResult(new TwoFactorBackupCodesDto { BackupCodes = result.BackupCodes.ToList() }, "已重新產生備用碼"));
    }

    /// <summary>停用（必須使用的人不能停用）；要輸入目前的驗證碼或一組備用碼。成功後回這台裝置的新 Token。</summary>
    [HttpPost("disable")]
    public async Task<ActionResult<ApiResult<SessionRenewalDto>>> Disable([FromBody] TwoFactorCodeRequestDto request)
    {
        var result = await twoFactorService.DisableAsync(UserId, request.Code);
        if (!result.Success)
        {
            return this.FromVerify<SessionRenewalDto>(result, () => null!, string.Empty);
        }

        var renewal = await sessionRenewal.RenewAsync<SessionRenewalDto>(User, UserId, "TwoFactorDisabled");
        return Ok(ApiResult<SessionRenewalDto>.SuccessResult(renewal, "已停用兩步驟驗證"));
    }
}
