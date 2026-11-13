using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Views.Admins;
using MyProject.Web.Auditing;
using MyProject.Web.Export;
using MyProject.Web.Filters;
using MyProject.Web.Streaming;

namespace MyProject.Web.Controllers;

/// <summary>
/// 系統例外紀錄的 Web API（0.10.5 起，WASM 遷移 M6），只限管理員（例外內容含帳號、頁面與堆疊）。
/// 查詢、明細、複製、匯出、刪除、清除、清空，以及 AI 例外分析（NDJSON 串流、可多輪追問）與 PDF 報告。
/// 稽核寫法與 Server 版頁面相同：按「查詢」一筆搜尋、看明細一筆查看；刪除、清除、清空、匯出、AI 分析、追問、匯出 PDF 各一筆。
///
/// <para>
/// AI 對話由畫面送回之前的問答，伺服器每一輪都用資料庫與堆疊檔重新組第一則「例外明細」，system 提示也由服務自己加：
/// 伺服器不保存對話（不怕 IIS 回收、不佔記憶體），畫面也改不到例外內容與系統提示。
/// </para>
/// </summary>
[Route("api/v1/exception-logs")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ExceptionLogsController : ControllerBase
{
    private const string TargetType = "ExceptionLog";
    private const string NotFoundMessage = "找不到這筆例外紀錄。";

    private readonly ExceptionLogService exceptionLogService;
    private readonly IAiExceptionAnalysisService aiService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ISystemIdentity systemIdentity;
    private readonly IOptionsMonitor<LogRetentionSettings> retentionOptions;
    private readonly ILogger<ExceptionLogsController> logger;

    public ExceptionLogsController(
        ExceptionLogService exceptionLogService,
        IAiExceptionAnalysisService aiService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ISystemIdentity systemIdentity,
        IOptionsMonitor<LogRetentionSettings> retentionOptions,
        ILogger<ExceptionLogsController> logger)
    {
        this.exceptionLogService = exceptionLogService;
        this.aiService = aiService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.systemIdentity = systemIdentity;
        this.retentionOptions = retentionOptions;
        this.logger = logger;
    }

    private int PurgeDays => retentionOptions.CurrentValue.ManualExceptionLogDays;

    /// <summary>清單；<see cref="ExceptionLogSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核（自由輸入的欄位只記有沒有填）。</summary>
    [HttpPost("search")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<PagedResult<ExceptionLogItemDto>>>> Search([FromBody] ExceptionLogSearchRequestDto request)
    {
        var result = await exceptionLogService.GetAsync(ToQuery(request, request.PageIndex, request.PageSize));
        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.ExceptionLog.Search, TargetType,
                AuditSearchCondition.Of("start", request.StartTime), AuditSearchCondition.Of("end", request.EndTime),
                AuditSearchCondition.Of("source", request.Source ?? string.Empty),
                AuditSearchCondition.Text("account", request.Account), AuditSearchCondition.Text("keyword", request.Keyword));
        }

        return Ok(ApiResult<PagedResult<ExceptionLogItemDto>>.SuccessResult(new PagedResult<ExceptionLogItemDto>
        {
            Items = result.Result.Select(ExceptionLogDtoMapper.ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "查詢例外紀錄成功"));
    }

    /// <summary>清除天數、匯出與複製的筆數上限，以及 AI 分析是否可用與追問輪數上限（設定可在系統參數頁改，所以每次都重讀）。</summary>
    [HttpGet("options")]
    [AdminOnly]
    public ActionResult<ApiResult<ExceptionLogOptionsDto>> Options()
        => Ok(ApiResult<ExceptionLogOptionsDto>.SuccessResult(new ExceptionLogOptionsDto
        {
            PurgeDays = PurgeDays,
            MaxRows = ExceptionLogService.MaxRows,
            AiAvailable = aiService.IsAvailable,
            AiUnavailableReason = aiService.UnavailableReason,
            MaxFollowUpRounds = aiService.MaxFollowUpRounds,
        }, "取得例外紀錄選項成功"));

    /// <summary>明細窗：一筆例外與它的完整堆疊（堆疊檔不存在時為 null）；寫一筆查看稽核。</summary>
    [HttpGet("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<ExceptionLogDetailDto>>> Detail(int id)
    {
        var item = await exceptionLogService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult<ExceptionLogDetailDto>.NotFoundResult(NotFoundMessage));
        }

        var detail = ExceptionLogDtoMapper.ToDto(item, new ExceptionLogDetailDto());
        detail.StackTrace = await ReadStackTraceAsync(id);
        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.ExceptionLog.View, TargetType, item.Id.ToString(), $"type={item.ExceptionType}");
        return Ok(ApiResult<ExceptionLogDetailDto>.SuccessResult(detail, "取得例外明細成功"));
    }

    /// <summary>「點列複製」用的堆疊。不寫稽核（與 Server 版相同：點列複製不算查看）。</summary>
    [HttpGet("{id:int}/stack-trace")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<ExceptionLogStackTraceDto>>> StackTrace(int id)
    {
        if (await exceptionLogService.GetByIdAsync(id) is null)
        {
            return NotFound(ApiResult<ExceptionLogStackTraceDto>.NotFoundResult(NotFoundMessage));
        }

        return Ok(ApiResult<ExceptionLogStackTraceDto>.SuccessResult(new ExceptionLogStackTraceDto { StackTrace = await ReadStackTraceAsync(id) }, "取得堆疊成功"));
    }

    /// <summary>
    /// 「複製目前查詢結果（含完整堆疊）」：目前查詢條件與排序下的全部資料（最多 <see cref="ExceptionLogService.MaxRows"/> 筆），
    /// 每筆與點列複製同格式。不寫稽核（與 Server 版相同）；超過 100 筆的確認在畫面上做。
    /// </summary>
    [HttpPost("copy-text")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<ExceptionLogCopyTextDto>>> CopyText([FromBody] ExceptionLogSearchRequestDto request)
    {
        var items = (await exceptionLogService.GetAsync(ToQuery(request, 1, ExceptionLogService.MaxRows))).Result.ToList();
        var stackTraces = await exceptionLogService.GetStackTracesAsync(items.Select(x => x.Id).ToList());
        var text = ExceptionLogClipboardText.BuildMany(items.Select(x => (x, stackTraces[x.Id])));
        return Ok(ApiResult<ExceptionLogCopyTextDto>.SuccessResult(new ExceptionLogCopyTextDto { Count = items.Count, Text = text }, "取得複製內容成功"));
    }

    /// <summary>匯出目前查詢條件與排序下的全部資料（CSV、UTF-8 含 BOM，最多 <see cref="ExceptionLogService.MaxRows"/> 筆），寫一筆匯出稽核。</summary>
    [HttpPost("export")]
    [AdminOnly]
    public async Task<IActionResult> Export([FromBody] ExceptionLogSearchRequestDto request)
    {
        var result = await exceptionLogService.GetAsync(ToQuery(request, 1, ExceptionLogService.MaxRows));
        var bytes = TabularExport.ToCsv(ExceptionLogExport.CsvColumns, result.Result);
        logger.LogInformation("Exception log export downloaded. Rows={Rows}", result.Count);

        // 稽核內容與 Server 版相同（筆數記查詢結果的總筆數）。
        await WriteAuditAsync(AuditActions.ExceptionLog.Export, "*", $"format=csv; rows={result.Count}");
        return File(bytes, TabularExport.CsvContentType, ExceptionLogExport.FileName(DateTime.Now));
    }

    /// <summary>刪除一列（連同堆疊檔）並寫一筆刪除稽核；「刪除選取」由畫面逐筆呼叫，每筆一列稽核（與 Server 版相同）。</summary>
    [HttpDelete("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        var item = await exceptionLogService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult.NotFoundResult("找不到要刪除的例外紀錄。"));
        }

        var result = await exceptionLogService.DeleteAsync(id);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.ExceptionLog.Delete, item.Id.ToString(), $"type={item.ShortExceptionType}");
        }

        return this.FromVerify(result, "刪除成功");
    }

    /// <summary>清除「最後發生」早於保留天數的紀錄（連同堆疊檔），寫一筆清除稽核；成功時回傳的訊息就是畫面要顯示的那一句。</summary>
    [HttpPost("purge")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Purge()
    {
        var days = PurgeDays;
        var result = await exceptionLogService.PurgeAsync(days);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.ExceptionLog.Purge, "*", $"清除 {days} 天未再發生的例外紀錄：{result.Message}");
        }

        return this.FromVerify(result, result.Message);
    }

    /// <summary>清空全部紀錄與整個堆疊目錄，寫一筆清空稽核。</summary>
    [HttpPost("clear-all")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> ClearAll()
    {
        var result = await exceptionLogService.ClearAllAsync();
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.ExceptionLog.ClearAll, "*", $"清空全部例外紀錄：{result.Message}");
        }

        return this.FromVerify(result, "已清空全部例外紀錄。");
    }

    /// <summary>
    /// AI 例外分析（第一次分析與每輪追問），回 NDJSON 串流（start、delta…、最後一定是 result）。
    /// 畫面只送例外明細之後的對話；第一則例外明細（去識別化、含完整堆疊）由伺服器重組，system 提示由服務自己加。
    /// 使用者關窗或按「停止產生」會中斷請求，AI 呼叫隨之取消；稽核一律在結果之後寫（含取消，與 Server 版相同）。
    /// </summary>
    [HttpPost("{id:int}/ai")]
    [AdminOnly]
    public async Task<IActionResult> Analyze(int id, [FromBody] ExceptionAiRequestDto request)
    {
        if (request.ConversationId == Guid.Empty)
        {
            return BadRequest(ApiResult.ValidationError("缺少對話識別碼。"));
        }

        if (AiExceptionConversation.Validate(request.Messages, aiService.MaxFollowUpRounds, endsWithQuestion: true) is { } invalid)
        {
            return BadRequest(ApiResult.ValidationError(invalid));
        }

        var item = await exceptionLogService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult.NotFoundResult(NotFoundMessage));
        }

        List<AiChatMessage> conversation =
        [
            new(AiChatRoles.User, AiExceptionPromptBuilder.Build(item, await ReadStackTraceAsync(id))),
            .. (request.Messages ?? []).Select(message => new AiChatMessage(message.Role, message.Content ?? string.Empty)),
        ];

        return new NdjsonResult<AiStreamFrameDto>(AnalyzeFrames(item, conversation, request.ConversationId, HttpContext.RequestAborted));
    }

    /// <summary>
    /// AI 例外分析的 PDF 報告：畫面送回對話（含哪些回覆被中止）、模型與累計用量，伺服器補上例外明細與堆疊（與 AI 實際收到的相同，不含帳號）。
    /// 寫一筆匯出 PDF 稽核。
    /// </summary>
    [HttpPost("{id:int}/ai/pdf")]
    [AdminOnly]
    public async Task<IActionResult> ExportAiPdf(int id, [FromBody] ExceptionAiPdfRequestDto request)
    {
        // 匯出不檢查追問輪數：對話途中管理員調低了 MaxFollowUpRounds，已經產生的對話仍要能匯出（輪數上限是為了控制 AI 費用，匯出不花費用）。
        // 內容大小仍受單則上限與請求大小上限保護。
        if (AiExceptionConversation.Validate(request.Messages, maxFollowUpRounds: int.MaxValue, endsWithQuestion: false) is { } invalid)
        {
            return BadRequest(ApiResult.ValidationError(invalid));
        }

        var item = await exceptionLogService.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult.NotFoundResult(NotFoundMessage));
        }

        var stackTrace = await ReadStackTraceAsync(id);
        var now = DateTime.Now;
        byte[] bytes;
        try
        {
            bytes = AiExceptionReportPdfBuilder.Build(new AiExceptionReportPdfRequest
            {
                SystemName = systemIdentity.Name,
                SystemVersion = systemIdentity.Version,
                OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                GeneratedAt = now,
                DetailLines = AiExceptionPromptBuilder.BuildDetailLines(item),
                StackTrace = stackTrace,
                // 產生途中停止的回覆加註「已中止」，PDF 才不會被當成完整報告。
                Exchanges = request.Messages
                    .Select(message => new AiChatMessage(message.Role, message.Interrupted ? AiInterruption.Annotate(message.Content ?? string.Empty) : message.Content ?? string.Empty))
                    .ToList(),
                ModelName = request.ModelName,
                Usage = AiTokenUsage.FromDto(request.Usage),
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "AI exception analysis PDF export failed because the embedded font is missing.");
            return this.ApiServerError("缺少內建中文字型，請確認建置產物完整。", ex);
        }

        var followUps = request.Messages.Count(message => message.Role == AiChatRoles.User);
        logger.LogInformation("AI exception analysis PDF exported. Bytes={Bytes}, FollowUps={FollowUps}", bytes.Length, followUps);

        // 數字與 Server 版相同：完整對話＝伺服器組的例外明細＋畫面送回的原文（不含「已中止」註記）。
        var first = AiExceptionPromptBuilder.Build(item, stackTrace);
        await WriteAiAuditAsync(AuditActions.ExceptionLog.AiExportPdf, item.Id, followUps, request.Messages.Count + 1,
            first.Length + request.Messages.Sum(message => (message.Content ?? string.Empty).Length), result: null);

        return File(bytes, "application/pdf", $"MyProject.Web-ai-exception-report-{now:yyyyMMdd-HHmmss}.pdf");
    }

    private async IAsyncEnumerable<AiStreamFrameDto> AnalyzeFrames(
        ExceptionLogAdapterModel item, List<AiChatMessage> conversation, Guid conversationId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 關窗等於取消：RequestAborted 讓 AI 服務收到取消並回一筆 Canceled 結果（不丟例外）。
        var round = conversation.Count(message => message.Role == AiChatRoles.User) - 1;
        var callContext = new AiExceptionCallContext(item.Id, item.ExceptionType, conversationId);

        yield return AiStreamFrames.Start();

        // 取消與未預期例外由 AiStreamGuard 收成最後一筆結果（0.10.5 獨立審查）：取消照寫稽核，未預期例外不寫（同 Server 版畫面外層的防護）。
        var guard = new AiStreamGuard();
        AiAnalysisResult result = null!;
        await foreach (var update in guard.RunAsync(aiService.AskStreamingAsync(conversation, callContext, cancellationToken), logger, "ExceptionAnalysis", cancellationToken))
        {
            if (update.Result is not null)
            {
                result = update.Result;
                break;
            }

            if (update.TextDelta.Length > 0)
            {
                yield return AiStreamFrames.Delta(update.TextDelta);
            }
        }
        // 使用者自己取消（關窗、停止產生）不是錯誤，不記警告（同 Server 版）。
        if (!result.Success && result.Reason != AiAnalysisFailureReason.Canceled)
        {
            logger.LogWarning(
                "AI exception analysis was not successful. Reason={Reason}, Round={Round}, ExceptionLogId={ExceptionLogId}",
                result.Reason, round, item.Id);
        }

        yield return AiStreamFrames.Result(result);

        // 稽核寫在結果之後：使用者中途中斷時 NdjsonResult 仍會把序列讀完，這一筆一定會寫。
        if (guard.Unexpected)
        {
            yield break;
        }

        await WriteAiAuditAsync(round == 0 ? AuditActions.ExceptionLog.AiAnalyze : AuditActions.ExceptionLog.AiFollowUp,
            item.Id, round, conversation.Count, conversation.Sum(message => message.Content.Length), result);
    }

    /// <summary>
    /// AI 分析、追問、匯出 PDF 的稽核（格式與 Server 版對話窗相同，見 <see cref="AiExceptionConversation.BuildAuditDetail"/>）。
    /// 寫入失敗不推翻「分析已經完成」這個事實，只記錯誤。
    /// </summary>
    private async Task WriteAiAuditAsync(string action, int exceptionLogId, int round, int messageCount, int characterCount, AiAnalysisResult? result)
    {
        try
        {
            var user = currentUserService.CurrentUser;
            await auditLogService.WriteAsync(
                action,
                success: result?.Success ?? true,
                actorUserId: user.Id,
                actorAccount: user.Account,
                targetType: TargetType,
                targetId: exceptionLogId.ToString(CultureInfo.InvariantCulture),
                detail: AiExceptionConversation.BuildAuditDetail(round, messageCount, characterCount, result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit log for AI exception analysis. Action={Action}", action);
        }
    }

    /// <summary>例外紀錄的維護動作本身也要留稽核（LOG-14，與 Server 版相同）。</summary>
    private Task WriteAuditAsync(string action, string targetId, string detail)
        => ViewAudit.WriteAsync(auditLogService, currentUserService, action, TargetType, targetId, detail);

    /// <summary>原始堆疊；檔案不存在或空白時回 null（畫面另外顯示說明文字，也不會把說明文字當堆疊送給 AI）。</summary>
    private async Task<string?> ReadStackTraceAsync(int id)
    {
        var stackTrace = await exceptionLogService.GetStackTraceAsync(id);
        return string.IsNullOrWhiteSpace(stackTrace) ? null : stackTrace;
    }

    /// <summary>查詢條件；排序只認畫面上可排序的兩欄（最後發生、次數），沒指定時依最後發生由新到舊。</summary>
    private static ExceptionLogQuery ToQuery(ExceptionLogSearchRequestDto request, int page, int pageSize) => new()
    {
        StartTime = request.StartTime,
        EndTime = request.EndTime,
        Source = request.Source ?? string.Empty,
        Account = request.Account ?? string.Empty,
        Keyword = request.Keyword ?? string.Empty,
        CurrentPage = page,
        PageSize = pageSize,
        SortField = request.SortBy switch
        {
            nameof(ExceptionLogAdapterModel.LastOccurredAt) => nameof(ExceptionLogAdapterModel.LastOccurredAt),
            nameof(ExceptionLogAdapterModel.OccurrenceCount) => nameof(ExceptionLogAdapterModel.OccurrenceCount),
            _ => string.Empty,
        },
        SortDescending = string.IsNullOrWhiteSpace(request.SortBy) ? null : request.SortDescending,
    };
}
