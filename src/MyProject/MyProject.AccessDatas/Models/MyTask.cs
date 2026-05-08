using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

public class MyTask
{
    public int Id { get; set; }

    [Required(ErrorMessage = "工作標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "分類 不可為空白")]
    public string Category { get; set; } = string.Empty;

    [Required(ErrorMessage = "狀態 不可為空白")]
    public string Status { get; set; } = string.Empty;

    [Required(ErrorMessage = "優先級 不可為空白")]
    public string Priority { get; set; } = string.Empty;

    [Range(0, 100, ErrorMessage = "完成百分比 必須介於 0 到 100")]
    public int CompletionPercentage { get; set; }

    [Required(ErrorMessage = "負責人 不可為空白")]
    public string Owner { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [Required(ErrorMessage = "專案 不可為空白")]
    public int? ProjectId { get; set; }

    public Project? Project { get; set; }

    public ICollection<MyTasFile> Files { get; set; } = [];
}
