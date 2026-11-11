namespace MyProject.Models.Systems;

/// <summary>
/// 專案清單在畫面條件（關鍵字、分類、團隊）之外的篩選（0.10.3 起，WASM 遷移 M4）：Web API 原本就有的條件，
/// API 改走 <c>ProjectService</c> 後保留下來。全部選填，未填就不篩；畫面不使用。
/// </summary>
public sealed record ProjectListFilter
{
    /// <summary>負責人（完全相同）。</summary>
    public string? Owner { get; init; }

    /// <summary>狀態（完全相同）。</summary>
    public string? Status { get; init; }

    /// <summary>優先順序（完全相同）。</summary>
    public string? Priority { get; init; }

    public DateTime? StartDateFrom { get; init; }

    public DateTime? StartDateTo { get; init; }

    public int? CompletionPercentageMin { get; init; }

    public int? CompletionPercentageMax { get; init; }
}
