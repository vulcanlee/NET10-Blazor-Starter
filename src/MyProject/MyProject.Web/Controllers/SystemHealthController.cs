using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Email;
using MyProject.Web.Filters;
using MyProject.Web.Health;

namespace MyProject.Web.Controllers;

/// <summary>
/// 系統健康監控頁的 Web API（0.10.6 起，WASM 遷移 M7）：只限管理員。每個動作一個端點，規則、訊息與稽核都在原本的服務裡
/// （<see cref="ISystemHealthService"/>、<see cref="EmailTestService"/>、<see cref="NotificationTestService"/>），與 Server 版頁面相同。
/// ⚠️ <c>GET</c> 報告每次都會執行全部檢查；AI 已設定時「LLM API」一項會真的呼叫模型（產生費用，記進 Token 用量）。
/// </summary>
[Route("api/v1/system-health")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public sealed class SystemHealthController : ControllerBase
{
    private readonly ISystemHealthService systemHealthService;
    private readonly ILogQueryService logQueryService;
    private readonly EmailTestService emailTestService;
    private readonly NotificationTestService notificationTestService;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemHealthController> logger;

    public SystemHealthController(
        ISystemHealthService systemHealthService,
        ILogQueryService logQueryService,
        EmailTestService emailTestService,
        NotificationTestService notificationTestService,
        IOptionsMonitor<EmailSettings> emailOptions,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<SystemHealthController> logger)
    {
        this.systemHealthService = systemHealthService;
        this.logQueryService = logQueryService;
        this.emailTestService = emailTestService;
        this.notificationTestService = notificationTestService;
        this.emailOptions = emailOptions;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>健康報告（全部檢查項目）與寄信測試區的設定。</summary>
    [HttpGet]
    public async Task<ActionResult<ApiResult<SystemHealthReportDto>>> Report()
    {
        var report = await systemHealthService.GetReportAsync(HttpContext.RequestAborted);
        var isEmailEnabled = emailOptions.CurrentValue.TryGetProvider(out var provider) && provider != EmailProvider.None;

        var dto = new SystemHealthReportDto
        {
            // Server 版顯示 CheckedAt.ToLocalTime()：換成伺服器本地時間、不帶時差送出。
            CheckedAtLocal = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(report.CheckedAt, timeProvider.LocalTimeZone).DateTime, DateTimeKind.Unspecified),
            Score = report.Score,
            Status = report.Status.ToString(),
            Light = report.Light.ToString(),
            Items = report.Items.Select(x => new SystemHealthItemDto
            {
                Name = x.Name,
                Category = x.Category,
                Weight = x.Weight,
                Status = x.Status.ToString(),
                Light = x.Light.ToString(),
                Evidence = x.Evidence,
                FailureMessage = x.FailureMessage,
            }).ToList(),
            IsEmailEnabled = isEmailEnabled,
            EmailProviderText = isEmailEnabled ? provider.ToString() : "None（未啟用）",
            DefaultTestEmailTo = currentUserService.CurrentUser.Email,
        };
        logger.LogInformation("System health report generated from the API. Score={Score}", report.Score);
        return Ok(ApiResult<SystemHealthReportDto>.SuccessResult(dto, "取得系統健康報告成功"));
    }

    /// <summary>
    /// 最近 24 小時 WARN 以上日誌（新到舊，沿用日誌檢視的上限）。讀取失敗只影響這一區：仍回 200，原因放在 <see cref="SystemHealthWarnLogsDto.EmptyText"/>（與 Server 版相同）。
    /// 「現在」用伺服器時間。
    /// </summary>
    [HttpGet("warn-logs")]
    public async Task<ActionResult<ApiResult<SystemHealthWarnLogsDto>>> WarnLogs()
    {
        var now = timeProvider.GetLocalNow().DateTime;
        var dto = new SystemHealthWarnLogsDto();

        try
        {
            var result = await logQueryService.QueryAsync(new LogQueryRequest
            {
                StartTime = now.AddHours(-24),
                EndTime = now,
                MinimumLevel = LogLevelRank.Warn,
                Take = LogQueryRequest.MaxTake,
            }, HttpContext.RequestAborted);

            dto.Entries = Enumerable.Reverse(result.Entries).Select(ToDto).ToList();
            dto.Warnings = result.Warnings.ToList();
            dto.Message = $"{result.AppliedStartTime:MM/dd HH:mm} ～ {result.AppliedEndTime:MM/dd HH:mm}，共 {dto.Entries.Count} 筆 WARN、ERROR、FATAL（新到舊）。";

            // 服務的訊息（目錄不存在、區間內沒有日誌檔等）比通用文字更能說明為什麼是空的。
            dto.EmptyText = string.IsNullOrWhiteSpace(result.Message)
                ? "最近 24 小時沒有 WARN 以上的日誌紀錄。"
                : result.Message;

            if (dto.Entries.Count >= LogQueryRequest.MaxTake)
            {
                dto.Warnings.Add($"僅顯示最新 {LogQueryRequest.MaxTake} 筆，完整內容請到「日誌檢視」查詢。");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "System health API failed to load warning logs.");
            dto = new SystemHealthWarnLogsDto
            {
                EmptyText = $"讀取日誌失敗：{ex.GetType().Name}。{TraceCode.Suffix(HttpContext.TraceIdentifier)}",
            };
        }

        return Ok(ApiResult<SystemHealthWarnLogsDto>.SuccessResult(dto, "取得警告日誌成功"));
    }

    /// <summary>寄出測試信（同步寄送，當場知道成敗）；稽核由 <see cref="EmailTestService"/> 寫，只記 provider 與成敗。</summary>
    [HttpPost("test-email")]
    public async Task<ActionResult<ApiResult<SystemHealthTestResultDto>>> TestEmail([FromBody] SystemHealthTestEmailRequestDto request)
    {
        var user = currentUserService.CurrentUser;
        var result = await emailTestService.SendAsync(request.To, user.Id, user.Account, HttpContext.RequestAborted);
        return Ok(ApiResult<SystemHealthTestResultDto>.SuccessResult(ToDto(result), "寄信測試完成"));
    }

    /// <summary>發一則「測試通知」給全體管理員（不寄信）；稽核由 <see cref="NotificationTestService"/> 寫。</summary>
    [HttpPost("test-notification")]
    public async Task<ActionResult<ApiResult<SystemHealthTestResultDto>>> TestNotification()
    {
        var user = currentUserService.CurrentUser;
        var result = await notificationTestService.SendAsync(user.Id, user.Account, HttpContext.RequestAborted);
        return Ok(ApiResult<SystemHealthTestResultDto>.SuccessResult(ToDto(result), "通知測試完成"));
    }

    private static SystemHealthTestResultDto ToDto(VerifyRecordResult result) => new() { Success = result.Success, Message = result.Message };

    private static LogEntryDto ToDto(LogEntry entry) => new()
    {
        Sequence = entry.Sequence,
        Timestamp = DateTime.SpecifyKind(entry.Timestamp, DateTimeKind.Unspecified),
        TraceId = entry.TraceId,
        Level = entry.Level,
        ThreadId = entry.ThreadId,
        Logger = entry.Logger,
        Message = entry.Message,
        Raw = entry.Raw,
        HasException = entry.HasException,
    };
}
