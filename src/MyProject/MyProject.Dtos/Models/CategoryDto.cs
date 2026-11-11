namespace MyProject.Dtos.Models;

/// <summary>
/// 分類資料傳輸物件
/// </summary>
public class CategoryDto : CategoryCreateUpdateDto
{
    public CategoryDto()
    {
    }

    /// <summary>已刪除（0.10.2 起回傳，「顯示已刪除」清單用）。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>刪除時間。</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>刪除者帳號。</summary>
    public string? DeletedBy { get; set; }
}
