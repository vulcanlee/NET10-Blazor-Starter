using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 依動作權限顯示按鈕（0.9.110 起）。四種身分：管理員、有動作鍵、只有裸頁面鍵（舊制＝全動作）、只有「頁面:view」。
/// </summary>
public sealed class RequirePermissionTests : ComponentTestBase
{
    private const string Page = MagicObjectHelper.角色_分類清單;

    public static TheoryData<string, string[], bool, string, bool> Cases => new()
    {
        { "管理員", [], true, PermissionActions.Delete, true },
        { "有刪除動作鍵", [PermissionKey.For(Page, PermissionActions.Delete)], false, PermissionActions.Delete, true },
        { "有修改動作鍵但要刪除", [PermissionKey.For(Page, PermissionActions.Edit)], false, PermissionActions.Delete, false },
        { "只有裸頁面鍵", [Page], false, PermissionActions.Create, true },
        { "只有檢視", [PermissionKey.For(Page, PermissionActions.View)], false, PermissionActions.Edit, false },
        { "別頁的動作鍵", [PermissionKey.For(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)], false, PermissionActions.Delete, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShouldRenderChildContent_OnlyWhenTheActionIsAllowed(string scenario, string[] keys, bool isAdmin, string action, bool expected)
    {
        var user = new CurrentUser { Id = 9, Account = "dave", IsAdmin = isAdmin, RoleList = [.. keys] };
        Services.AddSingleton(new CurrentUserService { CurrentUser = user });

        var cut = Render<RequirePermission>(parameters => parameters
            .Add(p => p.Page, Page)
            .Add(p => p.Action, action)
            .AddChildContent("<button class=\"guarded\">刪除</button>"));

        Assert.True(expected == (cut.FindAll("button.guarded").Count == 1), scenario);
        Assert.Equal(expected, PermissionRules.CanAccessAction(user, Page, action));
    }
}
