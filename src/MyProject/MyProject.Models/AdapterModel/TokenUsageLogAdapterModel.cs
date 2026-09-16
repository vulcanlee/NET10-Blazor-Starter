namespace MyProject.Models.AdapterModel;

/// <summary>
/// Token 用量的畫面繫結模型。本頁唯讀（只有刪除，沒有編輯），因此不需要 ICloneable。
/// </summary>
public class TokenUsageLogAdapterModel
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

    public int? DurationSeconds { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public string? RawUsageFile { get; set; }

    /// <summary>依時長計費的呼叫（不回傳 token 數），合計欄要改顯示秒數而非 token。</summary>
    public bool IsDurationBilled => DurationSeconds.HasValue && TotalCount.HasValue == false;
}
