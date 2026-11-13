using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Auditing;
using MyProject.Web.Diagnostics;
using MyProject.Web.Export;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 稽核紀錄的 Web API，只限管理員（稽核含來源 IP 與別人的操作）。
/// 0.10.2 起（M3）有資料頁「異動歷程」對話窗用的 <c>history</c>、<c>{id}/content</c>；
/// 0.10.4 起（M5）補上稽核紀錄頁本身的查詢、選項、明細、匯出、清除、清空與完整性驗證。
/// 稽核的寫法：按「查詢」一筆搜尋、看明細一筆查看；清除、清空、匯出、驗證各寫一筆（寫在刪除之後，所以清空後仍查得到是誰清的）。
/// </summary>
[Route("api/v1/audit-logs")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class AuditLogsController : ControllerBase
{
    private const int MaxHistoryPageSize = 50;
    private const string HistoryDeniedMessage = "只有管理員可以查看異動歷程。";
    private const string TargetType = "AuditLog";
    private const string MissingContentText = "內容檔不存在（可能已被清除，或當初寫檔失敗）。";

    private readonly AuditLogQueryService queryService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly AuditIntegrityVerifier integrityVerifier;
    private readonly IOptionsMonitor<LogRetentionSettings> retentionOptions;
    private readonly IOptionsMonitor<AuditLogSettings> scopeOptions;
    private readonly ILogger<AuditLogsController> logger;

    public AuditLogsController(
        AuditLogQueryService queryService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        AuditIntegrityVerifier integrityVerifier,
        IOptionsMonitor<LogRetentionSettings> retentionOptions,
        IOptionsMonitor<AuditLogSettings> scopeOptions,
        ILogger<AuditLogsController> logger)
    {
        this.queryService = queryService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.integrityVerifier = integrityVerifier;
        this.retentionOptions = retentionOptions;
        this.scopeOptions = scopeOptions;
        this.logger = logger;
    }

    private int PurgeDays => retentionOptions.CurrentValue.ManualAuditLogDays;

    private int BrowsingPurgeDays => retentionOptions.CurrentValue.ManualAuditBrowsingLogDays;

    /// <summary>某一筆資料的異動（不含換頁、搜尋、查看），新的在前。<paramref name="opened"/> 為 true 時寫一筆搜尋稽核（換頁不帶）。</summary>
    [HttpGet("history")]
    [AdminOnly(HistoryDeniedMessage)]
    public async Task<ActionResult<ApiResult<PagedResult<AuditHistoryItemDto>>>> History(
        [FromQuery] string targetType, [FromQuery] string targetId, [FromQuery] int page = 1, [FromQuery] int pageSize = 10, [FromQuery] bool opened = false)
    {
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId) || page < 1 || pageSize < 1 || pageSize > MaxHistoryPageSize)
        {
            return BadRequest(ApiResult<PagedResult<AuditHistoryItemDto>>.ValidationError("請指定目標類型與識別，頁碼從 1 開始、每頁 1～50 筆。"));
        }

        var result = await queryService.GetTargetHistoryAsync(targetType, targetId, page, pageSize);
        if (opened)
        {
            // 打開異動歷程：一筆稽核紀錄的「搜尋」。
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Audit.Search, TargetType,
                AuditSearchCondition.Of("targetType", targetType), AuditSearchCondition.Of("targetId", targetId),
                AuditSearchCondition.Of("via", "history"));
        }

        return Ok(ApiResult<PagedResult<AuditHistoryItemDto>>.SuccessResult(new PagedResult<AuditHistoryItemDto>
        {
            Items = result.Result.Select(item => Fill(new AuditHistoryItemDto(), item)).ToList(),
            TotalCount = result.Count,
            PageIndex = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)pageSize),
        }, "取得異動歷程成功"));
    }

    /// <summary>一筆稽核的詳細內容（異動歷程對話窗用）；寫一筆查看稽核（via=history）。</summary>
    [HttpGet("{id:int}/content")]
    [AdminOnly(HistoryDeniedMessage)]
    public async Task<ActionResult<ApiResult<AuditContentDto>>> Content(int id)
    {
        var item = await queryService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult<AuditContentDto>.NotFoundResult("找不到這筆稽核紀錄。"));
        }

        var text = item.ContentFile is null ? string.Empty : await ReadContentAsync(id) ?? MissingContentText;

        // 點開一筆：一筆稽核紀錄的「查看」。
        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Audit.View, TargetType, item.Id.ToString(), $"action={item.Action}; via=history");
        return Ok(ApiResult<AuditContentDto>.SuccessResult(new AuditContentDto { Text = text }, "取得詳細內容成功"));
    }

    /// <summary>稽核紀錄頁的清單；<see cref="AuditLogSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核（自由輸入的欄位只記有沒有填）。</summary>
    [HttpPost("search")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<PagedResult<AuditLogItemDto>>>> Search([FromBody] AuditLogSearchRequestDto request)
    {
        var result = await queryService.GetAsync(ToQuery(request, request.PageIndex, request.PageSize));
        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Audit.Search, TargetType,
                AuditSearchCondition.Of("start", request.StartTime), AuditSearchCondition.Of("end", request.EndTime),
                AuditSearchCondition.Of("action", request.Action ?? string.Empty),
                AuditSearchCondition.Of("success", request.Success switch { true => "true", false => "false", _ => string.Empty }),
                AuditSearchCondition.Text("account", request.Account), AuditSearchCondition.Text("ip", request.ClientIp),
                AuditSearchCondition.Text("keyword", request.Keyword),
                AuditSearchCondition.Of("targetType", request.TargetType ?? string.Empty), AuditSearchCondition.Text("targetId", request.TargetId));
        }

        return Ok(ApiResult<PagedResult<AuditLogItemDto>>.SuccessResult(new PagedResult<AuditLogItemDto>
        {
            Items = result.Result.Select(item => Fill(new AuditLogItemDto(), item)).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "查詢稽核紀錄成功"));
    }

    /// <summary>查詢條件的下拉選項（資料庫裡出現過的動作與目標類型）、清除的保留天數與記錄範圍提示。</summary>
    [HttpGet("options")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AuditLogOptionsDto>>> Options()
        => Ok(ApiResult<AuditLogOptionsDto>.SuccessResult(new AuditLogOptionsDto
        {
            Actions = await queryService.GetDistinctActionsAsync(),
            TargetTypes = await queryService.GetDistinctTargetTypesAsync(),
            PurgeDays = PurgeDays,
            BrowsingPurgeDays = BrowsingPurgeDays,
            ScopeNotice = ScopeNotice(),
        }, "取得稽核紀錄選項成功"));

    /// <summary>明細窗：一筆稽核與它的詳細內容；寫一筆查看稽核（沒有內容檔也寫，與 Server 版相同）。</summary>
    [HttpGet("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AuditLogDetailDto>>> Detail(int id)
    {
        var item = await queryService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult<AuditLogDetailDto>.NotFoundResult("找不到這筆稽核紀錄。"));
        }

        // ⚠️ 單筆查詢回的是資料庫的 UTC（清單才換成本地）：先換成本地，與清單一致（實機發現差了 8 小時）。
        item.OccurredAt = AuditLogQueryService.ToLocal(item.OccurredAt);
        var detail = Fill(new AuditLogDetailDto(), item);
        if (item.ContentFile is not null)
        {
            detail.Content = await ReadContentAsync(id) ?? MissingContentText;
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Audit.View, TargetType, item.Id.ToString(), $"action={item.Action}");
        return Ok(ApiResult<AuditLogDetailDto>.SuccessResult(detail, "取得稽核明細成功"));
    }

    /// <summary>匯出目前查詢條件下的全部資料（CSV、UTF-8 含 BOM，最多 <see cref="AuditLogQueryService.MaxExportRows"/> 筆），寫一筆匯出稽核。</summary>
    [HttpPost("export")]
    [AdminOnly]
    public async Task<IActionResult> Export([FromBody] AuditLogSearchRequestDto request)
    {
        var result = await queryService.GetAsync(ToQuery(request, 1, AuditLogQueryService.MaxExportRows));
        var rows = result.Result.ToList();
        var bytes = TabularExport.ToCsv(AuditLogExport.CsvColumns, rows);
        var exported = rows.Count;
        logger.LogInformation("Audit log export downloaded. Rows={Rows}, Total={Total}", exported, result.Count);

        // 稽核記實際匯出的筆數；超過上限被截掉時另記總筆數（審查發現：之前記的是總筆數，看起來像全匯出了）。
        await WriteSelfAuditAsync(AuditActions.Audit.Export, exported < result.Count ? $"format=csv; rows={exported}; total={result.Count}" : $"format=csv; rows={exported}");
        return File(bytes, TabularExport.CsvContentType, AuditLogExport.FileName(DateTime.Now));
    }

    /// <summary>
    /// 清除超過保留天數的紀錄：先清異動與安全紀錄，成功後才清瀏覽紀錄；兩段都成功才回收內容檔的空月份目錄。
    /// 每段各寫一筆 <c>Audit.Purge</c>（寫在刪除之後）。
    /// </summary>
    [HttpPost("purge")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AuditLogPurgeResultDto>>> Purge()
    {
        var change = await queryService.PurgeAsync(PurgeDays, AuditLogKind.Change);
        var browsing = change.Success ? await queryService.PurgeAsync(BrowsingPurgeDays, AuditLogKind.Browsing) : change;
        if (change.Success)
        {
            await WriteSelfAuditAsync(AuditActions.Audit.Purge, $"清除 {PurgeDays} 天前的異動與安全紀錄：{change.Message}");
        }

        var succeeded = change.Success && browsing.Success;
        if (succeeded)
        {
            await WriteSelfAuditAsync(AuditActions.Audit.Purge, $"清除 {BrowsingPurgeDays} 天前的瀏覽紀錄：{browsing.Message}");
            await queryService.DeleteContentMonthsBeforeAsync(DateTime.UtcNow.AddDays(-Math.Max(PurgeDays, BrowsingPurgeDays)));
        }

        return Ok(ApiResult<AuditLogPurgeResultDto>.SuccessResult(new AuditLogPurgeResultDto
        {
            Succeeded = succeeded,
            Changed = change.Success,
            Message = succeeded ? $"異動與安全紀錄：{change.Message}瀏覽紀錄：{browsing.Message}" : browsing.Message,
        }, succeeded ? "清除完成" : "清除失敗"));
    }

    /// <summary>清空全部稽核紀錄；清空後寫一筆 <c>Audit.ClearAll</c>，所以清空後的資料表裡查得到是誰清的。</summary>
    [HttpPost("clear-all")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> ClearAll()
    {
        var result = await queryService.ClearAllAsync();
        if (result.Success)
        {
            await WriteSelfAuditAsync(AuditActions.Audit.ClearAll, $"清空全部稽核紀錄：{result.Message}");
        }

        return this.FromVerify(result, "已清空全部稽核紀錄。");
    }

    /// <summary>
    /// 完整性驗證：重算全部簽章（只比對、不更新檢查點，檢查點只由每天的排程更新），寫一筆 <c>Audit.Verify</c>。
    /// 紀錄多時要一點時間（與筆數成正比）；WASM 頁面這個請求不套用一般的逾時。
    /// </summary>
    [HttpPost("verify")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AuditIntegrityReportDto>>> Verify(CancellationToken cancellationToken)
    {
        AuditIntegrityReport report;
        try
        {
            report = await integrityVerifier.VerifyAsync(updateCheckpoint: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return this.ApiServerError<AuditIntegrityReportDto>("驗證完整性失敗，詳細原因已記錄在系統日誌。", ex);
        }

        await WriteSelfAuditAsync(
            AuditActions.Audit.Verify,
            $"result={(report.HasIssues ? "failed" : "ok")}; signed={report.SignedRows}; issues={report.IssueCount}; "
            + $"legacy={report.LegacyRows}; keyMismatch={report.KeyMismatchRows}; trigger=manual");

        return Ok(ApiResult<AuditIntegrityReportDto>.SuccessResult(new AuditIntegrityReportDto
        {
            HasIssues = report.HasIssues,
            Summary = report.Summary(),
            IssueCount = report.IssueCount,
            Issues = report.Issues.Select(x => new AuditIntegrityIssueDto { Code = x.Code, Message = x.Message }).ToList(),
        }, "驗證完成"));
    }

    private static AuditLogQuery ToQuery(AuditLogSearchRequestDto request, int page, int pageSize) => new()
    {
        StartTime = request.StartTime,
        EndTime = request.EndTime,
        Account = request.Account ?? string.Empty,
        ClientIp = request.ClientIp ?? string.Empty,
        Action = request.Action ?? string.Empty,
        Success = request.Success,
        Keyword = request.Keyword ?? string.Empty,
        TargetType = request.TargetType ?? string.Empty,
        TargetId = request.TargetId ?? string.Empty,
        CurrentPage = page,
        PageSize = pageSize,
        SortField = string.Equals(request.SortBy, nameof(AuditLogAdapterModel.OccurredAt), StringComparison.OrdinalIgnoreCase) ? nameof(AuditLogAdapterModel.OccurredAt) : string.Empty,
        SortDescending = string.IsNullOrWhiteSpace(request.SortBy) ? null : request.SortDescending,
    };

    /// <summary>目前沒有記錄的範圍（兩個開關預設都關）；都有記錄時為 null。</summary>
    private string? ScopeNotice()
    {
        var scope = scopeOptions.CurrentValue;
        var off = new List<string>();
        if (!scope.RecordFieldChanges)
        {
            off.Add("修改前後差異");
        }

        if (!scope.RecordBrowsing)
        {
            off.Add("瀏覽類事件（換頁、搜尋、查看）");
        }

        return off.Count == 0 ? null : $"目前沒有記錄{string.Join("與", off)}，可在「系統參數」頁開啟。";
    }

    /// <summary>讀詳細內容檔：檔案不見回 null；讀取失敗回說明文字（附追蹤碼），不讓整個請求失敗。</summary>
    private async Task<string?> ReadContentAsync(int id)
    {
        try
        {
            return await queryService.GetContentAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read audit content file. AuditLogId={AuditLogId}", id);
            return "讀取詳細內容失敗。" + TraceCode.Suffix(HttpContext.TraceIdentifier);
        }
    }

    /// <summary>
    /// 把「清除／清空／匯出／驗證稽核紀錄」這件事本身寫成一筆稽核（與 Server 版相同）。
    /// ⚠️ 刻意的：稽核軌跡可被管理員一鍵抹除，會讓「誰抹除了稽核紀錄」成為系統裡唯一查不到的事。寫入失敗只記錯誤。
    /// </summary>
    private async Task WriteSelfAuditAsync(string action, string detail)
    {
        try
        {
            var user = currentUserService.CurrentUser;
            await auditLogService.WriteAsync(action, success: true, actorUserId: user.Id, actorAccount: user.Account, targetType: TargetType, targetId: "*", detail: detail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit log for audit maintenance. Action={Action}", action);
        }
    }

    private static T Fill<T>(T dto, AuditLogAdapterModel item)
        where T : AuditHistoryItemDto
    {
        dto.Id = item.Id;
        // ⚠️ 本地時間以「不帶時差」送出（Kind=Unspecified）：帶了 +08:00，WASM 會換成瀏覽器的時區（審查發現）。
        dto.OccurredAt = DateTime.SpecifyKind(item.OccurredAt, DateTimeKind.Unspecified);
        dto.ActorUserId = item.ActorUserId;
        dto.ActorAccount = item.ActorAccount;
        dto.Action = item.Action;
        dto.TargetType = item.TargetType;
        dto.TargetId = item.TargetId;
        dto.Detail = item.Detail;
        dto.Success = item.Success;
        dto.ClientIp = item.ClientIp;
        dto.HasContent = item.ContentFile is not null;
        if (dto is AuditLogItemDto row)
        {
            row.OccurredAtUtc = DateTime.SpecifyKind(AuditLogQueryService.ToUtc(item.OccurredAt), DateTimeKind.Utc);
            row.Signature = item.Seq is { } seq && item.Chain is { } chain
                ? $"已簽章，{AuditChains.Name(chain)} #{seq}"
                : "升級前的紀錄，未納入完整性驗證";
        }

        return dto;
    }
}
