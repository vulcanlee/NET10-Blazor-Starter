using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Models.AdapterModel;
using MyProject.Web.Client.Api;
using MyProject.Web.Shell;
using MyProject.Web.Auditing;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 公告的 Web API。
/// <list type="bullet">
/// <item>目前使用者要看到的公告與個人關閉（0.10.1 起，WASM 遷移 M2，給 WASM 外框的公告橫幅用；登入即可）。
/// 挑選規則（時間、對象、沒被關閉）在 <see cref="AnnouncementCache.Select"/>。</item>
/// <item>公告管理頁（0.10.6 起，WASM 遷移 M7；只限管理員）：清單、新增、修改、刪除，走畫面同一個 <see cref="AnnouncementService"/>。
/// 稽核：<c>Announcement.Create</c>／<c>Update</c>／<c>Delete</c>，每個動作一筆；
/// 存檔或刪除後讓公告快取失效並發出公告變動訊號（通知推播串流轉給 WASM 橫幅；橫幅另外每 60 秒與切回分頁時重新取得）。</item>
/// </list>
/// </summary>
[Route("api/v1/announcements")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class AnnouncementsController : ControllerBase
{
    private const string TargetType = "Announcement";

    private readonly AnnouncementCache cache;
    private readonly AnnouncementService announcementService;
    private readonly INotificationSignal signal;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AnnouncementsController> logger;

    public AnnouncementsController(
        AnnouncementCache cache,
        AnnouncementService announcementService,
        INotificationSignal signal,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<AnnouncementsController> logger)
    {
        this.logger = logger;
        this.cache = cache;
        this.announcementService = announcementService;
        this.signal = signal;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
    }

    [HttpGet("active")]
    [SignedInUser] // 登入即可（0.10.4 起明確標註）：公告橫幅：登入的人都看得到、都能關閉自己的。
    [AllowDuringForcedSetup] // 外框的公告橫幅（0.10.8 起，見 ShellController 的說明）。
    public async Task<ActionResult<ApiResult<List<ActiveAnnouncementDto>>>> Active()
    {
        var user = currentUserService.CurrentUser;
        var visible = AnnouncementCache.Select(
            await cache.GetAsync(),
            timeProvider.GetUtcNow().UtcDateTime,
            await announcementService.GetUserRoleIdsAsync(user.Id),
            user.TeamList,
            await announcementService.GetDismissedIdsAsync(user.Id));
        var items = visible.Select(x => new ActiveAnnouncementDto { Id = x.Id, Title = x.Title, Content = x.Content }).ToList();
        return Ok(ApiResult<List<ActiveAnnouncementDto>>.SuccessResult(items, "取得公告成功"));
    }

    [HttpPost("{id:int}/dismiss")]
    [SignedInUser]
    [AllowDuringForcedSetup]
    public async Task<ActionResult<ApiResult<object>>> Dismiss(int id)
    {
        await announcementService.DismissAsync(currentUserService.CurrentUser.Id, id);
        logger.LogDebug("Announcement dismissed. UserId={UserId}, AnnouncementId={AnnouncementId}", currentUserService.CurrentUser.Id, id);
        return Ok(ApiResult<object>.SuccessResult(new { }, "已關閉公告"));
    }

    /// <summary>管理頁：全部公告（開始時間新到舊，含對象名稱）、對象選項（未刪除的角色與團隊），以及伺服器的「現在」（本地時間）。</summary>
    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AnnouncementListDto>>> GetAll()
    {
        var items = await announcementService.GetAllAsync();
        var (roles, teams) = await announcementService.GetTargetOptionsAsync();
        var now = TimeZoneInfo.ConvertTimeFromUtc(timeProvider.GetUtcNow().UtcDateTime, timeProvider.LocalTimeZone);
        var dto = new AnnouncementListDto
        {
            Items = items.Select(AnnouncementDtoMapper.ToDto).ToList(),
            Roles = roles.Select(x => new AnnouncementTargetOptionDto { Id = x.Id, Name = x.Name }).ToList(),
            Teams = teams.Select(x => new AnnouncementTargetOptionDto { Id = x.Id, Name = x.Name }).ToList(),
            ServerNow = DateTime.SpecifyKind(now, DateTimeKind.Unspecified),
        };
        return Ok(ApiResult<AnnouncementListDto>.SuccessResult(dto, "取得公告成功"));
    }

    /// <summary>新增；回傳新的 Id。建立者是目前登入的帳號（不信任畫面送來的值）。輸入不符規則回 400。</summary>
    [HttpPost]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<int>>> Create([FromBody] AnnouncementDto dto)
    {
        var model = AnnouncementDtoMapper.ToModel(dto);
        model.Id = 0;
        var result = await announcementService.AddAsync(model, currentUserService.CurrentUser.Account);
        if (result.Success)
        {
            await AuditSavedAsync(AuditActions.Announcement.Create, model);
            Broadcast();
        }

        return this.FromVerify(result, () => model.Id, "已新增公告。");
    }

    /// <summary>修改；公告已不存在 404、開窗後別人改過 409、輸入不符規則 400。</summary>
    [HttpPut("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] AnnouncementDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        var model = AnnouncementDtoMapper.ToModel(dto);
        var result = await announcementService.UpdateAsync(model);
        if (result.Success)
        {
            await AuditSavedAsync(AuditActions.Announcement.Update, model);
            Broadcast();
        }

        return this.FromVerify(result, "已更新公告。");
    }

    /// <summary>刪除（帶開窗時的版本號）；公告已不存在 404、別人改過 409。稽核的標題取自資料庫。</summary>
    [HttpDelete("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Delete(int id, [FromQuery, Required(ErrorMessage = "版本號不正確。"), MaxLength(64, ErrorMessage = "版本號不正確。")] string concurrencyStamp)
    {
        var title = await announcementService.GetTitleAsync(id);
        var result = await announcementService.DeleteAsync(id, concurrencyStamp);
        if (result.Success)
        {
            await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Announcement.Delete, TargetType,
                id.ToString(CultureInfo.InvariantCulture), $"title={title}");
            Broadcast();
        }

        return this.FromVerify(result, $"已刪除「{title}」。");
    }

    /// <summary>新增與修改的稽核：標題與對象照送來的值。</summary>
    private Task AuditSavedAsync(string action, AnnouncementAdapterModel model)
        => ViewAudit.WriteAsync(auditLogService, currentUserService, action, TargetType,
            model.Id.ToString(CultureInfo.InvariantCulture), $"title={model.Title}; target={model.TargetKind}:{model.TargetId}");

    /// <summary>公告快取失效，並發出公告變動訊號給通知推播串流（同一個行程；其他行程最晚 60 秒）。</summary>
    private void Broadcast()
    {
        cache.Invalidate();
        signal.PublishAnnouncementsChanged();
    }
}
