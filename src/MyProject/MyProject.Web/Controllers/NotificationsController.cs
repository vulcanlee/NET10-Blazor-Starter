using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Components.Auths;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 自己的站內通知（0.10.1 起，WASM 遷移 M2，給 WASM 外框的通知鈴鐺用）。
/// 只看得到、改得到自己的通知：一律以 <see cref="CurrentUserService"/> 的使用者呼叫 <see cref="NotificationQueryService"/>，
/// 規則與 Server 版的通知鈴鐺相同。
/// </summary>
[Route("api/v1/notifications")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[SignedInUser] // 登入即可（0.10.4 起明確標註）：通知鈴鐺：每個人只處理自己的通知。
public sealed class NotificationsController : ControllerBase
{
    /// <summary>鈴鐺下拉清單顯示的筆數（與 Server 版相同）。</summary>
    internal const int ListSize = 10;

    private readonly NotificationQueryService queryService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationsController> logger;

    public NotificationsController(NotificationQueryService queryService, CurrentUserService currentUserService, TimeProvider timeProvider, ILogger<NotificationsController> logger)
    {
        this.queryService = queryService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private int UserId => currentUserService.CurrentUser.Id;

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResult<NotificationSummaryDto>>> Summary()
    {
        var latest = await queryService.GetLatestAsync(UserId, ListSize);
        var summary = new NotificationSummaryDto
        {
            UnreadCount = await queryService.GetUnreadCountAsync(UserId),
            HasRead = await queryService.HasReadAsync(UserId),
            Latest = latest.Select(x => new NotificationItemDto
            {
                Id = x.Id,
                Category = x.Category,
                Title = x.Title,
                Body = x.Body,
                // 只保留安全的站內相對網址，跟 Server 版點擊時的檢查相同。
                Link = !string.IsNullOrWhiteSpace(x.Link) && ReturnUrlGuard.Sanitize(x.Link) == x.Link ? x.Link : null,
                CreatedAtLocal = ToLocal(x.CreatedAtUtc),
                IsRead = x.ReadAtUtc is not null,
            }).ToList(),
        };
        return Ok(ApiResult<NotificationSummaryDto>.SuccessResult(summary, "取得通知成功"));
    }

    [HttpPost("{id:int}/read")]
    public async Task<ActionResult<ApiResult<object>>> MarkRead(int id)
    {
        await queryService.MarkReadAsync(UserId, id);
        return Ok(ApiResult<object>.SuccessResult(new { }, "已標為已讀"));
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<ApiResult<object>>> MarkAllRead()
    {
        var count = await queryService.MarkAllReadAsync(UserId);
        return Ok(ApiResult<object>.SuccessResult(new { count }, "已全部標為已讀"));
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResult<object>>> Delete(int id)
    {
        var deleted = await queryService.DeleteAsync(UserId, id);
        if (!deleted)
        {
            logger.LogInformation("Notification delete skipped because it was not found or not owned. UserId={UserId}, NotificationId={NotificationId}", UserId, id);
        }

        return deleted
            ? Ok(ApiResult<object>.SuccessResult(new { }, "已刪除"))
            : NotFound(ApiResult<object>.NotFoundResult("找不到這則通知。"));
    }

    [HttpDelete("read")]
    public async Task<ActionResult<ApiResult<object>>> DeleteRead()
    {
        var count = await queryService.DeleteReadAsync(UserId);
        return Ok(ApiResult<object>.SuccessResult(new { count }, "已清除已讀通知"));
    }

    private DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone);
}
