namespace MyProject.Dtos.Models;

/// <summary>
/// 專案資料傳輸物件
/// </summary>
public class ProjectDto : ProjectCreateUpdateDto
{
    public ProjectDto()
    {
    }

    /// <summary>附件（0.10.3 起回傳；清單搜尋不帶，單筆 <c>GET {id}</c> 才有）。</summary>
    public List<ProjectFileDto> Files { get; set; } = [];

    /// <summary>已刪除（0.10.3 起回傳，「顯示已刪除」清單用）。</summary>
    public bool IsDeleted { get; set; }

    /// <summary>刪除時間。</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>刪除者帳號。</summary>
    public string? DeletedBy { get; set; }

}
