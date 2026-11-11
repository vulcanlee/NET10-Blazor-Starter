using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Controllers;

/// <summary>
/// 稽核紀錄的 Web API（0.10.2 起，WASM 遷移 M3）：目前只有資料頁的「異動歷程」對話窗要用的兩個端點，只限管理員
/// （與「稽核紀錄」頁相同：稽核含來源 IP 與別人的操作）。稽核的寫法與 Server 頁面相同（<see cref="ServerAuditHistorySource"/>）：
/// 打開時一筆搜尋、點一筆時一筆查看。稽核紀錄頁本身在 M5 搬到 WASM 時再補齊其他端點。
/// </summary>
[Route("api/v1/audit-logs")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class AuditLogsController : ControllerBase
{
    private const int MaxPageSize = 50;

    private readonly AuditLogQueryService queryService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<AuditLogsController> logger;

    public AuditLogsController(
        AuditLogQueryService queryService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<AuditLogsController> logger)
    {
        this.queryService = queryService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>某一筆資料的異動（不含換頁、搜尋、查看），新的在前。<paramref name="opened"/> 為 true 時寫一筆搜尋稽核（換頁不帶）。</summary>
    [HttpGet("history")]
    public async Task<ActionResult<ApiResult<PagedResult<AuditHistoryItemDto>>>> History(
        [FromQuery] string targetType, [FromQuery] string targetId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] bool opened = false)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId) || page < 1 || pageSize < 1 || pageSize > MaxPageSize)
        {
            return BadRequest(ApiResult<PagedResult<AuditHistoryItemDto>>.ValidationError("請指定目標類型與識別，頁碼從 1 開始、每頁 1～50 筆。"));
        }

        var result = await queryService.GetTargetHistoryAsync(targetType, targetId, page, pageSize);
        if (opened)
        {
            await ServerAuditHistorySource.WriteOpenedAuditAsync(auditLogService, currentUserService, targetType, targetId);
        }

        return Ok(ApiResult<PagedResult<AuditHistoryItemDto>>.SuccessResult(new PagedResult<AuditHistoryItemDto>
        {
            Items = result.Result.Select(ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)pageSize),
        }, "取得異動歷程成功"));
    }

    /// <summary>一筆稽核的詳細內容（修改前後差異）；寫一筆查看稽核。</summary>
    [HttpGet("{id:int}/content")]
    public async Task<ActionResult<ApiResult<AuditContentDto>>> Content(int id)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var item = await queryService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult<AuditContentDto>.NotFoundResult("找不到這筆稽核紀錄。"));
        }

        var text = string.Empty;
        if (item.ContentFile is not null)
        {
            try
            {
                text = await queryService.GetContentAsync(id) ?? ServerAuditHistorySource.MissingContentText;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to read audit content file. AuditLogId={AuditLogId}", id);
                text = "讀取詳細內容失敗。" + TraceCode.Suffix(HttpContext.TraceIdentifier);
            }
        }

        await ServerAuditHistorySource.WriteViewedAuditAsync(auditLogService, currentUserService, item);
        return Ok(ApiResult<AuditContentDto>.SuccessResult(new AuditContentDto { Text = text }, "取得詳細內容成功"));
    }

    private ObjectResult? Denied()
    {
        if (currentUserService.CurrentUser.IsAdmin)
        {
            return null;
        }

        logger.LogWarning("Audit history API denied because the user is not an administrator. UserId={UserId}", currentUserService.CurrentUser.Id);
        return StatusCode(StatusCodes.Status403Forbidden, ApiResult.ForbiddenResult("只有管理員可以查看異動歷程。"));
    }

    private static AuditHistoryItemDto ToDto(AuditLogAdapterModel item) => new()
    {
        Id = item.Id,
        OccurredAt = item.OccurredAt,
        ActorUserId = item.ActorUserId,
        ActorAccount = item.ActorAccount,
        Action = item.Action,
        TargetType = item.TargetType,
        TargetId = item.TargetId,
        Detail = item.Detail,
        Success = item.Success,
        ClientIp = item.ClientIp,
        HasContent = item.ContentFile is not null,
    };
}
