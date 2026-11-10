using MyProject.Dtos.Shell;

namespace MyProject.Web.Client.Layout;

/// <summary>WASM 外框的選單項目（由 <see cref="MenuItemDto"/> 轉來；欄位與 Server 版 <c>SidebarMenuItemModel</c> 相同，側邊欄標記可以沿用）。</summary>
public sealed class ClientMenuItem
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Icon { get; init; }

    public string? Url { get; init; }

    public List<ClientMenuItem> SubMenu { get; init; } = [];

    public bool HasChildren => SubMenu.Count > 0;

    public static ClientMenuItem From(MenuItemDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        Icon = string.IsNullOrWhiteSpace(dto.Icon) ? null : dto.Icon,
        Url = string.IsNullOrWhiteSpace(dto.Url) ? null : dto.Url,
        SubMenu = dto.SubMenu.Select(From).ToList(),
    };

    /// <summary>選單圖示別名 → Material Icon 名稱（與 Server 版側邊欄相同的對照）。</summary>
    public string IconKind
    {
        get
        {
            var icon = Icon?.Trim();
            if (string.IsNullOrWhiteSpace(icon))
            {
                return HasChildren ? "folder_open" : "article";
            }

            return icon switch
            {
                "home" => "home",
                "dashboard" => "space_dashboard",
                "admin" => "admin_panel_settings",
                "users" => "group",
                "roles" => "security",
                "shield_person" => "security",
                "setting" => "settings",
                "ProjectFilled" => "workspaces",
                "CarryOutFilled" => "checklist",
                "EnterOutlined" => "event",
                _ => icon,
            };
        }
    }
}
