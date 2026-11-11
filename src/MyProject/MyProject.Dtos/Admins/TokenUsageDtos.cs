using System.ComponentModel.DataAnnotations;
using MyProject.Dtos.Commons;

namespace MyProject.Dtos.Admins;

/// <summary>
/// Token 用量頁的查詢條件（0.10.5 起，WASM 遷移 M6；<c>POST api/v1/token-usage/dashboard</c> 與 <c>export</c>）。
/// 日期是伺服器的本地日期（不帶時區），結束日含當天整天。排序支援 <c>OccurredAt</c>、<c>CostTwd</c>、<c>TotalCount</c>，
/// 未指定時依時間由新到舊。<see cref="SearchRequestBaseDto.Keyword"/> 不使用。
/// </summary>
public class TokenUsageSearchRequestDto : SearchRequestBaseDto
{
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>帳號（部分符合）。</summary>
    public string? Account { get; set; }

    /// <summary>作業（完全相同）。</summary>
    public string? Operation { get; set; }

    /// <summary>API 型別（完全相同）。</summary>
    public string? CallKind { get; set; }

    /// <summary>模型（完全相同）。</summary>
    public string? Model { get; set; }

    /// <summary>true：使用者按下「查詢」，伺服器寫一筆搜尋稽核；進頁、換頁、排序、重新整理不帶。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>PDF 匯出（<c>POST api/v1/token-usage/export-pdf</c>）：查詢條件加上要匯出的頁籤（空白＝全部頁籤）。</summary>
public class TokenUsagePdfExportRequestDto : TokenUsageSearchRequestDto
{
    /// <summary>頁籤鍵（trend、daily、account、operation、model、callkind、detail）；空白代表整份報表。</summary>
    public string? Tab { get; set; }
}

/// <summary>批次清除（<c>POST api/v1/token-usage/purge</c>）：刪除這一天（不含）之前的紀錄。</summary>
public class TokenUsagePurgeRequestDto
{
    [Required(ErrorMessage = "請選擇清除日期。")]
    public DateTime? BeforeDate { get; set; }
}

/// <summary>明細的一列。時間是伺服器的本地時間（不帶時差送出）；不含伺服器上原始明細檔的路徑。</summary>
public class TokenUsageItemDto
{
    public int Id { get; set; }

    public DateTime OccurredAt { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string CallKind { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Account { get; set; }

    public int? UserId { get; set; }

    public int? InputCount { get; set; }

    public int? OutputCount { get; set; }

    public int? TotalCount { get; set; }

    public int? CachedInputCount { get; set; }

    public int? ReasoningCount { get; set; }

    public int? ImageInputCount { get; set; }

    public int? ImageCachedInputCount { get; set; }

    public int? ImageOutputCount { get; set; }

    public int? DurationSeconds { get; set; }

    public int? CharacterCount { get; set; }

    /// <summary>估算費用（美金）。null 代表未定價。</summary>
    public double? CostUsd { get; set; }

    public double? CostTwd { get; set; }

    public double? CostExchangeRate { get; set; }

    public string? CostPriceKey { get; set; }

    public bool CostLongContext { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>對應 AI 對話紀錄的呼叫識別碼；0.9.72 之前的紀錄為 null。</summary>
    public Guid? CallId { get; set; }
}

/// <summary>篩選範圍的合計（快取是輸入的子集、推理計入輸出，都不另計入合計）。</summary>
public class TokenUsageTotalsDto
{
    public long InputCount { get; set; }

    public long OutputCount { get; set; }

    public long CachedInputCount { get; set; }

    public long ReasoningCount { get; set; }

    public long TotalCount { get; set; }

    public double CostUsd { get; set; }

    public double CostTwd { get; set; }

    public int UnpricedCount { get; set; }

    public int CallCount { get; set; }
}

/// <summary>分組統計的一列（依使用者／作業／模型／型別共用）。</summary>
public class TokenUsageGroupRowDto : TokenUsageTotalsDto
{
    public string Key { get; set; } = string.Empty;
}

/// <summary>逐日統計的一列（伺服器的本地日期，不帶時差送出）。</summary>
public class TokenUsageDailyRowDto
{
    public DateTime Date { get; set; }

    public long TotalCount { get; set; }

    public double CostUsd { get; set; }

    public double CostTwd { get; set; }

    public int UnpricedCount { get; set; }

    public int CallCount { get; set; }
}

/// <summary>「近 N 天」摘要卡：<see cref="TokenUsageDailyRowDto.Date"/> 是區間的起日，<see cref="End"/> 是迄日（伺服器的今天）。</summary>
public class TokenUsageRangeCardDto : TokenUsageDailyRowDto
{
    public int Days { get; set; }

    public DateTime End { get; set; }
}

/// <summary>AI 用量上限的一項（例：「全系統每日：今日 NT$ 12.5／NT$ 100」），文字由伺服器組好。</summary>
public class TokenUsageQuotaItemDto
{
    public string Text { get; set; } = string.Empty;

    /// <summary>「已達上限」「已達 80%」或 null（不顯示標籤）。</summary>
    public string? Pill { get; set; }
}

/// <summary>
/// 頁面一次載入的全部資料（<c>POST api/v1/token-usage/dashboard</c>）：明細一頁、合計、四種分組、每日趨勢（已補零、最多 180 天）、
/// 近 1／7／30 天摘要卡與 AI 用量上限。「今天」一律是伺服器的今天。
/// </summary>
public class TokenUsageDashboardDto
{
    public PagedResult<TokenUsageItemDto> Page { get; set; } = new();

    public TokenUsageTotalsDto Summary { get; set; } = new();

    public List<TokenUsageGroupRowDto> ByAccount { get; set; } = [];

    public List<TokenUsageGroupRowDto> ByOperation { get; set; } = [];

    public List<TokenUsageGroupRowDto> ByModel { get; set; } = [];

    public List<TokenUsageGroupRowDto> ByCallKind { get; set; } = [];

    /// <summary>每日趨勢：依有效區間補零、由舊到新，趨勢圖由它分桶。</summary>
    public List<TokenUsageDailyRowDto> Daily { get; set; } = [];

    /// <summary>篩選區間超過 180 天、只保留最後 180 天。</summary>
    public bool IsTrendTruncated { get; set; }

    /// <summary>近 1、7、30 天三張卡（沿用帳號／作業／型別／模型篩選，只換日期）。</summary>
    public List<TokenUsageRangeCardDto> RangeCards { get; set; } = [];

    public List<TokenUsageQuotaItemDto> Quota { get; set; } = [];

    /// <summary>每人每日上限的文字（「NT$ 100」或「不限制」）。</summary>
    public string PerUserDailyLimit { get; set; } = string.Empty;

    /// <summary>每人每月上限的文字。</summary>
    public string PerUserMonthlyLimit { get; set; } = string.Empty;
}

/// <summary>三個下拉選單的可選值（<c>GET api/v1/token-usage/options</c>），取自資料庫中實際出現過的值。</summary>
public class TokenUsageOptionsDto
{
    public List<string> Operations { get; set; } = [];

    public List<string> CallKinds { get; set; } = [];

    public List<string> Models { get; set; } = [];
}

/// <summary>明細窗的一列「名稱 → 值」（原始 usage 與費率快照平攤後）。</summary>
public class TokenUsageFieldDto
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>明細窗（<c>GET api/v1/token-usage/{id}</c>）：這一列、原始明細、費率快照與是否還有 AI 對話紀錄。</summary>
public class TokenUsageDetailDto
{
    public TokenUsageItemDto Item { get; set; } = new();

    /// <summary>供應商回傳的原始 usage，平攤成「路徑 → 值」。</summary>
    public List<TokenUsageFieldDto> RawRows { get; set; } = [];

    /// <summary>沒有原始明細時的說明（檔案不存在或無法解析）；有資料時為空字串。</summary>
    public string RawMessage { get; set; } = string.Empty;

    /// <summary>呼叫當下的費率快照，平攤成「鍵 → 值」。</summary>
    public List<TokenUsageFieldDto> CostRateRows { get; set; } = [];

    /// <summary>這次呼叫的 AI 對話紀錄是否還在（保留天數不同，舊的用量通常已沒有對話可看）。</summary>
    public bool HasConversation { get; set; }
}
