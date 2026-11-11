using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Web.Ai;
using MyProject.Web.Components.Commons;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Filters;
using MyProject.Web.Streaming;

namespace MyProject.Web.Controllers;

/// <summary>
/// 日誌檢視的 Web API（0.10.5 起，WASM 遷移 M6），只限管理員（日誌內容可能含個資與內部細節）。
/// 查詢、匯出、AI 分析、AI 報告 PDF 都依同一份條件在伺服器重新讀日誌檔（<see cref="ILogQueryService"/>），
/// 匯出與 AI 不必把上萬筆日誌從瀏覽器送回來；畫面送的是上一次查詢「實際使用」的起訖，所以結果與畫面上的相同。
/// 稽核的動作、目標與摘要與 Server 版頁面相同：按「查詢」一筆搜尋（進頁自動查詢不記）、匯出一筆、AI 分析一筆（取消也記）、PDF 一筆。
/// 需要「現在」的（預設區間、快速區間、檔名、PDF 產生時間）一律用伺服器時間。
/// </summary>
[Route("api/v1/logs")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public class LogsController : ControllerBase
{
    private const string TargetType = "LogQuery";
    private const string InvalidConditionMessage = "查詢條件不正確：快速區間只能是 last5m、last15m、last1h、today，最低等級只能是 TRACE、DEBUG、INFO、WARN、ERROR、FATAL 或空白。";

    private readonly ILogQueryService logQueryService;
    private readonly IAiLogAnalysisService aiLogAnalysisService;
    private readonly IOptionsMonitor<AiSettings> aiOptions;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ISystemIdentity systemIdentity;
    private readonly ILogger<LogsController> logger;

    public LogsController(
        ILogQueryService logQueryService,
        IAiLogAnalysisService aiLogAnalysisService,
        IOptionsMonitor<AiSettings> aiOptions,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ISystemIdentity systemIdentity,
        ILogger<LogsController> logger)
    {
        this.logQueryService = logQueryService;
        this.aiLogAnalysisService = aiLogAnalysisService;
        this.aiOptions = aiOptions;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.systemIdentity = systemIdentity;
        this.logger = logger;
    }

    /// <summary>
    /// 查詢：條件超出範圍時調整並回傳警告（與 Server 版相同），回傳實際使用的起訖讓畫面寫回日期欄位。
    /// <see cref="LogSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核（關鍵字、追蹤碼只記有沒有填）。
    /// </summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<LogSearchResultDto>>> Search([FromBody] LogSearchRequestDto request)
    {
        if (ToQuery(request) is not { } query)
        {
            return BadRequest(ApiResult<LogSearchResultDto>.ValidationError(InvalidConditionMessage));
        }

        LogQueryResult result;
        try
        {
            result = await logQueryService.QueryAsync(query, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Log query failed.");
            return QueryFailed<LogSearchResultDto>("查詢日誌失敗", ex);
        }

        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.LogViewer.Search, TargetType,
                AuditSearchCondition.Of("start", result.AppliedStartTime), AuditSearchCondition.Of("end", result.AppliedEndTime),
                AuditSearchCondition.Of("minimumLevel", request.MinimumLevel ?? string.Empty), AuditSearchCondition.Of("take", request.Take),
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Text("traceCode", request.TraceCode),
                AuditSearchCondition.Of("rows", result.Entries.Count));
        }

        return Ok(ApiResult<LogSearchResultDto>.SuccessResult(new LogSearchResultDto
        {
            Entries = result.Entries.Select(ToDto).ToList(),
            TotalCount = result.Entries.Count,
            Message = result.Message,
            Warnings = result.Warnings,
            // ⚠️ 本地時間以「不帶時差」送出：帶了 +08:00，WASM 會換成瀏覽器的時區。
            AppliedStartTime = Unzoned(result.AppliedStartTime),
            AppliedEndTime = Unzoned(result.AppliedEndTime),
        }, "查詢日誌成功"));
    }

    /// <summary>匯出原始 nlog 文字（時間正序、UTF-8 含 BOM，與「複製查詢結果」同內容），寫一筆匯出稽核；檔名用伺服器時間，與 Server 版相同。</summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] LogSearchRequestDto request)
    {
        if (ToQuery(request) is not { } query)
        {
            return BadRequest(ApiResult<object>.ValidationError(InvalidConditionMessage));
        }

        LogQueryResult result;
        try
        {
            result = await logQueryService.QueryAsync(query, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Log export failed.");
            return QueryFailed<object>("匯出失敗", ex);
        }

        if (result.Entries.Count == 0)
        {
            return BadRequest(ApiResult<object>.ValidationError("目前沒有可匯出的日誌。"));
        }

        // 匯出檔的 BOM 一律走 TextDownloadPayload；自己接 UTF8Encoding 容易寫成「看起來有、其實沒有」。
        var bytes = TextDownloadPayload.Utf8WithBom(string.Join(Environment.NewLine, result.Entries.Select(entry => entry.Raw)));
        logger.LogInformation("Log export downloaded. Rows={Rows}, Bytes={Bytes}", result.Entries.Count, bytes.Length);
        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.LogViewer.Export, TargetType, null, $"format=log; rows={result.Entries.Count}");

        return File(bytes, "text/plain", $"MyProject.Web-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    /// <summary>
    /// AI 分析（NDJSON 串流，格式見 <see cref="AiStreamFrameDto"/>）：依同一份條件重新查詢，整份交給 AI 服務（服務只送最新 <c>AiSettings:MaxEntries</c> 筆）。
    /// 先送 start（總筆數、送出筆數、是否因上限截斷、起訖），之後逐段文字，最後一定是 result。
    /// 使用者關窗或按「停止產生」時瀏覽器中斷請求，AI 呼叫隨 <c>RequestAborted</c> 取消；稽核寫在 result 之後，所以取消也會記一筆（與 Server 版相同），摘要不含任何日誌內容。
    /// </summary>
    [HttpPost("ai")]
    public async Task<IActionResult> Analyze([FromBody] LogSearchRequestDto request)
    {
        if (ToQuery(request) is not { } query)
        {
            return BadRequest(ApiResult<object>.ValidationError(InvalidConditionMessage));
        }

        LogQueryResult result;
        try
        {
            result = await logQueryService.QueryAsync(query, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Log query for AI analysis failed.");
            return QueryFailed<object>("查詢日誌失敗", ex);
        }

        return new NdjsonResult<AiStreamFrameDto>(AnalyzeFramesAsync(result, HttpContext.RequestAborted));
    }

    /// <summary>
    /// AI 報告匯出 PDF：報告內文、模型、用量、耗時由畫面送回；附錄的提示詞依同一份條件重新查詢後重建，產生時間與檔名用伺服器時間。
    /// 寫一筆 <c>LogViewer.AiAnalyzeExportPdf</c>（摘要與分析時相同格式）。
    /// </summary>
    [HttpPost("ai/pdf")]
    public async Task<IActionResult> ExportAiReportPdf([FromBody] LogAiReportPdfRequestDto request)
    {
        if (ToQuery(request.Query) is not { } query || !Enum.TryParse<AiAnalysisFailureReason>(request.Reason, out var reason) || !Enum.IsDefined(reason))
        {
            return BadRequest(ApiResult<object>.ValidationError(InvalidConditionMessage));
        }

        try
        {
            var queried = await logQueryService.QueryAsync(query, HttpContext.RequestAborted);
            var prompt = AiLogPromptBuilder.Build(queried.Entries, aiOptions.CurrentValue.MaxEntries);
            var usage = AiTokenUsage.FromDto(request.Usage);
            var bytes = AiReportPdfBuilder.Build(new AiReportPdfRequest
            {
                SystemName = systemIdentity.Name,
                SystemVersion = systemIdentity.Version,
                OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                GeneratedAt = DateTime.Now,
                QueryStartTime = queried.AppliedStartTime,
                QueryEndTime = queried.AppliedEndTime,
                MinimumLevel = request.Query.MinimumLevel ?? string.Empty,
                Keyword = request.Query.Keyword ?? string.Empty,
                Markdown = request.Markdown,
                Prompt = prompt,
                Usage = usage,
                ModelName = request.ModelName,
            });

            logger.LogInformation("AI analysis PDF exported. Bytes={Bytes}, Entries={Entries}", bytes.Length, prompt.IncludedEntryCount);
            await WriteAiAuditAsync(AuditActions.LogViewer.AiAnalyzeExportPdf, new AiAnalysisResult
            {
                Success = request.Success,
                Reason = reason,
                Prompt = prompt,
                ModelName = request.ModelName,
                Usage = usage,
                Elapsed = TimeSpan.FromMilliseconds(request.ElapsedMilliseconds),
            }, queried.AppliedStartTime, queried.AppliedEndTime);

            return File(bytes, "application/pdf", $"MyProject.Web-ai-log-report-{DateTime.Now:yyyyMMdd-HHmmss}.pdf");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "AI analysis PDF export failed because the embedded font is missing.");
            return this.ApiServerError<object>("PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。" + TraceCode.Suffix(HttpContext.TraceIdentifier), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AI analysis PDF export failed.");
            return this.ApiServerError<object>($"PDF 匯出失敗：{ex.GetType().Name}。" + TraceCode.Suffix(HttpContext.TraceIdentifier), ex);
        }
    }

    /// <summary>AI 分析是否可用（按鈕的停用與提示文字）。</summary>
    [HttpGet("options")]
    public ActionResult<ApiResult<LogViewerOptionsDto>> Options()
        => Ok(ApiResult<LogViewerOptionsDto>.SuccessResult(new LogViewerOptionsDto
        {
            AiAvailable = aiLogAnalysisService.IsAvailable,
            AiUnavailableReason = aiLogAnalysisService.UnavailableReason,
        }, "取得日誌檢視選項成功"));

    private async IAsyncEnumerable<AiStreamFrameDto> AnalyzeFramesAsync(LogQueryResult query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entries = query.Entries;

        // 與 AiLogPromptBuilder 相同的規則（設定被改成 0 或負數時至少送 1 筆），開始前就讓畫面知道送多少筆。
        var included = Math.Min(entries.Count, Math.Max(1, aiOptions.CurrentValue.MaxEntries));
        yield return AiStreamFrames.Start(new AiStreamScopeDto
        {
            TotalEntryCount = entries.Count,
            IncludedEntryCount = included,
            DroppedByEntryLimit = included < entries.Count,
            StartLocal = Unzoned(query.AppliedStartTime),
            EndLocal = Unzoned(query.AppliedEndTime),
        });

        // 取消與未預期例外由 AiStreamGuard 收成最後一筆結果（0.10.5 獨立審查）：取消照寫稽核，未預期例外不寫（同 Server 版）。
        var guard = new AiStreamGuard();
        AiAnalysisResult result = null!;
        await foreach (var update in guard.RunAsync(aiLogAnalysisService.AnalyzeStreamingAsync(entries, cancellationToken), logger, "LogAnalysis", cancellationToken))
        {
            if (update.Result is not null)
            {
                result = update.Result;
                break;
            }

            yield return AiStreamFrames.Delta(update.TextDelta);
        }

        yield return AiStreamFrames.Result(result);

        // Server 版遇到未預期例外時不寫稽核（只記錯誤），這裡相同。
        if (!guard.Unexpected)
        {
            await WriteAiAuditAsync(AuditActions.LogViewer.AiAnalyze, result, query.AppliedStartTime, query.AppliedEndTime);
        }
    }

    /// <summary>
    /// 稽核寫入失敗不應推翻「分析已經完成」這個事實，因此只記錯誤。
    /// ⚠️ detail 只寫數量、模型、用量與耗時，<b>絕不</b>寫入任何日誌內容或 AI 輸出：AuditLog 的可視範圍比本頁更廣。
    /// </summary>
    private async Task WriteAiAuditAsync(string action, AiAnalysisResult result, DateTime start, DateTime end)
    {
        try
        {
            var user = currentUserService.CurrentUser;
            await auditLogService.WriteAsync(
                action,
                success: result.Success,
                actorUserId: user.Id,
                actorAccount: user.Account,
                targetType: TargetType,
                targetId: $"{start:yyyyMMddHHmmss}-{end:yyyyMMddHHmmss}",
                detail: BuildAuditDetail(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit log for AI log analysis. Action={Action}", action);
        }
    }

    private static string BuildAuditDetail(AiAnalysisResult result)
    {
        var builder = new StringBuilder();
        builder.Append("送出 ").Append(result.Prompt.IncludedEntryCount)
            .Append('/').Append(result.Prompt.TotalEntryCount)
            .Append(" 筆、").Append(result.Prompt.CharacterCount).Append(" 字元");

        if (string.IsNullOrWhiteSpace(result.ModelName) == false)
        {
            builder.Append("；模型 ").Append(result.ModelName);
        }

        var usage = AiTokenUsage.ToDisplayText(result.Usage);
        if (string.IsNullOrEmpty(usage) == false)
        {
            builder.Append("；用量 ").Append(usage);
        }

        builder.Append("；耗時 ").Append(result.Elapsed.TotalMilliseconds.ToString("F0")).Append(" ms");

        if (result.Success == false)
        {
            builder.Append("；失敗原因 ").Append(result.Reason);
        }

        return builder.ToString();
    }

    /// <summary>畫面的條件轉成查詢；快速區間代碼或等級不認得時回 null。沒帶起訖時用伺服器的「現在」（與 Server 版相同：起 = 現在 − 1 小時、訖 = 現在）。</summary>
    private static LogQueryRequest? ToQuery(LogSearchRequestDto request)
    {
        var level = request.MinimumLevel ?? string.Empty;
        var rank = LogLevelRankHelper.FromLevelText(level, LogLevelRank.Unknown);
        if (level.Length > 0 && rank == LogLevelRank.Unknown)
        {
            return null;
        }

        var now = DateTime.Now;
        (DateTime Start, DateTime End)? range = request.QuickRange switch
        {
            null or "" => (request.StartTime ?? now.AddHours(-1), request.EndTime ?? now),
            LogQuickRanges.Last5Minutes => LogTimeRanges.Last(TimeSpan.FromMinutes(5), now),
            LogQuickRanges.Last15Minutes => LogTimeRanges.Last(TimeSpan.FromMinutes(15), now),
            LogQuickRanges.LastHour => LogTimeRanges.Last(TimeSpan.FromHours(1), now),
            LogQuickRanges.Today => LogTimeRanges.Today(now),
            _ => null,
        };

        if (range is not { } applied)
        {
            return null;
        }

        return new LogQueryRequest
        {
            StartTime = applied.Start,
            EndTime = applied.End,
            Take = request.Take,
            MinimumLevel = level.Length == 0 ? LogLevelRank.Any : rank,
            Keyword = request.Keyword ?? string.Empty,
            TraceCode = request.TraceCode ?? string.Empty,
        };
    }

    private static LogEntryDto ToDto(LogEntry entry) => new()
    {
        Sequence = entry.Sequence,
        Timestamp = Unzoned(entry.Timestamp),
        TraceId = entry.TraceId,
        Level = entry.Level,
        ThreadId = entry.ThreadId,
        Logger = entry.Logger,
        Message = entry.Message,
        Raw = entry.Raw,
        HasException = entry.HasException,
    };

    /// <summary>讀日誌檔失敗（與 Server 版相同的訊息：例外類型＋錯誤追蹤碼）。</summary>
    private ObjectResult QueryFailed<T>(string prefix, Exception ex)
        => this.ApiServerError<T>($"{prefix}：{ex.GetType().Name}。" + TraceCode.Suffix(HttpContext.TraceIdentifier), ex);

    private static DateTime Unzoned(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
