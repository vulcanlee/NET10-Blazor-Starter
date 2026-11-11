namespace MyProject.Dtos.Models;

/// <summary>
/// 分類表單的選項（0.10.2 起，<c>GET api/v1/Category/options</c>）：「適用團隊」下拉與存檔前的團隊提醒用。
/// 不要求團隊頁的權限（與 Server 版相同：能維護分類就看得到啟用中的團隊名稱）。
/// </summary>
public class CategoryFormOptionsDto
{
    /// <summary>啟用中的團隊名稱（依名稱排序）。</summary>
    public List<string> Teams { get; set; } = [];

    /// <summary>目前使用者是管理員（可指定任何團隊）。</summary>
    public bool IsAdmin { get; set; }

    /// <summary>目前使用者的團隊範圍（含下屬部門）；非管理員只能指定這些團隊。</summary>
    public List<string> MyTeams { get; set; } = [];
}
