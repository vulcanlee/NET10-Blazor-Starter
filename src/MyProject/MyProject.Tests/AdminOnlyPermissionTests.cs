using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 「統計與分析」群組的頁面刻意設計為管理員專屬：權限鍵不列入
/// <see cref="RolePermissionService.GetRoleListPermissionAllName"/>，因此不會種出
/// Permission 資料列、角色矩陣不顯示、任何角色都無法被授予，只有
/// AuthenticationStateHelper.CheckAccessPage 的管理員短路能通過。
///
/// MagicObjectHelper 與 SidebarMenuService 都寫了「請勿補上」的註解，但註解只是建議 ——
/// 這個測試才會真的擋下 PR。日後若有人「順手補齊」漏掉的權限鍵，這裡會紅燈。
/// </summary>
public sealed class AdminOnlyPermissionTests
{
    public static TheoryData<string> AdminOnlyPermissionKeys =>
    [
        MagicObjectHelper.角色_統計與分析,
        MagicObjectHelper.角色_日誌檢視,
        MagicObjectHelper.角色_資料庫用量,
    ];

    [Theory]
    [MemberData(nameof(AdminOnlyPermissionKeys))]
    public void AdminOnlyKeys_ShouldNotAppearInRolePermissionMatrix(string permissionKey)
    {
        var allNames = new RolePermissionService()
            .GetRoleListPermissionAllName()
            .SelectMany(group => group)
            .ToList();

        Assert.DoesNotContain(permissionKey, allNames);
    }
}
