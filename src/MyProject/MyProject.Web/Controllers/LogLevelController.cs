using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Web.Diagnostics;
using MyProject.Web.Filters;
using MyProject.Web.Auditing;

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
    private readonly ClientDiagnosticsRegistry diagnosticsRegistry;
    private readonly MyUserService userService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LogLevelController> logger;

    public LogLevelController(
        LogLevelRuntimeState logLevelState,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ClientDiagnosticsRegistry diagnosticsRegistry,
        MyUserService userService,
        TimeProvider timeProvider,
        ILogger<LogLevelController> logger)
    {
        this.logLevelState = logLevelState;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.diagnosticsRegistry = diagnosticsRegistry;
        this.userService = userService;
        this.timeProvider = timeProvider;
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

    /// <summary>開著的前端診斷記錄（0.10.37 起；到期的不列）。</summary>
    [HttpGet("client-diagnostics")]
    public ActionResult<ApiResult<List<ClientDiagnosticsSessionDto>>> ClientDiagnostics()
        => Ok(ApiResult<List<ClientDiagnosticsSessionDto>>.SuccessResult(diagnosticsRegistry.List().Select(ToDto).ToList(), "取得前端診斷記錄成功"));

    /// <summary>
    /// 對某位使用者開啟前端診斷記錄（0.10.37 起）：那位使用者瀏覽器裡的日誌（DEBUG 或 TRACE 以上）在時限內送到伺服器、依原等級寫進日誌檔。
    /// 只在執行期有效（重新啟動就關閉）；同一位使用者已經開著就以這次的設定取代。寫一筆 <c>LogLevel.ClientDiagnosticsStart</c>。
    /// </summary>
    [HttpPost("client-diagnostics")]
    public async Task<ActionResult<ApiResult<ClientDiagnosticsSessionDto>>> StartClientDiagnostics([FromBody] ClientDiagnosticsStartRequestDto request)
    {
        LogLevel? level = request.Level?.Trim().ToUpperInvariant() switch
        {
            "DEBUG" => LogLevel.Debug,
            "TRACE" => LogLevel.Trace,
            _ => null,
        };
        if (level is null)
        {
            return BadRequest(ApiResult<ClientDiagnosticsSessionDto>.ValidationError("前端診斷的等級只能是 DEBUG 或 TRACE。"));
        }

        if (!ClientDiagnosticsRegistry.DurationMinutes.Contains(request.DurationMinutes))
        {
            return BadRequest(ApiResult<ClientDiagnosticsSessionDto>.ValidationError(
                $"時間只能是 {string.Join("、", ClientDiagnosticsRegistry.DurationMinutes)} 分鐘。"));
        }

        if (await userService.FindByAccountAsync(request.Account ?? string.Empty) is not { } target)
        {
            return NotFound(ApiResult<ClientDiagnosticsSessionDto>.NotFoundResult("找不到這個帳號。"));
        }

        var admin = currentUserService.CurrentUser;
        var session = diagnosticsRegistry.Enable(target.Id, target.Account, level.Value, TimeSpan.FromMinutes(request.DurationMinutes), admin.Account);
        var dto = ToDto(session);
        await WriteDiagnosticsAuditAsync(AuditActions.LogLevel.ClientDiagnosticsStart, target.Id,
            $"對 {target.Account} 開啟前端診斷記錄 {dto.Level}，至 {dto.ExpiresAtLocal:yyyy/MM/dd HH:mm}（僅執行期有效）。");
        return Ok(ApiResult<ClientDiagnosticsSessionDto>.SuccessResult(dto, "已開啟前端診斷記錄"));
    }

    /// <summary>提前關閉某位使用者的前端診斷記錄（0.10.37 起）；沒開著回 409。寫一筆 <c>LogLevel.ClientDiagnosticsStop</c>。</summary>
    [HttpDelete("client-diagnostics/{userId:int}")]
    public async Task<ActionResult<ApiResult<object>>> StopClientDiagnostics(int userId)
    {
        var session = diagnosticsRegistry.Find(userId);
        if (session is null || !diagnosticsRegistry.Disable(userId))
        {
            return Conflict(ApiResult<object>.ConflictResult("這位使用者的前端診斷記錄已經關閉（或已到期）。"));
        }

        await WriteDiagnosticsAuditAsync(AuditActions.LogLevel.ClientDiagnosticsStop, userId, $"關閉 {session.Account} 的前端診斷記錄。");
        return Ok(ApiResult<object>.SuccessResult(new { }, "已關閉前端診斷記錄"));
    }

    private ClientDiagnosticsSessionDto ToDto(ClientDiagnosticsSession session) => new()
    {
        UserId = session.UserId,
        Account = session.Account,
        Level = session.Level == LogLevel.Trace ? "TRACE" : "DEBUG",
        StartedAtLocal = timeProvider.ToLocalUnzoned(session.StartedAt),
        ExpiresAtLocal = timeProvider.ToLocalUnzoned(session.ExpiresAt),
        EnabledByAccount = session.EnabledByAccount,
    };

    /// <summary>稽核寫入失敗不應推翻「已經開啟／關閉」這個事實（<see cref="ViewAudit"/>）。</summary>
    private Task WriteDiagnosticsAuditAsync(string action, int targetUserId, string detail)
        => ViewAudit.WriteAsync(auditLogService, currentUserService, action, "MyUser",
            targetUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), detail);

    private LogLevelStateDto State() => new()
    {
        IsAvailable = logLevelState.IsAvailable,
        EffectiveLevel = LogLevelRankHelper.ToLevelText(logLevelState.EffectiveLevel),
        SystemDefaultLevel = LogLevelRankHelper.ToLevelText(logLevelState.SystemDefaultLevel),
        HasOverride = logLevelState.OverrideLevel is not null,
    };

    /// <summary>稽核寫入失敗不應推翻「等級已經套用」這個事實（<see cref="ViewAudit"/>，與 Server 版相同）。</summary>
    private Task WriteAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
        => ViewAudit.WriteAsync(auditLogService, currentUserService, action, "NLog", "logger:*",
            $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} 調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。");
}
