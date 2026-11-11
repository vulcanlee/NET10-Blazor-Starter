using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>
/// Token 用量的 API 資料與畫面資料互轉（0.10.5 起，WASM 遷移 M6）。WASM 頁面與伺服器的 <c>TokenUsageController</c> 共用這一份。
/// 本地時間一律以「不帶時差」送出（Kind=Unspecified）：帶了 +08:00，WASM 會換成瀏覽器的時區。
/// </summary>
public static class TokenUsageDtoMapper
{
    public static TokenUsageItemDto ToDto(TokenUsageLogAdapterModel model) => new()
    {
        Id = model.Id,
        OccurredAt = Unzoned(model.OccurredAt),
        Operation = model.Operation,
        CallKind = model.CallKind,
        Provider = model.Provider,
        Model = model.Model,
        Account = model.Account,
        UserId = model.UserId,
        InputCount = model.InputCount,
        OutputCount = model.OutputCount,
        TotalCount = model.TotalCount,
        CachedInputCount = model.CachedInputCount,
        ReasoningCount = model.ReasoningCount,
        ImageInputCount = model.ImageInputCount,
        ImageCachedInputCount = model.ImageCachedInputCount,
        ImageOutputCount = model.ImageOutputCount,
        DurationSeconds = model.DurationSeconds,
        CharacterCount = model.CharacterCount,
        CostUsd = model.CostUsd,
        CostTwd = model.CostTwd,
        CostExchangeRate = model.CostExchangeRate,
        CostPriceKey = model.CostPriceKey,
        CostLongContext = model.CostLongContext,
        ElapsedMilliseconds = model.ElapsedMilliseconds,
        Success = model.Success,
        FailureReason = model.FailureReason,
        RunId = model.RunId,
        CallId = model.CallId,
    };

    public static TokenUsageLogAdapterModel ToModel(TokenUsageItemDto dto) => new()
    {
        Id = dto.Id,
        OccurredAt = dto.OccurredAt,
        Operation = dto.Operation,
        CallKind = dto.CallKind,
        Provider = dto.Provider,
        Model = dto.Model,
        Account = dto.Account,
        UserId = dto.UserId,
        InputCount = dto.InputCount,
        OutputCount = dto.OutputCount,
        TotalCount = dto.TotalCount,
        CachedInputCount = dto.CachedInputCount,
        ReasoningCount = dto.ReasoningCount,
        ImageInputCount = dto.ImageInputCount,
        ImageCachedInputCount = dto.ImageCachedInputCount,
        ImageOutputCount = dto.ImageOutputCount,
        DurationSeconds = dto.DurationSeconds,
        CharacterCount = dto.CharacterCount,
        CostUsd = dto.CostUsd,
        CostTwd = dto.CostTwd,
        CostExchangeRate = dto.CostExchangeRate,
        CostPriceKey = dto.CostPriceKey,
        CostLongContext = dto.CostLongContext,
        ElapsedMilliseconds = dto.ElapsedMilliseconds,
        Success = dto.Success,
        FailureReason = dto.FailureReason,
        RunId = dto.RunId,
        CallId = dto.CallId,
    };

    public static TokenUsageTotalsDto ToDto(TokenUsageSummary summary) => Fill(new TokenUsageTotalsDto(), summary.InputCount, summary.OutputCount,
        summary.CachedInputCount, summary.ReasoningCount, summary.TotalCount, summary.CostUsd, summary.CostTwd, summary.UnpricedCount, summary.CallCount);

    public static TokenUsageSummary ToModel(TokenUsageTotalsDto dto) => new()
    {
        InputCount = dto.InputCount,
        OutputCount = dto.OutputCount,
        CachedInputCount = dto.CachedInputCount,
        ReasoningCount = dto.ReasoningCount,
        TotalCount = dto.TotalCount,
        CostUsd = dto.CostUsd,
        CostTwd = dto.CostTwd,
        UnpricedCount = dto.UnpricedCount,
        CallCount = dto.CallCount,
    };

    public static TokenUsageGroupRowDto ToDto(TokenUsageGroupRow row)
    {
        var dto = Fill(new TokenUsageGroupRowDto(), row.InputCount, row.OutputCount, row.CachedInputCount, row.ReasoningCount, row.TotalCount,
            row.CostUsd, row.CostTwd, row.UnpricedCount, row.CallCount);
        dto.Key = row.Key;
        return dto;
    }

    public static TokenUsageGroupRow ToModel(TokenUsageGroupRowDto dto) => new()
    {
        Key = dto.Key,
        InputCount = dto.InputCount,
        OutputCount = dto.OutputCount,
        CachedInputCount = dto.CachedInputCount,
        ReasoningCount = dto.ReasoningCount,
        TotalCount = dto.TotalCount,
        CostUsd = dto.CostUsd,
        CostTwd = dto.CostTwd,
        UnpricedCount = dto.UnpricedCount,
        CallCount = dto.CallCount,
    };

    public static TokenUsageDailyRowDto ToDto(TokenUsageDailyRow row) => Fill(new TokenUsageDailyRowDto(), row);

    public static TokenUsageDailyRow ToModel(TokenUsageDailyRowDto dto) => new()
    {
        Date = dto.Date,
        TotalCount = dto.TotalCount,
        CostUsd = dto.CostUsd,
        CostTwd = dto.CostTwd,
        UnpricedCount = dto.UnpricedCount,
        CallCount = dto.CallCount,
    };

    /// <summary>「近 N 天」摘要卡：<paramref name="totals"/> 的 Date 是區間起日。</summary>
    public static TokenUsageRangeCardDto ToCardDto(int days, TokenUsageDailyRow totals, DateTime end)
    {
        var dto = Fill(new TokenUsageRangeCardDto(), totals);
        dto.Days = days;
        dto.End = Unzoned(end);
        return dto;
    }

    private static T Fill<T>(T dto, TokenUsageDailyRow row)
        where T : TokenUsageDailyRowDto
    {
        dto.Date = Unzoned(row.Date);
        dto.TotalCount = row.TotalCount;
        dto.CostUsd = row.CostUsd;
        dto.CostTwd = row.CostTwd;
        dto.UnpricedCount = row.UnpricedCount;
        dto.CallCount = row.CallCount;
        return dto;
    }

    private static T Fill<T>(T dto, long input, long output, long cached, long reasoning, long total, double costUsd, double costTwd, int unpriced, int calls)
        where T : TokenUsageTotalsDto
    {
        dto.InputCount = input;
        dto.OutputCount = output;
        dto.CachedInputCount = cached;
        dto.ReasoningCount = reasoning;
        dto.TotalCount = total;
        dto.CostUsd = costUsd;
        dto.CostTwd = costTwd;
        dto.UnpricedCount = unpriced;
        dto.CallCount = calls;
        return dto;
    }

    private static DateTime Unzoned(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
