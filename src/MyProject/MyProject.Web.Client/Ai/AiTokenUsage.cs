using MyProject.Dtos.Ai;

namespace MyProject.Web.Ai;

/// <summary>
/// API 回傳的用量（0.10.5 起放在 Client，命名空間不變：WASM 頁面與伺服器共用）。每個欄位都是 nullable，代表「這次沒回就不顯示」。
///
/// ⚠️ 欄名刻意用 Count 結尾而非 Tokens：MyProject.Tests 的 LoggingConventionTests
/// 禁止日誌佔位符含 "token"，用 Count 結尾才能安全地寫進 ILogger 訊息。
/// </summary>
public sealed record AiTokenUsage
{
    /// <summary><c>usage.prompt_tokens</c></summary>
    public int? InputCount { get; init; }

    /// <summary><c>usage.completion_tokens</c></summary>
    public int? OutputCount { get; init; }

    /// <summary><c>usage.total_tokens</c></summary>
    public int? TotalCount { get; init; }

    /// <summary><c>usage.prompt_tokens_details.cached_tokens</c></summary>
    public int? CachedInputCount { get; init; }

    /// <summary><c>usage.completion_tokens_details.reasoning_tokens</c></summary>
    public int? ReasoningCount { get; init; }

    public bool HasAny
        => InputCount.HasValue || OutputCount.HasValue || TotalCount.HasValue
        || CachedInputCount.HasValue || ReasoningCount.HasValue;

    /// <summary>
    /// 給人看的用量文字（例如「輸入 1,200　輸出 300　合計 1,500」）：只列出 API 真的有回的欄位，全缺時回空字串。
    /// 畫面、稽核摘要與 PDF 共用（0.10.5 起從 <c>AiReportPdfBuilder.BuildUsageText</c> 移來，WASM 頁面也要用）。
    /// </summary>
    public static string ToDisplayText(AiTokenUsage? usage)
    {
        if (usage is null || usage.HasAny == false)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        Append(parts, "輸入", usage.InputCount);
        Append(parts, "輸出", usage.OutputCount);
        Append(parts, "合計", usage.TotalCount);
        Append(parts, "快取輸入", usage.CachedInputCount);
        Append(parts, "推論", usage.ReasoningCount);
        return string.Join("　", parts);

        static void Append(List<string> parts, string label, int? value)
        {
            if (value.HasValue)
            {
                parts.Add($"{label} {value.Value:N0}");
            }
        }
    }

    /// <summary>
    /// 多次呼叫的累計（0.9.68 起，AI 例外分析的多輪對話用）。
    /// 每個欄位只加「有回報的」；全部都沒回報的欄位維持 null，全部為 null 時回 null。
    /// </summary>
    public static AiTokenUsage? Sum(IEnumerable<AiTokenUsage?> usages)
    {
        var items = usages.OfType<AiTokenUsage>().ToList();
        var total = new AiTokenUsage
        {
            InputCount = SumField(items, usage => usage.InputCount),
            OutputCount = SumField(items, usage => usage.OutputCount),
            TotalCount = SumField(items, usage => usage.TotalCount),
            CachedInputCount = SumField(items, usage => usage.CachedInputCount),
            ReasoningCount = SumField(items, usage => usage.ReasoningCount),
        };

        return total.HasAny ? total : null;

        static int? SumField(List<AiTokenUsage> items, Func<AiTokenUsage, int?> selector)
            => items.Any(item => selector(item).HasValue) ? items.Sum(item => selector(item) ?? 0) : null;
    }

    /// <summary>串流結果裡的用量（<see cref="AiUsageDto"/>）轉回來；WASM 頁面累計多輪用量時用。</summary>
    public static AiTokenUsage? FromDto(AiUsageDto? usage) => usage is null
        ? null
        : new AiTokenUsage
        {
            InputCount = usage.InputCount,
            OutputCount = usage.OutputCount,
            TotalCount = usage.TotalCount,
            CachedInputCount = usage.CachedInputCount,
            ReasoningCount = usage.ReasoningCount,
        };

    public static AiUsageDto? ToDto(AiTokenUsage? usage) => usage is null
        ? null
        : new AiUsageDto
        {
            InputCount = usage.InputCount,
            OutputCount = usage.OutputCount,
            TotalCount = usage.TotalCount,
            CachedInputCount = usage.CachedInputCount,
            ReasoningCount = usage.ReasoningCount,
        };
}
