using MyProject.Dtos.Commons;

namespace MyProject.Dtos.Admins;

/// <summary>角色清單的查詢（0.10.4 起，<c>POST api/v1/roles/search</c>）。關鍵字比對名稱；排序支援 Name、CreateAt、UpdateAt。</summary>
public class RoleSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>true：查「顯示已刪除」的清單，固定依刪除時間由新到舊。</summary>
    public bool Deleted { get; set; }

    /// <summary>true：使用者按下「搜尋」，伺服器寫一筆搜尋稽核。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>
/// 角色（0.10.4 起）。<see cref="Permission"/> 是權限矩陣（群組 → 頁面 → 動作），與畫面的勾選框一一對應；
/// 伺服器把它轉成權限鍵存檔（規則與 Server 版相同）。
/// </summary>
public class RoleDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>有這個角色的人要先設定兩步驟驗證。</summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>預設團隊（使用者沒有直接指定團隊時沿用）。</summary>
    public List<string> DefaultTeams { get; set; } = [];

    public RolePermissionDto Permission { get; set; } = new();

    public DateTime CreateAt { get; set; }

    public DateTime UpdateAt { get; set; }

    /// <summary>樂觀並行的版本號：修改時帶回開窗時的值，與資料庫不同代表別人已先修改（409）。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public string? DeletedBy { get; set; }
}

/// <summary>權限矩陣。</summary>
public class RolePermissionDto
{
    public List<RolePermissionGroupDto> Groups { get; set; } = [];
}

public class RolePermissionGroupDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>群組本身的勾選（側邊選單的群組）。</summary>
    public bool Enable { get; set; }

    public List<RolePermissionNodeDto> Permissions { get; set; } = [];
}

public class RolePermissionNodeDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>「（全部）」：這個頁面的所有動作。</summary>
    public bool Enable { get; set; }

    /// <summary>動作代碼（view／create／edit／delete）→ 是否勾選。</summary>
    public Dictionary<string, bool> Actions { get; set; } = [];
}

/// <summary>角色表單的選項（<c>GET api/v1/roles/options</c>）：啟用中的團隊與空白的權限矩陣（新增用）。</summary>
public class RoleFormOptionsDto
{
    public List<string> Teams { get; set; } = [];

    public RolePermissionDto EmptyPermission { get; set; } = new();
}
