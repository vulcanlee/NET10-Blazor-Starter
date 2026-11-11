using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Admins;

/// <summary>
/// 一則公告（0.10.6 起，WASM 遷移 M7）。清單、新增與修改共用；起訖與更新時間都是<b>伺服器的本地時間</b>（不帶時差送出，
/// 畫面送回時也去掉時區）。寫入時 <see cref="TargetName"/>、<see cref="CreatedByAccount"/>、<see cref="UpdatedAt"/> 不採用。
/// </summary>
public class AnnouncementDto
{
    public int Id { get; set; }

    /// <summary>上限與畫面輸入框相同；去掉前後空白後最多 100 字由服務再檢查。</summary>
    [MaxLength(100, ErrorMessage = "標題不可留空，最多 100 個字。")]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000, ErrorMessage = "內容不可留空，最多 2000 個字。")]
    public string Content { get; set; } = string.Empty;

    public DateTime StartAt { get; set; }

    public DateTime? EndAt { get; set; }

    /// <summary><c>All</c>／<c>Role</c>／<c>Team</c>。</summary>
    [MaxLength(20, ErrorMessage = "對象種類不正確。")]
    public string TargetKind { get; set; } = "All";

    public int? TargetId { get; set; }

    /// <summary>對象名稱（清單顯示用）；角色或團隊已刪除時附註「（已刪除）」。</summary>
    public string? TargetName { get; set; }

    public string? CreatedByAccount { get; set; }

    public DateTime UpdatedAt { get; set; }

    [MaxLength(64, ErrorMessage = "版本號不正確。")]
    public string ConcurrencyStamp { get; set; } = string.Empty;
}

/// <summary>對象下拉選單的一項（未刪除的角色或團隊）。</summary>
public class AnnouncementTargetOptionDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 公告管理頁的內容（<c>GET api/v1/announcements</c>）：清單（開始時間新到舊）、對象選項，以及伺服器的「現在」
/// （本地時間、不帶時差；畫面據此判斷未開始／進行中／已結束，新增時預設開始時間，不用瀏覽器的時鐘）。
/// </summary>
public class AnnouncementListDto
{
    public List<AnnouncementDto> Items { get; set; } = [];

    public List<AnnouncementTargetOptionDto> Roles { get; set; } = [];

    public List<AnnouncementTargetOptionDto> Teams { get; set; } = [];

    public DateTime ServerNow { get; set; }
}
