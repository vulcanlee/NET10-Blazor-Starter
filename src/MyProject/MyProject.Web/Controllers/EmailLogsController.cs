using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 寄信紀錄的 Web API（0.10.4 起，WASM 遷移 M5）：唯讀查詢，只限管理員（收件者是個資）。
/// 與 Server 版頁面相同：不寫稽核、不提供內文（內文加密，寄出或放棄後就清除）、沒有重寄或刪除。
/// </summary>
[Route("api/v1/email-logs")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public class EmailLogsController : ControllerBase
{
    private readonly EmailOutboxQueryService queryService;
    private readonly ILogger<EmailLogsController> logger;

    public EmailLogsController(EmailOutboxQueryService queryService, ILogger<EmailLogsController> logger)
    {
        this.queryService = queryService;
        this.logger = logger;
    }

    /// <summary>依建立時間由新到舊；狀態、種類完全相同，關鍵字比對收件者與主旨，每頁 1～200 筆。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<EmailLogItemDto>>>> Search([FromBody] EmailLogSearchRequestDto request)
    {
        var query = new EmailLogQuery(request.FromLocal, request.ToLocal, request.Status, request.Kind, request.Keyword, Math.Max(1, request.PageIndex), request.PageSize);
        var (items, total) = await queryService.GetPageAsync(query);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        logger.LogDebug("Email log search completed. Total={Total}, HasKeyword={HasKeyword}", total, !string.IsNullOrWhiteSpace(request.Keyword));

        return Ok(ApiResult<PagedResult<EmailLogItemDto>>.SuccessResult(new PagedResult<EmailLogItemDto>
        {
            Items = items.Select(x => new EmailLogItemDto
            {
                Id = x.Id,
                // 本地時間以「不帶時差」送出：帶了 +08:00，WASM 會換成瀏覽器的時區。
                CreatedAt = Unzoned(x.CreatedAt),
                Kind = x.Kind,
                To = x.To,
                Subject = x.Subject,
                Status = x.Status,
                Attempts = x.Attempts,
                NextAttemptAt = x.NextAttemptAt is { } next ? Unzoned(next) : null,
                SentAt = x.SentAt is { } sent ? Unzoned(sent) : null,
                LastError = x.LastError,
            }).ToList(),
            TotalCount = total,
            PageIndex = query.PageIndex,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize),
        }, "查詢寄信紀錄成功"));
    }

    private static DateTime Unzoned(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
}
