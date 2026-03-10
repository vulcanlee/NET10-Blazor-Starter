using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

public class Meeting
{
    public int Id { get; set; }

    [Required(ErrorMessage = "會議標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Summary { get; set; }

    public string? Participants { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [Required(ErrorMessage = "專案 不可為空白")]
    public int? ProjectId { get; set; }

    public Project? Project { get; set; }
}
