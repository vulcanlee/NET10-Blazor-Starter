using System.Text.Json.Serialization;

namespace MyProject.Dtos.Models;

/// <summary>
/// 會議資料傳輸物件
/// </summary>
public class MeetingDto : MeetingCreateUpdateDto
{
    [JsonPropertyName("projectTitle")]
    public string? ProjectTitle { get; set; }
}
