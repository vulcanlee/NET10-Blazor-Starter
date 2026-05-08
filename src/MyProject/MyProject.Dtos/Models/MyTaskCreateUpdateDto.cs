using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MyProject.Dtos.Models;

/// <summary>
/// 任務新增/修改資料傳輸物件
/// </summary>
public class MyTaskCreateUpdateDto
{
    /// <summary>
    /// 任務唯一代碼
    /// </summary>
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    /// <summary>
    /// 任務名稱
    /// </summary>
    [Required(ErrorMessage = "任務名稱 不可為空白")]
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 任務描述
    /// </summary>
    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 任務開始日期
    /// </summary>
    [JsonPropertyName("startDate")]
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// 任務結束日期
    /// </summary>
    [JsonPropertyName("endDate")]
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// 任務分類
    /// </summary>
    [Required(ErrorMessage = "分類 不可為空白")]
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 任務狀態
    /// </summary>
    [Required(ErrorMessage = "狀態 不可為空白")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 優先順序
    /// </summary>
    [Required(ErrorMessage = "優先順序 不可為空白")]
    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    /// <summary>
    /// 完成百分比
    /// </summary>
    [Range(0, 100, ErrorMessage = "完成百分比必須在 0-100 之間")]
    [JsonPropertyName("completionPercentage")]
    public int CompletionPercentage { get; set; }

    /// <summary>
    /// 負責人
    /// </summary>
    [Required(ErrorMessage = "負責人 不可為空白")]
    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// 專案代碼
    /// </summary>
    [Required(ErrorMessage = "專案代碼 不可為空白")]
    [JsonPropertyName("projectId")]
    public int? ProjectId { get; set; }

    /// <summary>
    /// 建立時間
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// 更新時間
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}
