using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Client.Api;
using MyProject.Web.Filters;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;
using MyProject.Web.SystemReports;

namespace MyProject.Web.Controllers;

/// <summary>
/// 「系統巡檢報告」頁的 Web API（0.10.39 起），只限管理員（權限鍵刻意不進角色矩陣）。報告全體管理員共用；看報告不寫稽核（同巡檢報告）。
/// 「立即產生」不在請求裡產生（可能超過一分鐘，HttpClient 會逾時；速查表 §6.6 也禁止在請求裡跑作業），
/// 而是帶參數放進排程作業的佇列，完成時以站內通知告訴產生者。時間是伺服器本地時間，以不帶時差送出。
/// 0.10.39 起舊版巡檢報告也從這裡讀（原本在系統管理助理的 API）。
/// </summary>
[Route("api/v1/system-reports")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public sealed class SystemReportsController : ControllerBase
{
    public const string NotFoundMessage = "找不到這份報告，可能已超過保存期限被清除。";

    private readonly SystemReportService reportService;
    private readonly AdminPatrolReportService legacyReportService;
    private readonly ScheduledJobOverviewService overviewService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemReportsController> logger;

    public SystemReportsController(
        SystemReportService reportService,
        AdminPatrolReportService legacyReportService,
        ScheduledJobOverviewService overviewService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<SystemReportsController> logger)
    {
        this.reportService = reportService;
        this.legacyReportService = legacyReportService;
        this.overviewService = overviewService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>報告清單（最新在前）。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<SystemReportListDto>>> Search([FromBody] SystemReportSearchRequestDto request)
    {
        var (items, total) = await reportService.SearchAsync(new SystemReportFilter(
            request.Frequency, request.Level, request.From, request.To, request.PageIndex, request.PageSize));
        logger.LogDebug("System reports searched. Rows={Rows}", items.Count);
        return Ok(ApiResult<SystemReportListDto>.SuccessResult(new SystemReportListDto
        {
            Items = items.Select(ToDto).ToList(),
            TotalCount = total,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            DailyRetentionDays = reportService.DailyRetentionDays,
        }));
    }

    /// <summary>一份報告；找不到 404（通知連結的報告可能已過期）。內容檔不見時 Content 為 null。</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResult<SystemReportDetailDto>>> Get(Guid id)
    {
        if (await reportService.GetAsync(id) is not { } detail)
        {
            logger.LogInformation("System report not found. ReportId={ReportId}", id);
            return NotFoundBody<SystemReportDetailDto>();
        }

        return Ok(ApiResult<SystemReportDetailDto>.SuccessResult(new SystemReportDetailDto { Summary = ToDto(detail.Summary), Content = ReadContent(detail) }));
    }

    /// <summary>報告內容；內容檔不見或格式壞掉回 null（後者記警告）。</summary>
    private SystemReportContentDto? ReadContent(SystemReportDetail detail)
    {
        if (detail.ContentJson is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SystemReportContentDto>(json, SystemReportGenerator.ContentJsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "System report content could not be read. ReportId={ReportId}", detail.Summary.ReportId);
            return null;
        }
    }

    /// <summary>
    /// 下載 PDF（0.10.40 起）：由報告內容產生，不另存檔；報告離開系統，寫一筆 <c>SystemReport.Download</c> 稽核。
    /// 找不到報告或內容檔回 404；內嵌字型缺失回 500。
    /// </summary>
    [HttpPost("{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id)
    {
        if (await reportService.GetAsync(id) is not { } detail || ReadContent(detail) is not { } content)
        {
            logger.LogInformation("System report PDF not found. ReportId={ReportId}", id);
            return NotFoundBody<object>();
        }

        byte[] bytes;
        try
        {
            bytes = SystemReportPdfBuilder.Build(content);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "System report PDF export failed because the embedded font is missing.");
            return this.ApiServerError("缺少內建中文字型，請確認建置產物完整。", ex);
        }

        logger.LogInformation("System report PDF exported. ReportId={ReportId}, Bytes={Bytes}", id, bytes.Length);
        await this.WriteAuditAsync(AuditActions.SystemReport.Download, "SystemReport", id.ToString("N"), $"format=pdf; frequency={content.Frequency}");
        return File(bytes, SystemReportPdfBuilder.ContentType, SystemReportPdfBuilder.FileName(content));
    }

    /// <summary>
    /// 立即產生：放進「系統巡檢日報」作業的佇列（背景產生，完成時通知產生者）。同一個作業已在佇列或執行中回 409。
    /// </summary>
    [HttpPost("generate")]
    public async Task<ActionResult<ApiResult<object>>> Generate([FromBody] SystemReportGenerateRequestDto request)
    {
        if (request.Frequency != SystemReportFrequencies.Daily)
        {
            return BadRequest(ApiResult<object>.ValidationError("目前只能產生日報。"));
        }

        var date = request.Date!.Value.Date;
        var today = timeProvider.GetLocalNow().Date;
        if (date >= today)
        {
            return BadRequest(ApiResult<object>.ValidationError("只能產生今天以前的日報（今天還沒過完）。"));
        }

        if (date < today.AddDays(-reportService.DailyRetentionDays))
        {
            return BadRequest(ApiResult<object>.ValidationError($"只能產生最近 {reportService.DailyRetentionDays} 天內的日報。"));
        }

        var user = currentUserService.CurrentUser;
        var arguments = new SystemReportJobArguments(date, request.SendToAdmins).ToJson();
        if (!overviewService.TryTrigger(SystemReportDailyJob.JobName, user.Account, user.Id, arguments))
        {
            return Conflict(ApiResult<object>.ConflictResult("已經有一份報告正在產生，請等它完成。"));
        }

        logger.LogInformation("System report generation queued. Date={Date}, SendToAdmins={SendToAdmins}", date, request.SendToAdmins);
        await this.WriteAuditAsync(AuditActions.Job.Trigger, "ScheduledJob", SystemReportDailyJob.JobName,
            $"job={SystemReportDailyJob.JobName}; date={date:yyyy-MM-dd}; send={request.SendToAdmins}; via=system-reports");
        return Ok(ApiResult<object>.SuccessResult(new { }, "已開始產生報告，完成後會以站內通知告訴你（約需一分鐘）。"));
    }

    /// <summary>舊版巡檢報告清單（0.9.161～0.10.38 的 AI 巡檢；最新在前）與保存天數。</summary>
    [HttpGet("legacy")]
    public async Task<ActionResult<ApiResult<AdminPatrolReportListDto>>> Legacy()
        => Ok(ApiResult<AdminPatrolReportListDto>.SuccessResult(new AdminPatrolReportListDto
        {
            RetentionDays = legacyReportService.RetentionDays,
            Items = (await legacyReportService.ListAsync()).Select(AgentDtoMapper.ToDto).ToList(),
        }));

    /// <summary>一份舊版巡檢報告（內文 Markdown 由畫面消毒）；找不到 404。</summary>
    [HttpGet("legacy/{id:guid}")]
    public async Task<ActionResult<ApiResult<AdminPatrolReportDetailDto>>> LegacyReport(Guid id)
    {
        if (await legacyReportService.GetAsync(id) is not { } detail)
        {
            logger.LogInformation("Legacy patrol report not found. ReportId={ReportId}", id);
            return NotFoundBody<AdminPatrolReportDetailDto>();
        }

        return Ok(ApiResult<AdminPatrolReportDetailDto>.SuccessResult(AgentDtoMapper.ToDto(detail)));
    }

    private NotFoundObjectResult NotFoundBody<T>()
    {
        var body = ApiResult<T>.NotFoundResult(NotFoundMessage);
        body.TraceId = HttpContext.TraceIdentifier;
        return NotFound(body);
    }

    private static SystemReportSummaryDto ToDto(SystemReportSummary item) => new()
    {
        ReportId = item.ReportId,
        Frequency = item.Frequency,
        PeriodStart = Unzoned(item.PeriodStart),
        PeriodEnd = Unzoned(item.PeriodEnd),
        CreatedAt = Unzoned(item.CreatedAt),
        Level = item.Level,
        RedCount = item.RedCount,
        OrangeCount = item.OrangeCount,
        YellowCount = item.YellowCount,
        Trigger = item.Trigger,
        TriggeredByAccount = item.TriggeredByAccount,
        NarrativeStatus = item.NarrativeStatus,
        ModelName = item.ModelName,
        DeliveryStatus = item.DeliveryStatus,
    };

    /// <summary>本地時間以「不帶時差」送出：帶了時差，WASM 會換成瀏覽器的時區。</summary>
    private static DateTime Unzoned(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
}
