using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;

namespace MyProject.Web.Components.Views.Admins
{
    /// <summary>
    /// 角色管理權限矩陣的顯示判斷與「複製角色」（0.9.139 起）。純函式，無 I/O、無 DI。
    /// </summary>
    public static class RolePermissionMatrix
    {
        /// <summary>
        /// 動作勾選框要不要顯示成打勾。勾了「（全部）」時四個動作一律顯示打勾（並反灰），
        /// 否則會看起來像沒有權限；只影響顯示，<see cref="RolePermissionNode.Actions"/> 不動，
        /// 取消「（全部）」後回到原本的個別勾選。
        /// </summary>
        public static bool IsActionShownChecked(RolePermissionNode node, string action)
            => node.Enable || IsActionChecked(node, action);

        /// <summary>這個動作本身有沒有勾（不看「（全部）」）。</summary>
        public static bool IsActionChecked(RolePermissionNode node, string action)
            => node.Actions is not null && node.Actions.TryGetValue(action, out var enabled) && enabled;

        /// <summary>群組或底下任何一項有勾選，開窗時就展開；完全沒勾的收合。</summary>
        public static bool HasAnyGrant(RolePermissionGroup group)
            => group.Enable
               || group.Permissions.Any(node => node.Enable || (node.Actions?.Values.Any(enabled => enabled) ?? false));

        /// <summary>
        /// 以既有角色為範本建立新角色：名稱、預設團隊、兩步驟驗證、權限照抄（深複製），名稱加上「 - 複本」以免撞名。
        /// ⚠️ 不可用 source.Clone()：那會帶到 Id 與 ConcurrencyStamp，存檔就變成去改來源角色。
        /// </summary>
        public static RoleViewAdapterModel CreateCopy(RoleViewAdapterModel source)
        {
            ArgumentNullException.ThrowIfNull(source);

            return new RoleViewAdapterModel
            {
                Name = $"{source.Name} - 複本",
                RolePermission = source.RolePermission.Clone(),
                DefaultTeams = new List<string>(source.DefaultTeams),
                RequireTwoFactor = source.RequireTwoFactor,
            };
        }
    }
}
