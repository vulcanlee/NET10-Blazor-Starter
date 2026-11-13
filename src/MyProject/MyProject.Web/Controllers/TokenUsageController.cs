using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
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
/// Token 用量頁的 Web API（0.10.5 起，WASM 遷移 M6），只限管理員。
/// 查詢走畫面同一個 <see cref="TokenUsageLogService"/>；「今天」（近 N 天、每日趨勢的預設區間）、檔名與 PDF 產生時間都用伺服器的時間。
/// 稽核與 Server 版頁面相同：按「查詢」一筆搜尋、開明細一筆查看、刪除每筆一筆、清除／清空／匯出各一筆。
/// 排序修正：Server 版比對 "ascend"/"descend" 永遠不生效，這裡依 <c>SortBy</c>／<c>SortDescending</c> 排序，CSV、PDF 也照同一個順序。
/// </summary>
[Route("api/v1/token-usage")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class TokenUsageController : ControllerBase
{
    private const string TargetType = "TokenUsage";

    /// <summary>
    /// 三張摘要卡的天數。與 <see cref="TokenUsageRanges"/> 是同一組常數，
    /// 不可以在這裡另外寫死數字 —— 標題說「近 7 天」就必須真的查 7 天。
    /// </summary>
    private static readonly int[] RangeCardDays = [TokenUsageRanges.RecentDay, TokenUsageRanges.RecentWeek, TokenUsageRanges.RecentMonth];

    private readonly TokenUsageLogService service;
    private readonly AiCallLogService aiCallLogService;
    private readonly IAiQuotaService quotaService;
    private readonly IOptionsMonitor<AiQuotaSettings> quotaOptions;
    private readonly ISystemIdentity systemIdentity;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<TokenUsageController> logger;

    public TokenUsageController(
        TokenUsageLogService service,
        AiCallLogService aiCallLogService,
        IAiQuotaService quotaService,
        IOptionsMonitor<AiQuotaSettings> quotaOptions,
        ISystemIdentity systemIdentity,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<TokenUsageController> logger)
    {
        this.service = service;
        this.aiCallLogService = aiCallLogService;
        this.quotaService = quotaService;
        this.quotaOptions = quotaOptions;
        this.systemIdentity = systemIdentity;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>
    /// 頁面一次載入的全部資料：明細一頁、篩選範圍合計、四種分組、每日趨勢、近 1／7／30 天摘要卡與 AI 用量上限。
    /// <see cref="TokenUsageSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核（自由輸入的帳號只記有沒有填）。
    /// </summary>
    [HttpPost("dashboard")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<TokenUsageDashboardDto>>> Dashboard([FromBody] TokenUsageSearchRequestDto request)
    {
        var today = DateTime.Today;
        var query = ToQuery(request, request.PageIndex, request.PageSize);
        var page = await service.GetAsync(query);
        var report = await LoadReportAsync(request, query, today);
        var quota = await quotaService.GetStatusAsync(null);

        // 近 1／7／30 天彼此是巢狀區間，只查一次「近 30 天」，三張卡再從同一份日列切片加總。
        var (monthStart, monthEnd) = TokenUsageRanges.Recent(today, TokenUsageRanges.RecentMonth);
        var recentDays = await service.GetDailyAsync(ForRange(request, monthStart, monthEnd));

        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.TokenUsage.Search, TargetType,
                AuditSearchCondition.Of("start", request.StartDate), AuditSearchCondition.Of("end", request.EndDate),
                AuditSearchCondition.Text("account", request.Account ?? string.Empty), AuditSearchCondition.Of("operation", request.Operation ?? string.Empty),
                AuditSearchCondition.Of("callKind", request.CallKind ?? string.Empty), AuditSearchCondition.Of("model", request.Model ?? string.Empty));
        }

        var settings = quotaOptions.CurrentValue;
        return Ok(ApiResult<TokenUsageDashboardDto>.SuccessResult(new TokenUsageDashboardDto
        {
            Page = new PagedResult<TokenUsageItemDto>
            {
                Items = page.Result.Select(TokenUsageDtoMapper.ToDto).ToList(),
                TotalCount = page.Count,
                PageIndex = request.PageIndex,
                PageSize = request.PageSize,
                TotalPages = (int)Math.Ceiling(page.Count / (double)request.PageSize),
            },
            Summary = TokenUsageDtoMapper.ToDto(report.Summary),
            ByAccount = report.ByAccount.Select(TokenUsageDtoMapper.ToDto).ToList(),
            ByOperation = report.ByOperation.Select(TokenUsageDtoMapper.ToDto).ToList(),
            ByModel = report.ByModel.Select(TokenUsageDtoMapper.ToDto).ToList(),
            ByCallKind = report.ByCallKind.Select(TokenUsageDtoMapper.ToDto).ToList(),
            Daily = report.Daily.Select(TokenUsageDtoMapper.ToDto).ToList(),
            IsTrendTruncated = report.IsTrendTruncated,
            RangeCards = RangeCardDays.Select(days => CardOf(recentDays, today, days)).ToList(),
            Quota = quota.Select(status => new TokenUsageQuotaItemDto
            {
                Text = QuotaText(status),
                Pill = status.IsReached ? "已達上限" : status.Ratio >= 0.8 ? "已達 80%" : null,
            }).ToList(),
            PerUserDailyLimit = LimitText(settings.PerUserDailyTwd),
            PerUserMonthlyLimit = LimitText(settings.PerUserMonthlyTwd),
        }, "查詢用量紀錄成功"));
    }

    /// <summary>三個下拉選單的可選值（資料庫中實際出現過的作業、型別、模型）。</summary>
    [HttpGet("options")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<TokenUsageOptionsDto>>> Options()
    {
        var options = await service.GetFilterOptionsAsync();
        return Ok(ApiResult<TokenUsageOptionsDto>.SuccessResult(new TokenUsageOptionsDto
        {
            Operations = [.. options.Operations],
            CallKinds = [.. options.CallKinds],
            Models = [.. options.Models],
        }, "取得篩選選項成功"));
    }

    /// <summary>
    /// 明細窗：這一列、原始 usage（平攤成「路徑 → 值」）、費率快照與 AI 對話紀錄是否還在；寫一筆查看稽核。
    /// 原始明細檔只在開窗時才讀，清單查詢不會一次把所有檔案讀進記憶體。
    /// </summary>
    [HttpGet("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<TokenUsageDetailDto>>> Detail(int id)
    {
        var item = await service.GetByIdAsync(id);
        if (item is null)
        {
            return NotFound(ApiResult<TokenUsageDetailDto>.NotFoundResult("找不到這筆用量紀錄。"));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.TokenUsage.View, TargetType, item.Id.ToString(), null);

        var detail = new TokenUsageDetailDto
        {
            Item = TokenUsageDtoMapper.ToDto(item),
            // 費率快照就在資料列上，不必再讀檔；沿用同一支平攤器讓呈現與原始明細一致。
            CostRateRows = string.IsNullOrWhiteSpace(item.CostRateSnapshot) ? [] : FlattenJson(item.CostRateSnapshot),
        };

        try
        {
            detail.HasConversation = item.CallId is { } callId && await aiCallLogService.ExistsAsync(callId);
        }
        catch (Exception ex)
        {
            // 查不到對話只是少一顆按鈕，不可以讓整個用量明細打不開。
            logger.LogError(ex, "Failed to check AI call log for LLM usage record. UsageId={UsageId}", item.Id);
            detail.HasConversation = false;
        }

        var raw = await service.GetRawUsageAsync(item.Id);
        if (string.IsNullOrWhiteSpace(raw))
        {
            detail.RawMessage = "原始明細不存在（可能已被清除，或當初這次呼叫沒有回傳用量）。";
        }
        else
        {
            detail.RawRows = FlattenJson(raw);
            detail.RawMessage = detail.RawRows.Count == 0 ? "原始明細無法解析。" : string.Empty;
        }

        return Ok(ApiResult<TokenUsageDetailDto>.SuccessResult(detail, "取得用量明細成功"));
    }

    /// <summary>匯出目前篩選條件下的全部資料（CSV、UTF-8 含 BOM，順序同畫面），寫一筆匯出稽核。</summary>
    [HttpPost("export")]
    [AdminOnly]
    public async Task<IActionResult> Export([FromBody] TokenUsageSearchRequestDto request)
    {
        var all = await service.GetAsync(ToQuery(request, 1, int.MaxValue));
        var bytes = TabularExport.ToCsv(TokenUsageExport.CsvColumns, all.Result);
        logger.LogInformation("Usage export downloaded. Rows={Rows}", all.Count);
        await WriteAuditAsync(AuditActions.TokenUsage.Export, "*", $"format=csv; rows={all.Count}");
        return File(bytes, TabularExport.CsvContentType, TokenUsageExport.CsvFileName(DateTime.Now));
    }

    /// <summary>
    /// 匯出 PDF：<see cref="TokenUsagePdfExportRequestDto.Tab"/> 空白是整份報表，否則只輸出那一個頁籤（標題、條件區與合計仍然保留）。
    /// 明細只有整份或「明細」頁籤才查（全部筆數、順序同畫面）；寫一筆匯出稽核。
    /// </summary>
    [HttpPost("export-pdf")]
    [AdminOnly]
    public async Task<IActionResult> ExportPdf([FromBody] TokenUsagePdfExportRequestDto request)
    {
        if (TokenUsageReportScopes.FromTab(request.Tab) is not { } scope)
        {
            return BadRequest(ApiResult.ValidationError("不認得的頁籤。"));
        }

        var now = DateTime.Now;
        var query = ToQuery(request, 1, int.MaxValue);
        var report = await LoadReportAsync(request, query, now.Date);

        // 明細是唯一要另外查的區塊（畫面上是分頁的）。匯出「依模型」之類的頁籤時不該為了一份用不到的明細去掃全表。
        var details = scope is TokenUsageReportScope.All or TokenUsageReportScope.Detail
            ? (await service.GetAsync(query)).Result.ToList()
            : [];

        byte[] bytes;
        try
        {
            bytes = TokenUsageReportPdfBuilder.Build(new TokenUsageReportRequest
            {
                SystemName = systemIdentity.Name,
                SystemVersion = systemIdentity.Version,
                OperatorAccount = currentUserService.CurrentUser.Account ?? string.Empty,
                GeneratedAt = now,
                StartDate = request.StartDate,
                EndDate = request.EndDate,
                Account = request.Account ?? string.Empty,
                Operation = request.Operation ?? string.Empty,
                CallKind = request.CallKind ?? string.Empty,
                Model = request.Model ?? string.Empty,
                Summary = report.Summary,
                ByAccount = report.ByAccount,
                ByOperation = report.ByOperation,
                ByModel = report.ByModel,
                ByCallKind = report.ByCallKind,
                Details = details,
                // 與畫面相同的趨勢（已補零、已套 180 天上限）。
                Daily = report.Daily,
                Scope = scope,
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Usage PDF export failed because the embedded font is missing.");
            return this.ApiServerError("缺少內建中文字型，請確認建置產物完整。", ex);
        }

        logger.LogInformation("Usage PDF exported. Scope={Scope}, Bytes={Bytes}, Rows={Rows}", scope, bytes.Length, details.Count);
        await WriteAuditAsync(AuditActions.TokenUsage.Export, "*", $"format=pdf; scope={scope}; rows={details.Count}");
        return File(bytes, "application/pdf", TokenUsageExport.PdfFileName(scope is TokenUsageReportScope.All ? null : request.Tab, now));
    }

    /// <summary>刪除一筆（連同原始明細檔）；成功寫一筆刪除稽核。「刪除選取」由頁面逐筆呼叫這裡，每筆一列稽核。</summary>
    [HttpDelete("{id:int}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        var result = await service.DeleteAsync(id);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.TokenUsage.Delete, id.ToString(), "刪除一筆用量紀錄");
        }

        return this.FromVerify(result, "刪除成功");
    }

    /// <summary>批次清除：刪除指定日期（不含當日）之前的紀錄與原始明細檔；成功寫一筆清除稽核，訊息含筆數。</summary>
    [HttpPost("purge")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Purge([FromBody] TokenUsagePurgeRequestDto request)
    {
        var before = request.BeforeDate!.Value;
        var result = await service.PurgeBeforeAsync(before);
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.TokenUsage.Purge, "*", $"清除 {before:yyyy-MM-dd} 之前的用量紀錄：{result.Message}");
        }

        return this.FromVerify(result, result.Message);
    }

    /// <summary>清空全部紀錄與整個原始明細目錄；成功寫一筆清空稽核。</summary>
    [HttpPost("clear-all")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> ClearAll()
    {
        var result = await service.ClearAllAsync();
        if (result.Success)
        {
            await WriteAuditAsync(AuditActions.TokenUsage.ClearAll, "*", $"清空全部用量紀錄：{result.Message}");
        }

        return this.FromVerify(result, result.Message);
    }

    /// <summary>合計、四種分組與每日趨勢（畫面與 PDF 共用，PDF 的內容與畫面一致）。</summary>
    private async Task<TokenUsageReport> LoadReportAsync(TokenUsageSearchRequestDto request, TokenUsageQuery query, DateTime today)
    {
        var (daily, isTruncated) = await LoadTrendAsync(request, query, today);
        return new TokenUsageReport(
            await service.GetSummaryAsync(query),
            await service.GetGroupedAsync(query, TokenUsageGroupBy.Account),
            await service.GetGroupedAsync(query, TokenUsageGroupBy.Operation),
            await service.GetGroupedAsync(query, TokenUsageGroupBy.Model),
            await service.GetGroupedAsync(query, TokenUsageGroupBy.CallKind),
            daily,
            isTruncated);
    }

    /// <summary>
    /// 「每日趨勢」。區間跟隨篩選；起訖日都沒填時退回近 30 天 —— 不設限的話，資料放久了會一次畫出上百格。
    /// 超過 <see cref="TokenUsageTrendChart.MaxTrendDays"/> 天只保留最後那幾天。
    /// </summary>
    private async Task<(List<TokenUsageDailyRow> Rows, bool IsTruncated)> LoadTrendAsync(TokenUsageSearchRequestDto request, TokenUsageQuery query, DateTime today)
    {
        var isUnbounded = request.StartDate is null && request.EndDate is null;
        var (windowStart, windowEnd) = TokenUsageRanges.Recent(today, TokenUsageRanges.RecentMonth);

        var actual = await service.GetDailyAsync(isUnbounded ? ForRange(request, windowStart, windowEnd) : query);

        DateTime start;
        DateTime end;

        if (isUnbounded)
        {
            (start, end) = (windowStart, windowEnd);
        }
        else
        {
            // 只填了一端時，另一端交給資料自己決定：沒有資料的日子沒有格子可畫。
            end = request.EndDate?.Date ?? today;
            start = request.StartDate?.Date ?? (actual.Count > 0 ? actual[0].Date : end);
        }

        // 使用者可以把結束日選在起始日之前，這裡夾一下，否則下面的迴圈永遠不會執行。
        if (end < start)
        {
            end = start;
        }

        var isTruncated = (end - start).Days + 1 > TokenUsageTrendChart.MaxTrendDays;
        if (isTruncated)
        {
            start = end.AddDays(-(TokenUsageTrendChart.MaxTrendDays - 1));
        }

        return (FillMissingDays(actual, start, end), isTruncated);
    }

    /// <summary>
    /// 補上沒有資料的日期。缺口不補的話，長條圖會把「那天沒花錢」畫成「那天不存在」，
    /// 相鄰的兩根柱子看起來就成了連續的兩天。
    /// </summary>
    private static List<TokenUsageDailyRow> FillMissingDays(List<TokenUsageDailyRow> actual, DateTime start, DateTime end)
    {
        var byDate = actual.ToDictionary(x => x.Date);
        var result = new List<TokenUsageDailyRow>();

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            result.Add(byDate.TryGetValue(day, out var row) ? row : new TokenUsageDailyRow { Date = day });
        }

        return result;
    }

    /// <summary>近 N 天的摘要卡：從近 30 天的日列切片加總。</summary>
    private static TokenUsageRangeCardDto CardOf(List<TokenUsageDailyRow> recentDays, DateTime today, int days)
    {
        var (start, end) = TokenUsageRanges.Recent(today, days);
        var slice = recentDays.Where(x => x.Date >= start).ToList();

        return TokenUsageDtoMapper.ToCardDto(days, new TokenUsageDailyRow
        {
            Date = start,
            TotalCount = slice.Sum(x => x.TotalCount),
            CostUsd = slice.Sum(x => x.CostUsd),
            CostTwd = slice.Sum(x => x.CostTwd),
            UnpricedCount = slice.Sum(x => x.UnpricedCount),
            CallCount = slice.Sum(x => x.CallCount),
        }, end);
    }

    private static TokenUsageQuery ToQuery(TokenUsageSearchRequestDto request, int page, int pageSize)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<TokenUsageLogAdapterModel>(request.SortBy,
            nameof(TokenUsageLogAdapterModel.OccurredAt), nameof(TokenUsageLogAdapterModel.CostTwd), nameof(TokenUsageLogAdapterModel.TotalCount));
        return new TokenUsageQuery
        {
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Account = request.Account ?? string.Empty,
            Operation = request.Operation ?? string.Empty,
            CallKind = request.CallKind ?? string.Empty,
            Model = request.Model ?? string.Empty,
            CurrentPage = page,
            PageSize = pageSize,
            SortField = sortField ?? string.Empty,
            SortDescending = sortField is null ? null : request.SortDescending,
        };
    }

    /// <summary>
    /// 沿用目前的帳號／作業／型別／模型篩選，只把日期換成指定的窗。
    /// 「條件照舊、只換日期」是摘要卡與主查詢唯一的差別，所以這裡刻意不碰其他欄位。
    /// </summary>
    private static TokenUsageQuery ForRange(TokenUsageSearchRequestDto request, DateTime start, DateTime end) => new()
    {
        StartDate = start,
        EndDate = end,
        Account = request.Account ?? string.Empty,
        Operation = request.Operation ?? string.Empty,
        CallKind = request.CallKind ?? string.Empty,
        Model = request.Model ?? string.Empty,
    };

    /// <summary>「全系統每日：今日 NT$ 12.5／NT$ 100」；沒有上限時寫「不限制」。</summary>
    private static string QuotaText(AiQuotaLimitStatus status)
        => $"{status.Label}：{status.PeriodLabel} NT$ {AiQuotaService.Money(status.UsedTwd)}／{LimitText(status.LimitTwd)}";

    private static string LimitText(int limitTwd) => limitTwd > 0 ? $"NT$ {AiQuotaService.Money(limitTwd)}" : "不限制";

    /// <summary>用量紀錄的維護與匯出要留稽核（LOG-14），只記筆數與格式。</summary>
    private Task WriteAuditAsync(string action, string targetId, string detail)
        => ViewAudit.WriteAsync(auditLogService, currentUserService, action, TargetType, targetId, detail);

    /// <summary>
    /// 把 usage JSON 平攤成「路徑 → 值」逐列顯示，例如 <c>prompt_tokens_details.cached_tokens</c>。
    /// 供應商日後新增欄位時不用改程式就看得到。
    /// </summary>
    private List<TokenUsageFieldDto> FlattenJson(string json)
    {
        var result = new List<TokenUsageFieldDto>();

        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement, string.Empty, result);
        }
        catch (JsonException ex)
        {
            // 檔案內容不是合法 JSON（理論上不會發生），交給畫面顯示友善訊息。
            logger.LogWarning(ex, "LLM usage JSON could not be parsed for display.");
            result.Clear();
        }

        return result;

        static void Walk(JsonElement element, string prefix, List<TokenUsageFieldDto> output)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var name = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    Walk(property.Value, name, output);
                }

                return;
            }

            var text = element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out var number)
                    ? TokenUsageFormat.Compact(number)
                    : element.ToString(),
                JsonValueKind.Null => "—",
                _ => element.ToString(),
            };

            output.Add(new TokenUsageFieldDto { Name = prefix, Value = text });
        }
    }

    /// <summary>合計、四種分組與每日趨勢。</summary>
    private sealed record TokenUsageReport(
        TokenUsageSummary Summary,
        List<TokenUsageGroupRow> ByAccount,
        List<TokenUsageGroupRow> ByOperation,
        List<TokenUsageGroupRow> ByModel,
        List<TokenUsageGroupRow> ByCallKind,
        List<TokenUsageDailyRow> Daily,
        bool IsTrendTruncated);
}
