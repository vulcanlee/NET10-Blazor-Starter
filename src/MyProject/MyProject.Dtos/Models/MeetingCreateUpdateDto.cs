using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MyProject.Dtos.Models;

/// <summary>
/// 會議新增/修改資料傳輸物件
/// </summary>
public class MeetingCreateUpdateDto
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [Required(ErrorMessage = "會議名稱 不可為空白")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [StringLength(4000, ErrorMessage = "摘要長度不可超過 4000 字元")]
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [StringLength(2000, ErrorMessage = "與會人員長度不可超過 2000 字元")]
    [JsonPropertyName("participants")]
    public string? Participants { get; set; }

    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }

    [Required(ErrorMessage = "專案代碼 不可為空白")]
    [JsonPropertyName("projectId")]
    public int? ProjectId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}
