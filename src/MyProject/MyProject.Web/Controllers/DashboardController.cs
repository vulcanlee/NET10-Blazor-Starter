using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Auths;
using MyProject.Web.Dashboard;
using MyProject.Web.Filters;
using MyProject.Web.Scheduling;

namespace MyProject.Web.Controllers;

/// <summary>
/// 登入後首頁（/App）的 Web API（0.10.6 起，WASM 遷移 M7）。
/// 伺服器決定快速入口與看得到的小工具（<see cref="DashboardWidgetCatalog.VisibleTo"/>，規則與 Server 版相同），畫面只渲染收到的；
/// 每個小工具的資料各有端點，權限與小工具的登記一致（「排程作業」只限管理員）。時間都是伺服器本地時間、不帶時差。
/// </summary>
[Route("api/v1/dashboard")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class DashboardController : ControllerBase
{
    /// <summary>「我的通知」顯示的筆數（與 Server 版小工具相同）。</summary>
    internal const int NotificationListSize = 5;

    private readonly DashboardWidgetCatalog widgetCatalog;
    private readonly CurrentUserService currentUserService;
    private readonly ISystemIdentity systemIdentity;
    private readonly IWebHostEnvironment environment;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DashboardController> logger;

    public DashboardController(
        DashboardWidgetCatalog widgetCatalog,
        CurrentUserService currentUserService,
        ISystemIdentity systemIdentity,
        IWebHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<DashboardController> logger)
    {
        this.widgetCatalog = widgetCatalog;
        this.currentUserService = currentUserService;
        this.systemIdentity = systemIdentity;
        this.environment = environment;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>歡迎列、快速入口與這個人看得到的小工具。</summary>
    [HttpGet]
    [HasPermission(MagicObjectHelper.角色_首頁, PermissionActions.View)]
    public ActionResult<ApiResult<DashboardDto>> Get()
    {
        var user = currentUserService.CurrentUser;
        bool CanAccess(string page) => PermissionRules.CanAccessPage(user, page);

        var dto = new DashboardDto
        {
            SystemName = systemIdentity.Name,
            SystemDescription = systemIdentity.Description,
            SystemVersion = systemIdentity.Version,
            EnvironmentName = environment.EnvironmentName,
            QuickLinks = DashboardQuickLinks.All
                .Where(x => CanAccess(x.PermissionKey))
                .Select(x => new DashboardQuickLinkDto { Url = x.Url, Icon = x.Icon, Title = x.Title })
                .ToList(),
            Widgets = widgetCatalog.VisibleTo(user.IsAdmin, CanAccess)
                .Select(x => new DashboardWidgetDto { Id = x.Id, Title = x.Title, Icon = x.Icon })
                .ToList(),
        };
        logger.LogDebug("Dashboard loaded. WidgetCount={WidgetCount}, QuickLinkCount={QuickLinkCount}", dto.Widgets.Count, dto.QuickLinks.Count);
        return Ok(ApiResult<DashboardDto>.SuccessResult(dto, "取得首頁成功"));
    }

    /// <summary>「我的帳號」：上一次登入（不含這一次）、密碼到期、兩步驟驗證。</summary>
    [HttpGet("account")]
    [HasPermission(MagicObjectHelper.角色_首頁, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<DashboardAccountDto>>> Account([FromServices] ProfileService profileService)
    {
        var userId = currentUserService.CurrentUser.Id;
        var profile = await profileService.GetAsync(userId);
        var previousLogin = await profileService.GetPreviousLoginAsync(userId);

        var dto = new DashboardAccountDto
        {
            PreviousLoginLocal = previousLogin is null ? null : ToLocal(previousLogin.OccurredAtUtc),
            PreviousLoginMethod = previousLogin is null ? null : previousLogin.Action == AuditActions.Login.SsoSuccess ? "Google 登入" : "帳號密碼",
            PreviousLoginClientIp = previousLogin?.ClientIp,
            HasLocalPassword = profile?.HasLocalPassword == true,
            PasswordExpiresAtLocal = profile?.PasswordExpiresAtUtc is { } expires ? ToLocal(expires) : null,
            PasswordExpiresSoon = profile?.PasswordExpiresAtUtc is { } expiresAt && expiresAt <= timeProvider.GetUtcNow().UtcDateTime.AddDays(7),
            TwoFactorEnabled = profile?.TwoFactorEnabled == true,
        };
        return Ok(ApiResult<DashboardAccountDto>.SuccessResult(dto, "取得帳號摘要成功"));
    }

    /// <summary>「我的通知」：未讀數與最新 5 則（只保留安全的站內連結，規則與通知鈴鐺相同）。</summary>
    [HttpGet("notifications")]
    [HasPermission(MagicObjectHelper.角色_首頁, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<DashboardNotificationsDto>>> Notifications([FromServices] NotificationQueryService queryService)
    {
        var userId = currentUserService.CurrentUser.Id;
        var latest = await queryService.GetLatestAsync(userId, NotificationListSize);
        var dto = new DashboardNotificationsDto
        {
            UnreadCount = await queryService.GetUnreadCountAsync(userId),
            Items = latest.Select(x => new DashboardNotificationItemDto
            {
                Id = x.Id,
                Title = x.Title,
                Link = !string.IsNullOrWhiteSpace(x.Link) && ReturnUrlGuard.Sanitize(x.Link) == x.Link ? x.Link : null,
                CreatedAtLocal = ToLocal(x.CreatedAtUtc),
                IsRead = x.ReadAtUtc is not null,
            }).ToList(),
        };
        return Ok(ApiResult<DashboardNotificationsDto>.SuccessResult(dto, "取得通知成功"));
    }

    /// <summary>「排程作業」（只限管理員）：上次失敗或中斷、逾期未執行、已停用的作業。</summary>
    [HttpGet("scheduled-jobs")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<DashboardScheduledJobsDto>>> ScheduledJobs([FromServices] ScheduledJobOverviewService overviewService)
    {
        var overview = await overviewService.GetOverviewAsync();
        var dto = new DashboardScheduledJobsDto
        {
            SchedulingEnabled = overview.SchedulingEnabled,
            JobCount = overview.Items.Count,
            Attention = ScheduledJobAttention.From(overview).Select(x => new DashboardScheduledJobAttentionDto
            {
                DisplayName = x.DisplayName,
                Reason = x.Reason,
                IsDisabled = x.Reason == ScheduledJobAttention.DisabledReason,
            }).ToList(),
        };
        return Ok(ApiResult<DashboardScheduledJobsDto>.SuccessResult(dto, "取得排程作業摘要成功"));
    }

    /// <summary>UTC 換成伺服器本地時間，以不帶時差送出（帶了時差，WASM 會換成瀏覽器的時區）。</summary>
    private DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone), DateTimeKind.Unspecified);
}
