using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Models.Systems;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Layout;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Health;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// WASM 前端外框用的 API（0.10.1 起，WASM 遷移 M2）：系統資訊、側邊選單、操作說明、開頁稽核、瀏覽器錯誤回報。
/// 規則在 <see cref="SidebarMenuService"/>、<see cref="PageHelpService"/>、<see cref="BrowserErrorReporter"/>（原本 Server 版外框用的服務，0.10.9 起只剩這裡使用）。
/// </summary>
[Route("api/v1/shell")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[SignedInUser] // 登入即可（0.10.4 起明確標註）：WASM 外框：資訊、選單（伺服器依權限過濾）、操作說明、開頁與錯誤回報。
// 強制改密碼／設定兩步驟驗證期間也能用（0.10.8 起）：那兩頁顯示完整外框，外框的資料不能被擋（否則一直被導回設定頁）。
// 只涉及自己的資料與畫面框架，不能用來做業務操作。
[AllowDuringForcedSetup]
public sealed class ShellController : ControllerBase
{
    private const int MaxPathLength = 200;

    /// <summary>開頁稽核每位使用者每分鐘的上限（正常換頁遠低於此；超過就不記，避免灌爆稽核表）。</summary>
    internal const int PageViewsPerMinute = 60;

    private readonly ISystemIdentity systemIdentity;
    private readonly IWebHostEnvironment environment;
    private readonly SystemStartupState startupState;
    private readonly PageHelpService pageHelpService;
    private readonly SidebarMenuService sidebarMenuService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly BrowserErrorReporter browserErrorReporter;
    private readonly PerUserRateLimiter rateLimiter;
    private readonly IOptionsMonitor<ClientErrorReportingSettings> clientErrorOptions;
    private readonly LoggingPipelineMonitor pipelineMonitor;
    private readonly ILogger<ShellController> logger;
    private readonly IOptionsMonitor<NotificationSettings> notificationSettings;

    public ShellController(
        ISystemIdentity systemIdentity,
        IWebHostEnvironment environment,
        SystemStartupState startupState,
        PageHelpService pageHelpService,
        SidebarMenuService sidebarMenuService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        BrowserErrorReporter browserErrorReporter,
        PerUserRateLimiter rateLimiter,
        IOptionsMonitor<ClientErrorReportingSettings> clientErrorOptions,
        LoggingPipelineMonitor pipelineMonitor,
        ILogger<ShellController> logger,
        IOptionsMonitor<NotificationSettings> notificationSettings)
    {
        this.systemIdentity = systemIdentity;
        this.environment = environment;
        this.startupState = startupState;
        this.pageHelpService = pageHelpService;
        this.sidebarMenuService = sidebarMenuService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.browserErrorReporter = browserErrorReporter;
        this.rateLimiter = rateLimiter;
        this.clientErrorOptions = clientErrorOptions;
        this.pipelineMonitor = pipelineMonitor;
        this.logger = logger;
        this.notificationSettings = notificationSettings;
    }

    /// <summary>
    /// 伺服器（zh-TW）的日期時間格式（0.10.2 起）：WASM 在啟動前讀一次並套用（語系要在 RunAsync 之前設定才生效），
    /// 所以不需要登入；內容只有格式字串，與使用者無關。
    /// </summary>
    [HttpGet("culture")]
    [AllowAnonymous]
    public ActionResult<ApiResult<DateTimeFormatDto>> Culture()
        => Ok(ApiResult<DateTimeFormatDto>.SuccessResult(FormatOf(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat), "取得日期時間格式成功"));

    [HttpGet("info")]
    public async Task<ActionResult<ApiResult<ShellInfoDto>>> Info()
    {
        var topics = await pageHelpService.LoadTopicsAsync();
        var info = new ShellInfoDto
        {
            SystemName = systemIdentity.Name,
            SystemDescription = systemIdentity.Description,
            SystemVersion = systemIdentity.Version,
            EnvironmentName = environment.EnvironmentName,
            // 不帶時差（Kind=Unspecified）送出（0.10.6 起修正）：LocalDateTime 的 Kind 是 Local，會序列化成 +08:00，WASM 再換成瀏覽器的時區。
            StartedAtLocal = DateTime.SpecifyKind(startupState.StartedAt.LocalDateTime, DateTimeKind.Unspecified),
            HelpTopics = topics.Select(x => new HelpTopicDto { Route = x.Route, Title = x.Title }).ToList(),
            NotificationPushEnabled = notificationSettings.CurrentValue.PushEnabled,
        };
        return Ok(ApiResult<ShellInfoDto>.SuccessResult(info, "取得系統資訊成功"));
    }

    /// <summary>目前使用者看得到的選單（權限過濾規則：<see cref="PermissionRules.CanAccessPage"/>）。</summary>
    [HttpGet("menu")]
    public async Task<ActionResult<ApiResult<ShellMenuDto>>> Menu()
    {
        var items = await sidebarMenuService.LoadAuthorizedMenuItemsAsync(currentUserService.CurrentUser);
        var menu = new ShellMenuDto
        {
            Items = items.Select(ToDto).ToList(),
            AllMenuUrls = (await sidebarMenuService.LoadAllMenuUrlsAsync()).ToList(),
        };
        return Ok(ApiResult<ShellMenuDto>.SuccessResult(menu, "取得選單成功"));
    }

    /// <summary>某個路由的操作說明。Markdown 在伺服器端以安全管線（<see cref="HelpMarkdownRenderer"/>，停用 HTML）轉成 HTML。</summary>
    [HttpGet("help")]
    public async Task<ActionResult<ApiResult<HelpDocumentDto>>> Help([FromQuery] string? route)
    {
        var topics = await pageHelpService.LoadTopicsAsync();
        var topic = PageHelpService.MatchTopic(topics, route);
        if (topic is null)
        {
            return NotFound(ApiResult<HelpDocumentDto>.NotFoundResult("這一頁沒有操作說明。"));
        }

        var document = await pageHelpService.LoadDocumentAsync(topic);
        var authorizedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SidebarMenuService.CollectUrls(await sidebarMenuService.LoadAuthorizedMenuItemsAsync(currentUserService.CurrentUser), authorizedUrls);
        var related = PageHelpService.FilterVisibleRelatedPages(document.RelatedPages, await sidebarMenuService.LoadAllMenuUrlsAsync(), authorizedUrls);
        var relatedSection = document.Sections.FirstOrDefault(x => x.Heading == PageHelpMarkdownParser.RelatedPagesHeading);

        var dto = new HelpDocumentDto
        {
            Title = topic.Title,
            PreambleHtml = HelpMarkdownRenderer.ToHtml(document.Preamble),
            Sections = document.Sections.Select(x => new HelpSectionDto { Heading = x.Heading, Html = HelpMarkdownRenderer.ToHtml(x.Markdown) }).ToList(),
            RelatedPages = related.Select(x => new HelpRelatedPageDto { Title = x.Title, Route = x.Route, Description = x.Description }).ToList(),
            RelatedPagesIntroHtml = relatedSection is null ? null : HelpMarkdownRenderer.ToHtml(PageHelpMarkdownParser.RelatedPagesIntro(relatedSection.Markdown)),
        };
        return Ok(ApiResult<HelpDocumentDto>.SuccessResult(dto, "取得操作說明成功"));
    }

    /// <summary>
    /// 開頁稽核：由前端路由在換頁時呼叫（<c>Page.View</c>，路徑去掉 query string）。
    /// 只接受 WASM 的頁面路徑（<see cref="MyProject.Web.Client.ClientRouteTable"/>），並有每分鐘上限。
    /// </summary>
    [HttpPost("page-view")]
    public async Task<ActionResult<ApiResult<object>>> PageView([FromBody] PageViewRequestDto request)
    {
        var user = currentUserService.CurrentUser;
        var path = NormalizePath(request.Path);
        if (user.Id > 0 && path.Length > 0
            && MyProject.Web.Client.ClientRouteTable.IsClientRoute(path)
            && rateLimiter.TryAcquire("page-view", user.Id, PageViewsPerMinute))
        {
            var from = NormalizePath(request.From);
            await auditLogService.WriteAsync(
                AuditActions.Page.View, success: true, actorUserId: user.Id, actorAccount: user.Account,
                targetType: "Page", targetId: path, detail: $"from={(from.Length > 0 ? from : "entry")}");

            // 沒有權限的人打開頁面（0.10.2 起）：再記一筆權限拒絕。
            // 由伺服器依選單的權限鍵判斷，不採信瀏覽器的說法；畫面本身只顯示「沒有權限」。
            if (await sidebarMenuService.FindPagePermissionAsync(path) is { } permission && !PermissionRules.CanAccessPage(user, permission))
            {
                await auditLogService.WriteAsync(
                    AuditActions.Permission.Denied, success: false, actorUserId: user.Id, actorAccount: user.Account,
                    targetType: "Page", targetId: path, detail: "via=WASM");
            }
        }

        return Ok(ApiResult<object>.SuccessResult(new { }, "已記錄"));
    }

    /// <summary>瀏覽器端的錯誤回報：交給 <see cref="BrowserErrorReporter"/>（同一套截斷、開關與系統例外紀錄）。</summary>
    [HttpPost("client-errors")]
    public ActionResult<ApiResult<object>> ClientErrors([FromBody] ClientErrorReportDto report)
    {
        // 每位使用者每分鐘的上限沿用 ClientErrorReporting:MaxPerCircuitPerMinute（BrowserErrorReporter 本身的計數是每個實例，在 API 路徑上不成立）。
        if (!rateLimiter.TryAcquire("client-error", currentUserService.CurrentUser.Id, clientErrorOptions.CurrentValue.MaxPerCircuitPerMinute))
        {
            pipelineMonitor.RecordClientErrorDropped();
            return Ok(ApiResult<object>.SuccessResult(new { accepted = false }, "未記錄"));
        }

        var accepted = browserErrorReporter.Report(report.Kind, report.Message, report.Stack, report.Source, report.Path);
        if (!accepted)
        {
            logger.LogDebug("Client error report was not recorded (reporting disabled or throttled).");
        }

        return Ok(ApiResult<object>.SuccessResult(new { accepted }, accepted ? "已記錄" : "未記錄"));
    }

    private static MenuItemDto ToDto(SidebarMenuItemModel item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Icon = item.Icon ?? string.Empty,
        Url = item.Url ?? string.Empty,
        SubMenu = item.SubMenu.Select(ToDto).ToList(),
    };

    private static DateTimeFormatDto FormatOf(System.Globalization.DateTimeFormatInfo format) => new()
    {
        ShortDatePattern = format.ShortDatePattern,
        LongDatePattern = format.LongDatePattern,
        ShortTimePattern = format.ShortTimePattern,
        LongTimePattern = format.LongTimePattern,
        AMDesignator = format.AMDesignator,
        PMDesignator = format.PMDesignator,
    };

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var value = path.Trim();
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        return value.Length > MaxPathLength ? value[..MaxPathLength] : value;
    }
}
