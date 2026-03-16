namespace MyProject.Dtos.Commons;

/// <summary>
/// 會議搜尋請求參數
/// </summary>
public class MeetingSearchRequestDto : SearchRequestBaseDto
{
    public int? ProjectId { get; set; }

    public DateTime? StartDateFrom { get; set; }

    public DateTime? StartDateTo { get; set; }

    public string? ParticipantsKeyword { get; set; }
}
