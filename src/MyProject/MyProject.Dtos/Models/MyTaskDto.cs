using System.Text.Json.Serialization;

namespace MyProject.Dtos.Models;

/// <summary>
/// 任務資料傳輸物件
/// </summary>
public class MyTaskDto : MyTaskCreateUpdateDto
{
    /// <summary>
    /// 所屬專案名稱
    /// </summary>
    [JsonPropertyName("projectTitle")]
    public string? ProjectTitle { get; set; }
}
