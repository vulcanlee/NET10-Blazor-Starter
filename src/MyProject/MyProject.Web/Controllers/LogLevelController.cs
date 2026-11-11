using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Web.Diagnostics;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 日誌等級設定的 Web API（0.10.5 起，WASM 遷移 M6），只限管理員。
/// 只改執行期的 NLog 萬用規則（<see cref="LogLevelRuntimeState"/>，Singleton），不寫回任何設定檔，重新啟動就回到 nlog.config 的等級。
/// 每次套用、還原各寫一筆稽核（與 Server 版頁面相同：targetType <c>NLog</c>、targetId <c>logger:*</c>）；調到 Trace／Debug 的確認窗在畫面上。
/// </summary>
[Route("api/v1/log-level")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public class LogLevelController : ControllerBase
{
    private readonly LogLevelRuntimeState logLevelState;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<LogLevelController> logger;

    public LogLevelController(
        LogLevelRuntimeState logLevelState,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<LogLevelController> logger)
    {
        this.logLevelState = logLevelState;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>目前生效等級、系統預設等級、有沒有執行期覆寫、NLog 設定是否可用（每次都讀真實狀態，不快取）。</summary>
    [HttpGet]
    public ActionResult<ApiResult<LogLevelStateDto>> Get()
        => Ok(ApiResult<LogLevelStateDto>.SuccessResult(State(), "取得日誌等級成功"));

    /// <summary>套用執行期等級，回傳套用後的狀態；寫一筆 <c>LogLevel.Apply</c>（「由 X 調整為 Y（僅執行期有效）」）。</summary>
    [HttpPost("apply")]
    public async Task<ActionResult<ApiResult<LogLevelStateDto>>> Apply([FromBody] LogLevelApplyRequestDto request)
    {
        var target = LogLevelRankHelper.FromLevelText(request.Level ?? string.Empty, LogLevelRank.Unknown);
        if (!LogLevelRankHelper.IsRealLevel(target))
        {
            return BadRequest(ApiResult<LogLevelStateDto>.ValidationError("日誌等級只能是 TRACE、DEBUG、INFO、WARN、ERROR、FATAL。"));
        }

        var previous = logLevelState.EffectiveLevel;
        if (!logLevelState.Apply(target))
        {
            return Conflict(ApiResult<LogLevelStateDto>.ConflictResult("套用失敗，請確認 NLog 設定。"));
        }

        await WriteAuditAsync(AuditActions.LogLevel.Apply, previous, target);
        return Ok(ApiResult<LogLevelStateDto>.SuccessResult(State(), "套用日誌等級成功"));
    }

    /// <summary>丟棄執行期覆寫、回到 nlog.config 的等級；沒有覆寫或 NLog 設定不可用時 409。寫一筆 <c>LogLevel.Restore</c>。</summary>
    [HttpPost("restore")]
    public async Task<ActionResult<ApiResult<LogLevelStateDto>>> Restore()
    {
        var previous = logLevelState.EffectiveLevel;
        if (!logLevelState.RestoreDefault())
        {
            return Conflict(ApiResult<LogLevelStateDto>.ConflictResult("還原失敗，請確認 NLog 設定。"));
        }

        await WriteAuditAsync(AuditActions.LogLevel.Restore, previous, logLevelState.EffectiveLevel);
        return Ok(ApiResult<LogLevelStateDto>.SuccessResult(State(), "還原日誌等級成功"));
    }

    private LogLevelStateDto State() => new()
    {
        IsAvailable = logLevelState.IsAvailable,
        EffectiveLevel = LogLevelRankHelper.ToLevelText(logLevelState.EffectiveLevel),
        SystemDefaultLevel = LogLevelRankHelper.ToLevelText(logLevelState.SystemDefaultLevel),
        HasOverride = logLevelState.OverrideLevel is not null,
    };

    /// <summary>稽核寫入失敗不應推翻「等級已經套用」這個事實，因此只記錯誤（與 Server 版相同）。</summary>
    private async Task WriteAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
    {
        try
        {
            var user = currentUserService.CurrentUser;
            await auditLogService.WriteAsync(
                action,
                success: true,
                actorUserId: user.Id,
                actorAccount: user.Account,
                targetType: "NLog",
                targetId: "logger:*",
                detail: $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} "
                    + $"調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit log for log level change. Action={Action}", action);
        }
    }
}
