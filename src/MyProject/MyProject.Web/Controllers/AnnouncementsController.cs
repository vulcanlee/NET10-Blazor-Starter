using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Components.Layout;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 目前使用者要看到的公告（0.10.1 起，WASM 遷移 M2，給 WASM 外框的公告橫幅用）。
/// 挑選規則（時間、對象、沒被關閉）與 Server 版共用 <see cref="AnnouncementBanner.Select"/>。
/// </summary>
[Route("api/v1/announcements")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[SignedInUser] // 登入即可（0.10.4 起明確標註）：公告橫幅：登入的人都看得到、都能關閉自己的。
public sealed class AnnouncementsController : ControllerBase
{
    private readonly AnnouncementCache cache;
    private readonly AnnouncementService announcementService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AnnouncementsController> logger;

    public AnnouncementsController(AnnouncementCache cache, AnnouncementService announcementService, CurrentUserService currentUserService, TimeProvider timeProvider, ILogger<AnnouncementsController> logger)
    {
        this.logger = logger;
        this.cache = cache;
        this.announcementService = announcementService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
    }

    [HttpGet("active")]
    public async Task<ActionResult<ApiResult<List<ActiveAnnouncementDto>>>> Active()
    {
        var user = currentUserService.CurrentUser;
        var visible = AnnouncementBanner.Select(
            await cache.GetAsync(),
            timeProvider.GetUtcNow().UtcDateTime,
            await announcementService.GetUserRoleIdsAsync(user.Id),
            user.TeamList,
            await announcementService.GetDismissedIdsAsync(user.Id));
        var items = visible.Select(x => new ActiveAnnouncementDto { Id = x.Id, Title = x.Title, Content = x.Content }).ToList();
        return Ok(ApiResult<List<ActiveAnnouncementDto>>.SuccessResult(items, "取得公告成功"));
    }

    [HttpPost("{id:int}/dismiss")]
    public async Task<ActionResult<ApiResult<object>>> Dismiss(int id)
    {
        await announcementService.DismissAsync(currentUserService.CurrentUser.Id, id);
        logger.LogDebug("Announcement dismissed. UserId={UserId}, AnnouncementId={AnnouncementId}", currentUserService.CurrentUser.Id, id);
        return Ok(ApiResult<object>.SuccessResult(new { }, "已關閉公告"));
    }
}
