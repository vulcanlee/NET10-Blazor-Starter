using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Layout;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Health;

namespace MyProject.Web.Controllers;

/// <summary>
/// WASM 前端外框用的 API（0.10.1 起，WASM 遷移 M2）：系統資訊、側邊選單、操作說明、開頁稽核、瀏覽器錯誤回報。
/// 規則全部沿用 Server 版外框用的服務（<see cref="SidebarMenuService"/>、<see cref="PageHelpService"/>、
/// <see cref="BrowserErrorReporter"/>），兩個版本的外框看到的內容一致。
/// </summary>
[Route("api/v1/shell")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
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
    private readonly AuthenticationStateHelper authenticationStateHelper;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly BrowserErrorReporter browserErrorReporter;
    private readonly PerUserRateLimiter rateLimiter;
    private readonly IOptionsMonitor<ClientErrorReportingSettings> clientErrorOptions;
    private readonly LoggingPipelineMonitor pipelineMonitor;
    private readonly ILogger<ShellController> logger;

    public ShellController(
        ISystemIdentity systemIdentity,
        IWebHostEnvironment environment,
        SystemStartupState startupState,
        PageHelpService pageHelpService,
        SidebarMenuService sidebarMenuService,
        AuthenticationStateHelper authenticationStateHelper,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        BrowserErrorReporter browserErrorReporter,
        PerUserRateLimiter rateLimiter,
        IOptionsMonitor<ClientErrorReportingSettings> clientErrorOptions,
        LoggingPipelineMonitor pipelineMonitor,
        ILogger<ShellController> logger)
    {
        this.systemIdentity = systemIdentity;
        this.environment = environment;
        this.startupState = startupState;
        this.pageHelpService = pageHelpService;
        this.sidebarMenuService = sidebarMenuService;
        this.authenticationStateHelper = authenticationStateHelper;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.browserErrorReporter = browserErrorReporter;
        this.rateLimiter = rateLimiter;
        this.clientErrorOptions = clientErrorOptions;
        this.pipelineMonitor = pipelineMonitor;
        this.logger = logger;
    }

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
            StartedAtLocal = startupState.StartedAt.LocalDateTime,
            HelpTopics = topics.Select(x => new HelpTopicDto { Route = x.Route, Title = x.Title }).ToList(),
        };
        return Ok(ApiResult<ShellInfoDto>.SuccessResult(info, "取得系統資訊成功"));
    }

    /// <summary>目前使用者看得到的選單（權限過濾規則與 Server 版側邊欄相同：<see cref="AuthenticationStateHelper.CheckAccessPage"/>）。</summary>
    [HttpGet("menu")]
    public async Task<ActionResult<ApiResult<ShellMenuDto>>> Menu()
    {
        var items = await sidebarMenuService.LoadAuthorizedMenuItemsAsync(authenticationStateHelper);
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
        SidebarMenuService.CollectUrls(await sidebarMenuService.LoadAuthorizedMenuItemsAsync(authenticationStateHelper), authorizedUrls);
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
    /// 開頁稽核：WASM 頁面沒有 Blazor 電路，由前端路由在換頁時呼叫，寫法與 <c>ApplicationCircuitHandler</c> 相同（<c>Page.View</c>，路徑去掉 query string）。
    /// 只接受已搬到 WASM 的頁面路徑（Server 頁面由電路記），並有每分鐘上限。
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
