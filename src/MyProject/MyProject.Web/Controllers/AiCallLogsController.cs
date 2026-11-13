using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Auditing;
using MyProject.Web.Diagnostics;
using MyProject.Web.Export;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// AI 對話紀錄的 Web API（0.10.5 起，WASM 遷移 M6）：只限管理員（內容含日誌、例外堆疊與使用者帳號）。
/// 一律走畫面同一個 <see cref="AiCallLogService"/>；稽核的動作、目標、摘要與詳細內容和 Server 版頁面相同：
/// 按「查詢」一筆搜尋、開明細一筆查看；下載 JSON、PDF（單筆、合併）、CSV 各一筆匯出；刪除、批次清除、清空各一筆（寫在刪除之後）。
/// </summary>
[Route("api/v1/ai-call-logs")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class AiCallLogsController : ControllerBase
{
    private const string TargetType = "AiCallLog";
    private const string NotFoundMessage = "找不到這筆對話紀錄（可能已被刪除）。";
    private const string ExportItemsNotFoundMessage = "找不到要匯出的紀錄（可能已被清除），請重新整理。";

    /// <summary>合併匯出的三個來源（稽核摘要的 source）。</summary>
    private const string SourceSelected = "selected";
    private const string SourceConversation = "conversation";
    private const string SourceQuery = "query";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AiCallLogService service;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ISystemIdentity systemIdentity;
    private readonly ILogger<AiCallLogsController> logger;

    public AiCallLogsController(
        AiCallLogService service,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ISystemIdentity systemIdentity,
        ILogger<AiCallLogsController> logger)
    {
        this.service = service;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.systemIdentity = systemIdentity;
        this.logger = logger;
    }

    /// <summary>
    /// 清單。填了內文關鍵字時先逐檔掃描（最多 <see cref="AiCallLogService.ContentScanLimit"/> 筆，使用者離開頁面就停止），結果的 Id 一起回傳；
    /// 之後換頁、排序、匯出由畫面帶回 <see cref="AiCallLogSearchRequestDto.ContentIds"/>。
    /// <see cref="AiCallLogSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核（自由輸入的欄位只記有沒有填）。
    /// </summary>
    [HttpPost("search")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AiCallLogPageDto>>> Search([FromBody] AiCallLogSearchRequestDto request, CancellationToken cancellationToken)
    {
        // 搜尋稽核記的是使用者送出的條件，寫在掃描之前（0.10.5 獨立審查）：內文掃描失敗時畫面會改用其他條件重查（不再記稽核），
        // 稽核仍要記「內文有填」，與 Server 版相同。
        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.AiCallLog.Search, TargetType,
                AuditSearchCondition.Of("start", request.StartDate), AuditSearchCondition.Of("end", request.EndDate),
                AuditSearchCondition.Of("operation", request.Operation ?? string.Empty),
                AuditSearchCondition.Of("success", request.Success switch { true => "true", false => "false", _ => string.Empty }),
                AuditSearchCondition.Of("model", request.Model ?? string.Empty), AuditSearchCondition.Text("account", request.Account),
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Text("content", request.ContentKeyword));
        }

        var query = ToQuery(request, request.PageIndex, request.PageSize);
        AiCallLogContentSearchResult? scan = null;
        var contentKeyword = request.ContentKeyword?.Trim() ?? string.Empty;
        if (contentKeyword.Length > 0)
        {
            query.Ids = null;
            query.ContentKeyword = contentKeyword;
            try
            {
                scan = await service.FindContentMatchesAsync(query, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 使用者離開頁面：瀏覽器已經不等這個回應了。
                logger.LogInformation("AI call log content search canceled by the client.");
                return StatusCode(499, ApiResult<AiCallLogPageDto>.FailureResult("已取消。", 499));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log content search failed.");
                return this.ApiServerError<AiCallLogPageDto>($"內文搜尋失敗：{ex.GetType().Name}。", ex);
            }

            query.Ids = scan.Ids;
        }

        var result = await service.GetAsync(query);
        return Ok(ApiResult<AiCallLogPageDto>.SuccessResult(new AiCallLogPageDto
        {
            Items = result.Result.Select(AiCallLogDtoMapper.ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
            ContentSearch = scan is null ? null : new AiCallLogContentSearchDto { Ids = scan.Ids.ToList(), Scanned = scan.Scanned, Truncated = scan.Truncated },
        }, "查詢 AI 對話紀錄成功"));
    }

    /// <summary>查詢條件的下拉選項（資料庫裡出現過的作業與模型）、記錄開關、保留天數與內文搜尋的掃描上限。</summary>
    [HttpGet("options")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AiCallLogOptionsDto>>> Options()
    {
        var options = await service.GetFilterOptionsAsync();
        return Ok(ApiResult<AiCallLogOptionsDto>.SuccessResult(new AiCallLogOptionsDto
        {
            Operations = options.Operations,
            Models = options.Models,
            Enabled = service.IsEnabled,
            RetentionDays = service.RetentionDays,
            ContentScanLimit = AiCallLogService.ContentScanLimit,
        }, "取得 AI 對話紀錄選項成功"));
    }

    /// <summary>以呼叫識別碼找紀錄 Id（Token 用量明細「AI 對話紀錄」的深連結 <c>/ai-call-logs?callId=…</c>）。</summary>
    [HttpGet("by-call/{callId:guid}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<int>>> ByCall(Guid callId)
    {
        var id = await service.FindIdByCallIdAsync(callId);
        return id is { } found
            ? Ok(ApiResult<int>.SuccessResult(found, "找到對話紀錄"))
            : NotFound(ApiResult<int>.NotFoundResult("找不到這次呼叫的對話紀錄（可能已被清除、超過保留天數，或當時未啟用記錄）。"));
    }

    /// <summary>
    /// 明細窗：中繼資料、用量、同一次執行與同一段對話，以及五種檢視的內容（每段只送前 <see cref="AiCallLogDetailDto.MaxTextCharacters"/> 字元）；
    /// 寫一筆查看稽核（與 Server 版相同，在同一段對話間切換也各寫一筆）。
    /// </summary>
    [HttpGet("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AiCallLogDetailDto>>> Detail(int id)
    {
        var detail = await service.GetDetailAsync(id);
        if (detail is null)
        {
            return NotFound(ApiResult<AiCallLogDetailDto>.NotFoundResult(NotFoundMessage));
        }

        var content = detail.Content;
        var dto = new AiCallLogDetailDto
        {
            Item = AiCallLogDtoMapper.ToDto(detail.Item),
            HasContent = content is not null,
            ExceptionType = content?.ExceptionType,
            Endpoint = content?.Endpoint,
            RequestedModel = content?.RequestedModel,
            UsageText = UsageText(detail),
            Conversation = detail.Conversation.Select(AiCallLogDtoMapper.ToDto).ToList(),
            Run = detail.Run.Select(AiCallLogDtoMapper.ToDto).ToList(),
        };

        if (content is not null)
        {
            dto.Messages = detail.Messages?.Select(message => new AiCallLogMessageDto { Role = message.Role, Content = Clip(message.Content) }).ToList();
            dto.Response = Clip(content.ResponseText);
            dto.RawRequest = Clip(PrettyJson(content.RequestBody));
            dto.RawResponse = content.ResponseBody is null ? null : Clip(PrettyJson(content.ResponseBody));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.AiCallLog.View, TargetType, id.ToString(), null);
        return Ok(ApiResult<AiCallLogDetailDto>.SuccessResult(dto, "取得 AI 對話紀錄明細成功"));
    }

    /// <summary>
    /// 明細窗某一段的完整內容（「複製」被截斷的段落時用；與 Server 版的複製相同，不另寫稽核）。
    /// <paramref name="part"/>：<c>message-{序號}</c>、<c>response</c>、<c>raw-request</c>、<c>raw-response</c>。
    /// </summary>
    [HttpGet("{id:int}/text")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<AiCallLogTextDto>>> Text(int id, [FromQuery] string part)
    {
        var detail = await service.GetDetailAsync(id);
        if (detail is null)
        {
            return NotFound(ApiResult<AiCallLogTextDto>.NotFoundResult(NotFoundMessage));
        }

        if (detail.Content is not { } content)
        {
            return NotFound(ApiResult<AiCallLogTextDto>.NotFoundResult("內容檔不存在，無法讀取。"));
        }

        string? text = part switch
        {
            "response" => content.ResponseText,
            "raw-request" => PrettyJson(content.RequestBody),
            "raw-response" => content.ResponseBody is null ? string.Empty : PrettyJson(content.ResponseBody),
            _ when part.StartsWith("message-", StringComparison.Ordinal)
                && int.TryParse(part["message-".Length..], out var index)
                && detail.Messages is { } messages
                && index >= 0 && index < messages.Count => messages[index].Content,
            _ => null,
        };

        return text is null
            ? BadRequest(ApiResult<AiCallLogTextDto>.ValidationError("找不到這段內容。"))
            : Ok(ApiResult<AiCallLogTextDto>.SuccessResult(new AiCallLogTextDto { Text = text, Length = text.Length }, "取得內容成功"));
    }

    /// <summary>
    /// 下載完整紀錄（內容檔原文，UTF-8 不加 BOM：RFC 8259 不允許），寫一筆匯出稽核。
    /// 回應的型別是 <c>application/octet-stream</c>：<c>application/json</c> 會被 WASM 的下載函式當成錯誤回應解析，畫面存檔時才標成 JSON。
    /// </summary>
    [HttpPost("{id:int}/raw")]
    [AdminOnly]
    public async Task<IActionResult> Raw(int id)
    {
        var item = await service.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult<object>.NotFoundResult(NotFoundMessage));
        }

        var json = await service.ReadContentFileAsync(id);
        if (json is null)
        {
            return NotFound(ApiResult<object>.NotFoundResult("內容檔不存在，無法下載。"));
        }

        await WriteSelfAuditAsync(AuditActions.AiCallLog.Export, id.ToString(), $"匯出 AI 對話紀錄（json）：{item.Operation}");
        return File(Encoding.UTF8.GetBytes(json), "application/octet-stream", $"MyProject.Web-ai-call-{item.CallId:N}.json");
    }

    /// <summary>單筆 PDF（中繼資料＋完整對話，不含原始 JSON），寫一筆匯出稽核。</summary>
    [HttpPost("{id:int}/pdf")]
    [AdminOnly]
    public async Task<IActionResult> Pdf(int id)
    {
        var detail = await service.GetDetailAsync(id);
        if (detail is null)
        {
            return NotFound(ApiResult<object>.NotFoundResult(NotFoundMessage));
        }

        if (detail.Content is not { } content)
        {
            return NotFound(ApiResult<object>.NotFoundResult("內容檔不存在，無法匯出 PDF。"));
        }

        var now = DateTime.Now;
        byte[] bytes;
        try
        {
            bytes = AiCallLogPdfBuilder.Build(new AiCallLogPdfRequest
            {
                SystemName = systemIdentity.Name,
                SystemVersion = systemIdentity.Version,
                OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                GeneratedAt = now,
                Item = detail.Item,
                Messages = detail.Messages ?? [],
                ResponseText = content.ResponseText,
                Usage = detail.Usage,
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "AI call log PDF export failed because the embedded font is missing.");
            return this.ApiServerError("PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI call log PDF export failed.");
            return this.ApiServerError($"PDF 匯出失敗：{ex.GetType().Name}。", ex);
        }

        logger.LogInformation("AI call log PDF exported. CallLogId={CallLogId}, Bytes={Bytes}", id, bytes.Length);
        await WriteSelfAuditAsync(AuditActions.AiCallLog.Export, id.ToString(), $"匯出 AI 對話紀錄（pdf）：{detail.Item.Operation}");
        return File(bytes, "application/pdf", $"MyProject.Web-ai-call-{id}-{now:yyyyMMdd-HHmmss}.pdf");
    }

    /// <summary>
    /// 多筆合併成一份 PDF：勾選的紀錄、明細窗的整段對話、目前查詢結果（有內文搜尋時只含符合的），一次最多 <see cref="AiCallLogPdfBuilder.MaxMergedRecords"/> 筆。
    /// 稽核摘要只記來源與筆數，匯出了哪幾筆（紀錄 Id）放進詳細內容檔，絕不記內容。
    /// </summary>
    [HttpPost("merged-pdf")]
    [AdminOnly]
    public async Task<IActionResult> MergedPdf([FromBody] AiCallLogMergedPdfRequestDto request)
    {
        List<int> ids;
        if (request.Source == SourceQuery)
        {
            if (request.Query is null)
            {
                return BadRequest(ApiResult<object>.ValidationError("請指定查詢條件。"));
            }

            var result = await service.GetAsync(ToQuery(request.Query, 1, AiCallLogPdfBuilder.MaxMergedRecords));
            if (result.Count > AiCallLogPdfBuilder.MaxMergedRecords)
            {
                return BadRequest(ApiResult<object>.ValidationError(TooManyMessage(result.Count, "請縮小日期或其他條件，或勾選要匯出的紀錄。")));
            }

            ids = result.Result.Select(x => x.Id).ToList();
        }
        else if (request.Source is SourceSelected or SourceConversation)
        {
            ids = request.Ids ?? [];
            if (ids.Count > AiCallLogPdfBuilder.MaxMergedRecords)
            {
                return BadRequest(ApiResult<object>.ValidationError(TooManyMessage(ids.Count, "請縮小範圍。")));
            }
        }
        else
        {
            return BadRequest(ApiResult<object>.ValidationError("不支援的匯出來源。"));
        }

        List<AiCallLogExportItem> items = ids.Count == 0 ? [] : await service.GetExportItemsAsync(ids);
        if (items.Count == 0)
        {
            return NotFound(ApiResult<object>.NotFoundResult(ExportItemsNotFoundMessage));
        }

        var now = DateTime.Now;
        byte[] bytes;
        try
        {
            bytes = AiCallLogPdfBuilder.BuildMerged(new AiCallLogMergedPdfRequest
            {
                SystemName = systemIdentity.Name,
                SystemVersion = systemIdentity.Version,
                OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                GeneratedAt = now,
                Items = items.Select(item => new AiCallLogPdfRequest
                {
                    Item = item.Item,
                    Messages = item.Messages,
                    ResponseText = item.ResponseText,
                    Usage = item.Usage,
                    ContentMissing = item.ContentMissing,
                }).ToList(),
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI call logs merged PDF export failed. Source={Source}, Rows={Rows}", request.Source, ids.Count);
            return this.ApiServerError($"PDF 匯出失敗：{ex.GetType().Name}。", ex);
        }

        var exported = items.Select(x => x.Item.Id).ToList();
        logger.LogInformation("AI call logs merged PDF exported. Source={Source}, Rows={Rows}, Bytes={Bytes}", request.Source, exported.Count, bytes.Length);
        await WriteSelfAuditAsync(AuditActions.AiCallLog.Export, "*", $"format=pdf-merged; source={request.Source}; rows={exported.Count}",
            content: $"ids={string.Join(",", exported)}");
        return File(bytes, "application/pdf", $"MyProject.Web-ai-calls-{items.Count}-{now:yyyyMMdd-HHmmss}.pdf");
    }

    /// <summary>匯出目前查詢條件下的全部資料（CSV、UTF-8 含 BOM、不含內文，最多 <see cref="AiCallLogService.MaxExportRows"/> 筆，順序與畫面相同），寫一筆匯出稽核。</summary>
    [HttpPost("export")]
    [AdminOnly]
    public async Task<IActionResult> Export([FromBody] AiCallLogSearchRequestDto request)
    {
        var result = await service.GetAsync(ToQuery(request, 1, AiCallLogService.MaxExportRows));
        var bytes = TabularExport.ToCsv(AiCallLogExport.CsvColumns, result.Result);
        logger.LogInformation("AI call log export downloaded. Rows={Rows}", result.Count);

        // 與 Server 版相同：記查詢結果的總筆數。
        await WriteSelfAuditAsync(AuditActions.AiCallLog.Export, "*", $"format=csv; rows={result.Count}");
        return File(bytes, TabularExport.CsvContentType, AiCallLogExport.FileName(DateTime.Now));
    }

    /// <summary>刪除一筆（連同內容檔），寫一筆刪除稽核。畫面的「刪除選取」逐筆呼叫這裡，規則與稽核同單筆。</summary>
    [HttpDelete("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        var item = await service.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult.NotFoundResult("找不到要刪除的對話紀錄。"));
        }

        var result = await service.DeleteAsync(id);
        if (result.Success)
        {
            await WriteSelfAuditAsync(AuditActions.AiCallLog.Delete, id.ToString(), $"刪除 AI 對話紀錄：{item.Operation}");
        }

        return this.FromVerify(result, "已刪除對話紀錄。");
    }

    /// <summary>刪除指定日期（不含當日）之前的紀錄與內容檔，寫一筆清除稽核；回應的訊息就是畫面要顯示的那一句。</summary>
    [HttpPost("purge")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Purge([FromBody] AiCallLogPurgeRequestDto request)
    {
        var beforeDate = request.BeforeDate!.Value;
        var result = await service.PurgeBeforeAsync(beforeDate);
        if (result.Success)
        {
            await WriteSelfAuditAsync(AuditActions.AiCallLog.Purge, "*", $"清除 {beforeDate:yyyy-MM-dd} 之前的 AI 對話紀錄：{result.Message}");
        }

        return this.FromVerify(result, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
    }

    /// <summary>清空全部紀錄與內容檔（Token 用量不受影響），寫一筆清空稽核。</summary>
    [HttpPost("clear-all")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> ClearAll()
    {
        var result = await service.ClearAllAsync();
        if (result.Success)
        {
            await WriteSelfAuditAsync(AuditActions.AiCallLog.ClearAll, "*", $"清空全部 AI 對話紀錄：{result.Message}");
        }

        return this.FromVerify(result, "已清空全部對話紀錄。");
    }

    /// <summary>筆數超過上限時的提示（與 Server 版同一句）。</summary>
    private static string TooManyMessage(int count, string advice)
        => $"共 {count:N0} 筆，合併匯出一次最多 {AiCallLogPdfBuilder.MaxMergedRecords} 筆；{advice}";

    private static AiCallLogQuery ToQuery(AiCallLogSearchRequestDto request, int page, int pageSize)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<AiCallLogAdapterModel>(request.SortBy,
            nameof(AiCallLogAdapterModel.OccurredAt), nameof(AiCallLogAdapterModel.ElapsedMilliseconds), nameof(AiCallLogAdapterModel.RequestCharacters));
        return new AiCallLogQuery
        {
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Operation = request.Operation ?? string.Empty,
            Account = request.Account ?? string.Empty,
            Success = request.Success,
            Model = request.Model ?? string.Empty,
            Keyword = request.Keyword ?? string.Empty,
            Ids = request.ContentIds,
            CurrentPage = page,
            PageSize = pageSize,
            SortField = sortField ?? string.Empty,
            SortDescending = sortField is null ? null : request.SortDescending,
        };
    }

    /// <summary>每段只送前 <see cref="AiCallLogDetailDto.MaxTextCharacters"/> 字元（畫面也只顯示這麼多），並帶完整長度。</summary>
    private static AiCallLogTextDto Clip(string text) => new()
    {
        Text = text.Length > AiCallLogDetailDto.MaxTextCharacters ? text[..AiCallLogDetailDto.MaxTextCharacters] : text,
        Length = text.Length,
    };

    /// <summary>排版 JSON 以便閱讀；不是合法 JSON 時原樣顯示（與 Server 版明細窗相同）。</summary>
    private static string PrettyJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>「用量與費用」欄的文字（與 Server 版明細窗相同）。</summary>
    private static string UsageText(AiCallLogDetail detail)
    {
        if (detail.Usage is not { } usage)
        {
            // 取消與非預期錯誤這兩支，Token 用量本來就不記（兩頁筆數可能不同，是刻意的）。
            return detail.Item.IsCanceled || detail.Item.FailureReason == nameof(AiAnalysisFailureReason.Unexpected)
                ? "沒有對應的用量紀錄（使用者取消與非預期錯誤的呼叫不記入 Token 用量）"
                : "沒有對應的用量紀錄（可能已被清除）";
        }

        var cost = usage.IsUnpriced
            ? "未定價"
            : $"NT$ {TokenUsageFormat.CostTwdCell(usage.CostTwd)}（US$ {TokenUsageFormat.CostUsdCell(usage.CostUsd)}）";
        return $"輸入 {Format(usage.InputCount)}／輸出 {Format(usage.OutputCount)}／合計 {Format(usage.TotalCount)}，{cost}";

        static string Format(int? value) => value?.ToString("N0") ?? "—";
    }

    /// <summary>
    /// 刪除、清除、清空、匯出對話紀錄本身留下一筆稽核（只記動作與筆數，絕不記內容；與 Server 版相同）。
    /// 稽核寫入失敗不推翻已經完成的動作，只記錯誤。
    /// </summary>
    private async Task WriteSelfAuditAsync(string action, string targetId, string detail, string? content = null)
    {
        try
        {
            var user = currentUserService.CurrentUser;
            await auditLogService.WriteAsync(action, success: true, actorUserId: user.Id, actorAccount: user.Account,
                targetType: TargetType, targetId: targetId, detail: detail, content: content);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit log for AI call log maintenance. Action={Action}", action);
        }
    }
}
