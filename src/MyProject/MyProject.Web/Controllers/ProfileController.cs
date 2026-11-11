using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 自己的個人資料（0.10.1 起，WASM 遷移 M2：「個人資料」頁搬到 WASM 後的資料來源）。
/// 只能看、改自己的資料；規則全在 <see cref="ProfileService"/>（與搬遷前的頁面相同）。
/// </summary>
[Route("api/v1/profile")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[SignedInUser] // 登入即可（0.10.4 起明確標註）：自己的個人資料。
public sealed class ProfileController : ControllerBase
{
    private readonly ProfileService profileService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ProfileController> logger;

    public ProfileController(ProfileService profileService, CurrentUserService currentUserService, TimeProvider timeProvider, ILogger<ProfileController> logger)
    {
        this.profileService = profileService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResult<ProfileDto>>> Get()
    {
        var userId = currentUserService.CurrentUser.Id;
        var info = await profileService.GetAsync(userId);
        if (info is null)
        {
            return NotFound(ApiResult<ProfileDto>.NotFoundResult("找不到你的帳號資料。"));
        }

        var logins = await profileService.GetRecentLoginsAsync(userId);
        var dto = new ProfileDto
        {
            Id = info.Id,
            Account = info.Account,
            Name = info.Name,
            Email = info.Email,
            IsGoogleAccount = info.IsGoogleAccount,
            HasLocalPassword = info.HasLocalPassword,
            Roles = info.Roles.ToList(),
            Teams = info.Teams.ToList(),
            SubordinateTeams = info.SubordinateTeams?.ToList() ?? [],
            PasswordChangedAtLocal = ToLocal(info.PasswordChangedAtUtc),
            PasswordExpiresAtLocal = ToLocal(info.PasswordExpiresAtUtc),
            MustChangePassword = info.MustChangePassword,
            TwoFactorEnabled = info.TwoFactorEnabled,
            ConcurrencyStamp = info.ConcurrencyStamp,
            RecentLogins = logins.Select(x => new LoginRecordDto
            {
                OccurredAtLocal = ToLocal(x.OccurredAtUtc)!.Value,
                Action = DescribeLoginAction(x.Action),
                Success = x.Success,
                ClientIp = x.ClientIp,
            }).ToList(),
        };
        return Ok(ApiResult<ProfileDto>.SuccessResult(dto, "取得個人資料成功"));
    }

    /// <summary>修改自己的姓名；帶回取得時的 <see cref="ProfileDto.ConcurrencyStamp"/>，別人同時改過就不儲存。</summary>
    [HttpPut("name")]
    public async Task<ActionResult<ApiResult<object>>> UpdateName([FromBody] UpdateProfileNameDto request)
    {
        var result = await profileService.UpdateNameAsync(currentUserService.CurrentUser.Id, request.Name, request.ConcurrencyStamp);
        if (!result.Success)
        {
            logger.LogInformation("Profile name update was rejected. UserId={UserId}", currentUserService.CurrentUser.Id);
            return BadRequest(ApiResult<object>.FailureResult(result.Message));
        }

        return Ok(ApiResult<object>.SuccessResult(new { }, "已更新姓名"));
    }

    /// <summary>稽核動作代碼 → 畫面文字（0.10.1 起從個人資料頁搬來）。</summary>
    internal static string DescribeLoginAction(string action) => action switch
    {
        AuditActions.Login.Success => "登入",
        AuditActions.Login.Failed => "登入失敗",
        AuditActions.Login.Disabled => "帳號停用中嘗試登入",
        AuditActions.Login.LockedOut => "連續輸錯，帳號被鎖定",
        AuditActions.Login.SsoSuccess => "Google 登入",
        AuditActions.Login.SsoFailed => "Google 登入失敗",
        AuditActions.Login.TwoFactorFailed => "兩步驟驗證碼錯誤",
        AuditActions.Login.SessionExpired => "其他地方變更了帳號，被登出",
        AuditActions.Logout => "登出",
        _ => action,
    };

    /// <summary>UTC 換成伺服器本地時間，以不帶時差（Kind=Unspecified）送出（0.10.6 起修正，理由同 NotificationsController）。</summary>
    private DateTime? ToLocal(DateTime? utc)
        => utc is { } value
            ? DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), timeProvider.LocalTimeZone), DateTimeKind.Unspecified)
            : null;
}
