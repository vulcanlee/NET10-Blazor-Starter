using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.AdapterModel;

public class MyTasAdapterModel : ICloneable, IValidatableObject
{
    public static readonly IReadOnlyList<string> StatusOptions =
    [
        "未開始",
        "進行中",
        "已完成",
        "暫緩",
        "等待"
    ];

    public static readonly IReadOnlyList<string> PriorityOptions =
    [
        "低",
        "中",
        "高"
    ];

    public int Id { get; set; }

    [Required(ErrorMessage = "工作標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "分類 不可為空白")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = StatusOptions[0];

    [Required(ErrorMessage = "優先級 不可為空白")]
    public string Priority { get; set; } = PriorityOptions[1];

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [Required(ErrorMessage = "專案 不可為空白")]
    public int? ProjectId { get; set; }

    public ProjectAdapterModel? Project { get; set; }

    public string ProjectTitle => Project?.Title ?? string.Empty;

    public MyTasAdapterModel Clone()
    {
        return ((ICloneable)this).Clone() as MyTasAdapterModel ?? new MyTasAdapterModel();
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate.Value < StartDate.Value)
        {
            yield return new ValidationResult("結束日期 不可早於開始日期", [nameof(EndDate)]);
        }
    }
}
