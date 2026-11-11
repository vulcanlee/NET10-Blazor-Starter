namespace MyProject.Dtos.Models;

/// <summary>
/// 專案表單與工具列的選項（0.10.3 起，<c>GET api/v1/Project/options</c>）：分類、團隊過濾與表單的下拉、
/// 新增時預設帶入的團隊，以及附件的限制。與 Server 版相同，不要求分類頁、團隊頁的權限。
/// </summary>
public class ProjectFormOptionsDto
{
    /// <summary>目前使用者看得到的啟用中分類名稱。</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>啟用中的團隊名稱。</summary>
    public List<string> Teams { get; set; } = [];

    /// <summary>目前使用者是管理員（可指定任何團隊）。</summary>
    public bool IsAdmin { get; set; }

    /// <summary>目前使用者的團隊範圍（含下屬部門）；非管理員只能指定這些團隊。</summary>
    public List<string> MyTeams { get; set; } = [];

    /// <summary>新增時預設帶入的團隊：範圍裡最上層的那些（全帶會讓專案對所有下屬公開）。</summary>
    public List<string> DefaultTeams { get; set; } = [];

    /// <summary>允許上傳的副檔名（含點，小寫）。</summary>
    public List<string> AllowedExtensions { get; set; } = [];

    /// <summary>單一附件的大小上限（位元組）。</summary>
    public long MaxUploadFileSize { get; set; }
}
