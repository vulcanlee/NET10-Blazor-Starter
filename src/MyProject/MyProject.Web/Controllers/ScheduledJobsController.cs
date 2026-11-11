using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Web.Filters;
using MyProject.Web.Scheduling;

namespace MyProject.Web.Controllers;

/// <summary>
/// 排程作業頁的 Web API（0.10.6 起，WASM 遷移 M7）：只限管理員（權限鍵刻意不進角色矩陣）。
/// 排程器是伺服器的單例狀態（<see cref="ScheduledJobOverviewService"/>），這裡只讀總覽與放入「立即執行」、切換啟用；
/// 稽核的動作、對象與內容與 Server 版頁面相同（由伺服器寫）。時間是伺服器本地時間，以不帶時差送出。
/// </summary>
[Route("api/v1/scheduled-jobs")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public sealed class ScheduledJobsController : ControllerBase
{
    private readonly ScheduledJobOverviewService overviewService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<ScheduledJobsController> logger;

    public ScheduledJobsController(ScheduledJobOverviewService overviewService, CurrentUserService currentUserService, ILogger<ScheduledJobsController> logger)
    {
        this.overviewService = overviewService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>每個作業的執行時間、下次執行、最近一次結果、是否在佇列中（畫面在有作業執行中時輪詢這裡）。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResult<ScheduledJobOverviewDto>>> Overview()
    {
        var overview = await overviewService.GetOverviewAsync();
        var dto = new ScheduledJobOverviewDto
        {
            SchedulingEnabled = overview.SchedulingEnabled,
            SettingsError = overview.SettingsError,
            Items = overview.Items.Select(x => new ScheduledJobItemDto
            {
                Name = x.Name,
                DisplayName = x.DisplayName,
                Description = x.Description,
                Cron = x.Cron,
                IsEnabled = x.IsEnabled,
                NextRunLocal = x.NextRunLocal is { } next ? Unzoned(next) : null,
                LastRun = x.LastRun is null ? null : ToDto(x.LastRun),
                IsQueued = x.IsQueued,
            }).ToList(),
        };
        return Ok(ApiResult<ScheduledJobOverviewDto>.SuccessResult(dto, "取得排程作業成功"));
    }

    /// <summary>立即執行：放進佇列由排程器在背景執行；同一個作業已在佇列或執行中回 409（不寫稽核，與 Server 版相同）。</summary>
    [HttpPost("{name}/trigger")]
    public async Task<ActionResult<ApiResult<object>>> Trigger(string name)
    {
        if (Find(name) is not { } descriptor)
        {
            return JobNotFound(name);
        }

        var user = currentUserService.CurrentUser;
        if (!overviewService.TryTrigger(descriptor.Name, user.Account, user.Id))
        {
            return Conflict(ApiResult<object>.ConflictResult("這個作業已經在執行中，請等它完成。"));
        }

        logger.LogInformation("Scheduled job triggered manually. JobName={JobName}", descriptor.Name);
        await this.WriteAuditAsync(AuditActions.Job.Trigger, "ScheduledJob", descriptor.Name, $"job={descriptor.Name}");
        return Ok(ApiResult<object>.SuccessResult(new { }, $"已開始執行「{descriptor.DisplayName}」。"));
    }

    /// <summary>啟用或停用（停用後不再依排程執行，仍可立即執行）。</summary>
    [HttpPut("{name}/enabled")]
    public async Task<ActionResult<ApiResult<object>>> SetEnabled(string name, [FromBody] ScheduledJobEnabledRequestDto request)
    {
        if (Find(name) is not { } descriptor)
        {
            return JobNotFound(name);
        }

        await overviewService.SetEnabledAsync(descriptor.Name, request.Enabled, currentUserService.CurrentUser.Account);
        await this.WriteAuditAsync(request.Enabled ? AuditActions.Job.Enable : AuditActions.Job.Disable, "ScheduledJob", descriptor.Name, $"job={descriptor.Name}");
        logger.LogInformation("Scheduled job enabled state changed from the API. JobName={JobName}, Enabled={Enabled}", descriptor.Name, request.Enabled);
        return Ok(ApiResult<object>.SuccessResult(new { }, request.Enabled
            ? $"已啟用「{descriptor.DisplayName}」，從下一個時段開始依排程執行。"
            : $"已停用「{descriptor.DisplayName}」，不會再依排程執行（仍可按「立即執行」）。"));
    }

    /// <summary>最近的執行紀錄（新到舊，最多 50 筆）。</summary>
    [HttpGet("{name}/runs")]
    public async Task<ActionResult<ApiResult<List<ScheduledJobRunDto>>>> Runs(string name)
    {
        if (Find(name) is not { } descriptor)
        {
            return JobNotFound<List<ScheduledJobRunDto>>(name);
        }

        var runs = await overviewService.GetRecentRunsAsync(descriptor.Name);
        return Ok(ApiResult<List<ScheduledJobRunDto>>.SuccessResult(runs.Select(ToDto).ToList(), "取得執行紀錄成功"));
    }

    private ScheduledJobDescriptor? Find(string name)
        => overviewService.Descriptors.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

    private NotFoundObjectResult JobNotFound(string name) => JobNotFound<object>(name);

    private NotFoundObjectResult JobNotFound<T>(string name)
    {
        logger.LogInformation("Scheduled job API request for an unknown job. NameLength={NameLength}", name.Length);
        var body = ApiResult<T>.NotFoundResult($"沒有名為「{name}」的排程作業。");
        body.TraceId = HttpContext.TraceIdentifier;
        return NotFound(body);
    }

    private ScheduledJobRunDto ToDto(JobRunAdapterModel run) => new()
    {
        Id = run.Id,
        StartedAtLocal = Unzoned(overviewService.ToLocal(run.StartedAtUtc)),
        Status = run.Status,
        Trigger = run.Trigger,
        TriggeredByAccount = run.TriggeredByAccount,
        DurationMs = run.DurationMs,
        Message = run.Message,
        TraceId = run.TraceId,
    };

    /// <summary>本地時間以「不帶時差」送出：帶了時差，WASM 會換成瀏覽器的時區。</summary>
    private static DateTime Unzoned(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
}
