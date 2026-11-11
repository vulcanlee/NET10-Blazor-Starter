using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Models.Systems;
using MyProject.Web.Components.Auths;
using MyProject.Web.Filters;
using MyProject.Web.Streaming;

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
// 強制改密碼／設定兩步驟驗證期間也能用（0.10.8 起）：那兩頁在 WASM 執行、和 Server 版一樣顯示完整外框，外框的資料不能被擋（否則一直被導回設定頁）。
// 只涉及自己的資料與畫面框架，不能用來做業務操作。
[AllowDuringForcedSetup]
public sealed class NotificationsController : ControllerBase
{
    /// <summary>鈴鐺下拉清單顯示的筆數（與 Server 版相同）。</summary>
    internal const int ListSize = 10;

    private readonly NotificationQueryService queryService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationsController> logger;
    private readonly INotificationSignal signal;
    private readonly IOptionsMonitor<NotificationSettings> notificationSettings;
    private readonly NotificationPushOptions pushOptions;
    private readonly NotificationPushConnections pushConnections;
    private readonly IHostApplicationLifetime applicationLifetime;

    public NotificationsController(
        NotificationQueryService queryService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<NotificationsController> logger,
        INotificationSignal signal,
        IOptionsMonitor<NotificationSettings> notificationSettings,
        NotificationPushOptions pushOptions,
        NotificationPushConnections pushConnections,
        IHostApplicationLifetime applicationLifetime)
    {
        this.queryService = queryService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.signal = signal;
        this.notificationSettings = notificationSettings;
        this.pushOptions = pushOptions;
        this.pushConnections = pushConnections;
        this.applicationLifetime = applicationLifetime;
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

    /// <summary>
    /// 通知推播串流（0.10.7 起，WASM 遷移 M8）：NDJSON 長連線，自己的通知或公告有變動時送一筆（不帶內容，外框收到後自己重查），
    /// 閒置時每 25 秒心跳。Token 快到期（送 renew）或連線滿 30 分鐘（送 reconnect）時伺服器主動結束，前端重連時重新檢查身分。
    /// 每位使用者同時最多 <see cref="NotificationPushOptions.MaxConnectionsPerUser"/> 條，超過回 429；
    /// <c>NotificationSettings:PushEnabled=false</c> 時回 404。外框的 60 秒輪詢一直都在，推播斷線也不會漏。
    /// </summary>
    [HttpPost("stream")]
    public IActionResult Stream()
    {
        if (!notificationSettings.CurrentValue.PushEnabled)
        {
            return NotFound(ApiResult<object>.NotFoundResult("通知推播已關閉。"));
        }

        var lease = pushConnections.TryAcquire(UserId, pushOptions.MaxConnectionsPerUser);
        if (lease is null)
        {
            logger.LogInformation("Notification push stream rejected because the user has too many connections. UserId={UserId}, Limit={Limit}", UserId, pushOptions.MaxConnectionsPerUser);
            return StatusCode(StatusCodes.Status429TooManyRequests, ApiResult<object>.FailureResult("推播連線數已達上限。", StatusCodes.Status429TooManyRequests));
        }

        // 請求結束（不論正常結束、斷線或例外）一定歸還名額。
        HttpContext.Response.RegisterForDispose(lease);
        var (lifetime, renew) = NotificationPushStream.ResolveLifetime(timeProvider.GetUtcNow(), TokenExpiresAt(), pushOptions);
        var frames = NotificationPushStream.ProduceAsync(
            signal, UserId, lifetime, renew, timeProvider, applicationLifetime.ApplicationStopping, HttpContext.RequestAborted);
        return new NdjsonResult<PushFrameDto>(
            NdjsonHeartbeat.WithHeartbeat(frames, pushOptions.HeartbeatInterval, () => new PushFrameDto { Kind = PushFrameKinds.Ping }));
    }

    /// <summary>這個請求的存取 Token 何時到期（JWT 的 <c>exp</c>，Unix 秒）；讀不到時只靠連線最長時間。</summary>
    private DateTimeOffset? TokenExpiresAt()
        => long.TryParse(User.FindFirst("exp")?.Value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>
    /// UTC 換成伺服器本地時間，並以不帶時差（Kind=Unspecified）送出（0.10.6 起修正）：換算結果的 Kind 是 Local，
    /// 會序列化成 +08:00，WASM 再換成瀏覽器的時區，兩邊時區不同時時間就錯。
    /// </summary>
    private DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone), DateTimeKind.Unspecified);
}
