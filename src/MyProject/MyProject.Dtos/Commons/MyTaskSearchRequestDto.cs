namespace MyProject.Dtos.Commons;

/// <summary>
/// 任務搜尋請求參數
/// </summary>
public class MyTaskSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 分類
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// 擁有者
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// 狀態
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// 優先順序
    /// </summary>
    public string? Priority { get; set; }

    /// <summary>
    /// 所屬專案代碼
    /// </summary>
    public int? ProjectId { get; set; }

    /// <summary>
    /// 開始日期(起)
    /// </summary>
    public DateTime? StartDateFrom { get; set; }

    /// <summary>
    /// 開始日期(迄)
    /// </summary>
    public DateTime? StartDateTo { get; set; }

    /// <summary>
    /// 完成百分比(最小)
    /// </summary>
    public int? CompletionPercentageMin { get; set; }

    /// <summary>
    /// 完成百分比(最大)
    /// </summary>
    public int? CompletionPercentageMax { get; set; }
}
