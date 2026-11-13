using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Auditing;
using MyProject.Web.DataRetention;
using MyProject.Web.Filters;
using MyProject.Web.Scheduling;

namespace MyProject.Web.Controllers;

/// <summary>
/// 資料保留與清理頁的 Web API（0.10.6 起，WASM 遷移 M7），只限管理員。全部走畫面同一個 <see cref="DataRetentionOverviewService"/>。
/// 「立即清理」與 Server 版相同放進排程的佇列（背景執行，畫面每 5 秒重新讀總覽看進度）；伺服器重新讀一次總覽，
/// 確認那一列現在能清理、算出「會清掉幾筆」後才放進佇列並寫 <c>Job.Trigger</c>（detail 同 Server 版）。
/// 「自訂清除」的預覽與清除都由伺服器以自己的「今天」驗證條件；清掉資料才寫稽核（動作代碼與 detail 同 Server 版）。
/// </summary>
[Route("api/v1/data-retention")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class DataRetentionController : ControllerBase
{
    private readonly DataRetentionOverviewService overviewService;
    private readonly ScheduledJobOverviewService jobOverviewService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DataRetentionController> logger;

    public DataRetentionController(
        DataRetentionOverviewService overviewService,
        ScheduledJobOverviewService jobOverviewService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<DataRetentionController> logger)
    {
        this.overviewService = overviewService;
        this.jobOverviewService = jobOverviewService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private DateOnly TodayLocal => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);

    /// <summary>每種資料的數量、檔案空間、最舊一筆、保留規則、下次會清掉幾筆與負責的排程，以及伺服器的「今天」。</summary>
    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<DataRetentionOverviewDto>>> Overview()
    {
        var overview = await overviewService.GetOverviewAsync();
        return Ok(ApiResult<DataRetentionOverviewDto>.SuccessResult(new DataRetentionOverviewDto
        {
            SchedulingEnabled = overview.SchedulingEnabled,
            SettingsError = overview.SettingsError,
            TodayLocal = TodayLocal.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            Rows = overview.Rows.Select(ToDto).ToList(),
        }, "取得資料保留資訊成功"));
    }

    /// <summary>
    /// 立即清理：與「排程作業」頁的「立即執行」同一個佇列。已刪除資料的五列共用一個作業，按任何一列都會五種一起清，
    /// 稽核的 expected 是這幾列「下次會清掉」的合計。
    /// </summary>
    [HttpPost("{rowKey}/purge-now")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<object>>> PurgeNow(string rowKey)
    {
        var overview = await overviewService.GetOverviewAsync();
        var row = overview.Rows.FirstOrDefault(x => x.Key == rowKey);
        if (row is null)
        {
            return NotFound(ApiResult<object>.NotFoundResult("找不到這種資料。"));
        }

        if (row.JobName is not { } jobName)
        {
            return BadRequest(ApiResult<object>.FailureResult($"「{row.Label}」沒有自動清理的排程作業。"));
        }

        var related = overview.Rows.Where(x => x.JobName == jobName && x.Error is null).ToList();
        var current = related.FirstOrDefault(x => x.Key == row.Key) ?? row;
        if (!current.CanPurgeNow)
        {
            return Conflict(ApiResult<object>.ConflictResult($"現在不能清理「{row.Label}」：{current.PurgeDisabledReason}。"));
        }

        var expected = related.Sum(x => x.Expected ?? 0);
        var user = currentUserService.CurrentUser;
        if (!overviewService.TryTrigger(jobName, user.Account, user.Id))
        {
            return Conflict(ApiResult<object>.ConflictResult("這個清理作業已經在執行中，請等它完成。"));
        }

        logger.LogInformation("Data retention purge triggered manually. JobName={JobName}, Expected={Expected}", jobName, expected);
        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Job.Trigger, "ScheduledJob", jobName,
            $"job={jobName}; source=data-retention; expected={expected}");
        return Ok(ApiResult<object>.SuccessResult(new { }, $"已開始清理「{current.Job?.DisplayName ?? row.Label}」，完成後這一頁會自動更新。"));
    }

    /// <summary>「自訂清除」的預覽：依條件會刪掉幾筆（與清除同一個條件；已刪除資料是「最多」）。</summary>
    [HttpPost("{rowKey}/preview")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<int>>> Preview(string rowKey, [FromBody] CustomPurgeRequestDto request)
    {
        var cutoff = new CustomPurgeCutoff(request.KeepDays, request.BeforeDate);
        if (cutoff.Validate(TodayLocal) is { } error)
        {
            return BadRequest(ApiResult<int>.ValidationError(error));
        }

        try
        {
            return Ok(ApiResult<int>.SuccessResult(await overviewService.PreviewCustomAsync(rowKey, cutoff), "取得預覽成功"));
        }
        catch (ArgumentException ex) when (ex.ParamName == "rowKey")
        {
            return BadRequest(ApiResult<int>.FailureResult(ex.Message.Split(" (Parameter", 2)[0]));
        }
    }

    /// <summary>
    /// 「自訂清除」：刪除早於條件的資料（連同檔案），只影響這一次、不改系統參數。刪到資料才寫稽核（同 Server 版）。
    /// 已刪除資料途中有一筆失敗時 <see cref="CustomPurgeResultDto.Failed"/> 為 true（失敗前刪掉的照算）。
    /// </summary>
    [HttpPost("{rowKey}/custom-purge")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<CustomPurgeResultDto>>> CustomPurge(string rowKey, [FromBody] CustomPurgeRequestDto request)
    {
        var cutoff = new CustomPurgeCutoff(request.KeepDays, request.BeforeDate);
        if (cutoff.Validate(TodayLocal) is { } error)
        {
            return BadRequest(ApiResult<CustomPurgeResultDto>.ValidationError(error));
        }

        CustomPurgeOutcome outcome;
        try
        {
            // 不拿 RequestAborted：使用者關掉頁面時刪到一半停下來，稽核反而記不到已刪的筆數（Server 版也不中途取消）。
            outcome = await overviewService.PurgeCustomAsync(rowKey, cutoff, CancellationToken.None);
        }
        catch (ArgumentException ex) when (ex.ParamName == "rowKey")
        {
            return BadRequest(ApiResult<CustomPurgeResultDto>.FailureResult(ex.Message.Split(" (Parameter", 2)[0]));
        }

        if (outcome.Removed > 0)
        {
            await ViewAudit.WriteAsync(auditLogService, currentUserService, outcome.AuditAction, outcome.TargetType, "*", cutoff.AuditDetail(outcome.Removed));
        }

        logger.LogInformation("Custom purge completed. RowKey={RowKey}, Removed={Removed}, Left={Left}, Failed={Failed}",
            rowKey, outcome.Removed, outcome.Left, outcome.Failed);
        return Ok(ApiResult<CustomPurgeResultDto>.SuccessResult(new CustomPurgeResultDto
        {
            Removed = outcome.Removed,
            Left = outcome.Left,
            Failed = outcome.Failed,
        }, "清除完成"));
    }

    private DataRetentionRowDto ToDto(DataRetentionRow row) => new()
    {
        Key = row.Key,
        Label = row.Label,
        Note = row.Note,
        Count = row.Count,
        CountUnit = row.CountUnit,
        Folder = row.Folder is { } folder ? new DataRetentionFolderDto { Status = folder.Status.ToString(), Bytes = folder.Bytes } : null,
        OldestLocal = MaintenanceJobMapper.Unzoned(row.OldestLocal),
        RuleText = row.RuleText,
        ParameterGroup = row.ParameterGroup,
        Expected = row.Expected,
        ExpectedNote = row.ExpectedNote,
        JobName = row.JobName,
        Job = MaintenanceJobMapper.ToDto(row.Job, jobOverviewService),
        ScheduleText = row.ScheduleText,
        CanPurgeNow = row.CanPurgeNow,
        PurgeDisabledReason = row.PurgeDisabledReason,
        ManageUrl = row.ManageUrl,
        CanCustomPurge = row.CanCustomPurge,
        Error = row.Error,
    };
}
