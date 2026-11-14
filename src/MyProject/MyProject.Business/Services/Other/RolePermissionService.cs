using MyProject.AccessDatas.Models;
using MyProject.Models.Admins;
using MyProject.Share.Helpers;

namespace MyProject.Business.Services.Other;

public class RolePermissionService
{
    public List<List<string>> GetRoleListPermissionAllName()
    {
        // 「首頁」0.10.17 起不在矩陣裡：每個登入的人都能開（PermissionRules.OpenToEveryone）。既有角色存著的「首頁」鍵留著無妨。
        return
        [
            [
                MagicObjectHelper.角色_專案管理,
                MagicObjectHelper.角色_專案項目,
            ],
            [
                MagicObjectHelper.角色_資料定義,
                MagicObjectHelper.角色_分類清單,
                MagicObjectHelper.角色_團隊清單,
            ],
            [
                MagicObjectHelper.角色_AI助理,
                MagicObjectHelper.角色_Agent測試台,
                MagicObjectHelper.角色_Agent工作流程,
            ],
            [MagicObjectHelper.角色_登出],
        ];
    }

    public List<string> GetRolePermissionAllName()
    {
        var result = GetRoleListPermissionAllName()
            .SelectMany(x => x)
            .ToList();

        return result;
    }

    /// <summary>
    /// 權限目錄（<see cref="Permission"/> 表）應有的頁面鍵，含群組名稱與排序；每次呼叫都回傳新的實體。
    ///
    /// RBAC 回填與 <c>DefaultRoleViewSeeder</c> 共用這一份：預設角色的「新頁面」偵測依賴目錄內容，
    /// 兩邊各自組列的話，群組或排序只要差一點，就會出現同一個鍵兩種長相。
    /// </summary>
    public List<Permission> CreatePermissionCatalog()
    {
        var result = new List<Permission>();
        var sort = 0;
        foreach (var group in GetRoleListPermissionAllName())
        {
            var groupName = group.FirstOrDefault() ?? string.Empty;
            foreach (var name in group)
            {
                sort++;
                result.Add(new Permission
                {
                    Key = name,
                    DisplayName = name,
                    GroupName = groupName,
                    SortOrder = sort,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// 不自動開給「預設角色」的頁面（0.9.142 起）：會花 AI 費用的 Agent 頁面，由管理員自己決定開給哪些角色。
    /// 這些頁面照樣在權限目錄裡、照樣可以授權；只是建立預設角色與升級加入新頁面時略過它們。
    /// </summary>
    public static IReadOnlySet<string> NotGrantedToDefaultRole { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        MagicObjectHelper.角色_AI助理,
        MagicObjectHelper.角色_Agent測試台,
        MagicObjectHelper.角色_Agent工作流程,
    };

    /// <summary>預設角色應有的頁面：全部頁面扣掉 <see cref="NotGrantedToDefaultRole"/>。</summary>
    public List<string> GetDefaultRolePermissionNames()
        => GetRolePermissionAllName().Where(x => !NotGrantedToDefaultRole.Contains(x)).ToList();

    public string GetDefaultRolePermissionNamesToJson()
        => Newtonsoft.Json.JsonConvert.SerializeObject(GetDefaultRolePermissionNames());

    public RolePermission InitializePermissionSetting()
    {
        var result = new RolePermission();

        foreach (var permissionNames in GetRoleListPermissionAllName())
        {
            if (permissionNames.Count == 0)
            {
                continue;
            }

            var group = new RolePermissionGroup
            {
                Name = permissionNames[0],
                Enable = false,
            };

            foreach (var item in permissionNames.Skip(1))
            {
                group.Permissions.Add(new RolePermissionNode
                {
                    Name = item,
                    Enable = false,
                    Actions = CreateEmptyActions(),
                });
            }

            result.Groups.Add(group);
        }

        return result;
    }

    /// <summary>權限矩陣支援的動作代碼（顯示順序）。</summary>
    public static readonly IReadOnlyList<string> SupportedActions =
    [
        PermissionActions.View,
        PermissionActions.Create,
        PermissionActions.Edit,
        PermissionActions.Delete,
    ];

    private static Dictionary<string, bool> CreateEmptyActions()
        => SupportedActions.ToDictionary(action => action, _ => false, StringComparer.Ordinal);

    public void SetPermissionInput(RolePermission rolePermission, List<string> permissions)
    {
        var permissionLookup = permissions.ToHashSet(StringComparer.Ordinal);

        foreach (var group in rolePermission.Groups)
        {
            group.Enable = permissionLookup.Contains(group.Name);

            foreach (var item in group.Permissions)
            {
                item.Enable = permissionLookup.Contains(item.Name);

                item.Actions ??= CreateEmptyActions();
                foreach (var action in SupportedActions)
                {
                    item.Actions[action] = permissionLookup.Contains(PermissionKey.For(item.Name, action));
                }
            }
        }
    }

    public List<string> GetPermissionInput(RolePermission rolePermission)
    {
        var result = new List<string>();

        foreach (var group in rolePermission.Groups)
        {
            if (group.Enable)
            {
                result.Add(group.Name);
            }

            foreach (var node in group.Permissions)
            {
                if (node.Enable)
                {
                    // 裸頁面鍵＝該頁全部動作（舊制相容）。
                    result.Add(node.Name);
                    continue;
                }

                foreach (var action in SupportedActions)
                {
                    if (node.Actions is not null && node.Actions.TryGetValue(action, out var enabled) && enabled)
                    {
                        result.Add(PermissionKey.For(node.Name, action));
                    }
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    public string GetPermissionInputToJson(RolePermission rolePermission)
    {
        var items = GetPermissionInput(rolePermission);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
        return json;
    }
}
