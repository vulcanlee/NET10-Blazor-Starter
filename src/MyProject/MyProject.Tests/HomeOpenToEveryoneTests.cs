using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 首頁不受權限限制（0.10.17 起，使用者 2026/11/14 決定）：以前角色沒有勾「首頁」時，使用者一登入第一個畫面就是「你沒有權限存取此頁面」。
/// 改成每個登入的人都能開首頁（畫面、側邊欄的「首頁」、儀表板 API）；儀表板裡的小工具仍依各自權限。
/// 角色管理的權限矩陣不再有「首頁」勾選框（勾不勾都一樣，留著會讓人以為能擋）。
/// </summary>
public sealed class HomeOpenToEveryoneTests
{
    [Fact]
    public void HomePage_ShouldBeAccessible_WithoutAnyPermission()
    {
        var user = new CurrentUser { Id = 9, IsAuthenticated = true, RoleList = [] };

        Assert.True(PermissionRules.CanAccessPage(user, MagicObjectHelper.角色_首頁));
        Assert.False(PermissionRules.CanAccessPage(user, MagicObjectHelper.角色_專案項目));
    }

    [Fact]
    public void RolePermissionMatrix_ShouldNotOfferTheHomeCheckbox()
    {
        Assert.DoesNotContain(MagicObjectHelper.角色_首頁, new RolePermissionService().GetRolePermissionAllName());
    }
}
