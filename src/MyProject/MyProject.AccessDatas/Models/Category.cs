using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

/// <summary>
/// 分類（主資料，獨立無外鍵關聯）
/// </summary>
public class Category
{
    public int Id { get; set; }

    [Required(ErrorMessage = "分類名稱 不可為空白")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>適用團隊（多值，以分隔字串儲存，可空；未設定表示所有團隊皆可使用）</summary>
    public string? Teams { get; set; }

    /// <summary>是否啟用</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
