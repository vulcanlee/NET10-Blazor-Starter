using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.AdapterModel;

public class MeetingAdapterModel : ICloneable, IValidatableObject
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

    public ProjectAdapterModel? Project { get; set; }

    public string ProjectTitle => Project?.Title ?? string.Empty;

    public List<MeetingFileAdapterModel> Files { get; set; } = [];

    public MeetingAdapterModel Clone()
    {
        return ((ICloneable)this).Clone() as MeetingAdapterModel ?? new MeetingAdapterModel();
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
