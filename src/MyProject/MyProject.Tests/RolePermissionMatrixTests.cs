using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Views.Admins;

namespace MyProject.Tests;

/// <summary>
/// 角色權限矩陣的顯示判斷與「複製角色」（0.9.139 起）。
/// </summary>
public sealed class RolePermissionMatrixTests
{
    [Fact]
    public void IsActionShownChecked_ShouldBeTrueForEveryAction_WhenAllIsChecked()
    {
        var node = new RolePermissionNode { Name = "專案項目", Enable = true };

        Assert.True(RolePermissionMatrix.IsActionShownChecked(node, PermissionActions.View));
        Assert.True(RolePermissionMatrix.IsActionShownChecked(node, PermissionActions.Delete));
    }

    [Fact]
    public void IsActionShownChecked_ShouldFollowActions_WhenAllIsNotChecked()
    {
        var node = new RolePermissionNode
        {
            Name = "專案項目",
            Actions = new() { [PermissionActions.View] = true, [PermissionActions.Edit] = false },
        };

        Assert.True(RolePermissionMatrix.IsActionShownChecked(node, PermissionActions.View));
        Assert.False(RolePermissionMatrix.IsActionShownChecked(node, PermissionActions.Edit));
        Assert.False(RolePermissionMatrix.IsActionShownChecked(node, PermissionActions.Create));
    }

    [Fact]
    public void IsActionShownChecked_ShouldBeFalse_WhenActionsIsNull()
    {
        var node = new RolePermissionNode { Name = "專案項目", Actions = null! };

        Assert.False(RolePermissionMatrix.IsActionShownChecked(node, PermissionActions.View));
    }

    [Fact]
    public void HasAnyGrant_ShouldBeFalse_WhenNothingIsChecked()
    {
        var group = CreateGroup();
        group.Permissions[0].Actions[PermissionActions.View] = false;

        Assert.False(RolePermissionMatrix.HasAnyGrant(group));
    }

    [Fact]
    public void HasAnyGrant_ShouldBeTrue_WhenOnlyOneActionIsChecked()
    {
        var group = CreateGroup();
        group.Permissions[1].Actions[PermissionActions.Edit] = true;

        Assert.True(RolePermissionMatrix.HasAnyGrant(group));
    }

    [Fact]
    public void HasAnyGrant_ShouldBeTrue_WhenOnlyAllIsChecked()
    {
        var group = CreateGroup();
        group.Permissions[0].Enable = true;

        Assert.True(RolePermissionMatrix.HasAnyGrant(group));
    }

    [Fact]
    public void HasAnyGrant_ShouldBeTrue_WhenOnlyGroupIsChecked()
    {
        var group = CreateGroup();
        group.Enable = true;

        Assert.True(RolePermissionMatrix.HasAnyGrant(group));
    }

    [Fact]
    public void CreateCopy_ShouldCarrySettingsAndAppendCopySuffix()
    {
        var source = CreateRole();

        var copy = RolePermissionMatrix.CreateCopy(source);

        Assert.Equal("業務人員 - 複本", copy.Name);
        Assert.Equal(["業務一組"], copy.DefaultTeams);
        Assert.True(copy.RequireTwoFactor);
        Assert.True(copy.RolePermission.Groups[0].Enable);
        Assert.True(copy.RolePermission.Groups[0].Permissions[0].Actions[PermissionActions.View]);
    }

    [Fact]
    public void CreateCopy_ShouldNotCarryIdentityOrConcurrencyOrDeletion()
    {
        var copy = RolePermissionMatrix.CreateCopy(CreateRole());

        Assert.Equal(0, copy.Id);
        Assert.Equal(string.Empty, copy.ConcurrencyStamp);
        Assert.Equal(string.Empty, copy.TabViewJson);
        Assert.False(copy.IsDeleted);
        Assert.Null(copy.DeletedAt);
        Assert.Null(copy.DeletedBy);
    }

    [Fact]
    public void CreateCopy_ShouldDeepCopy_SoEditingCopyLeavesSourceUntouched()
    {
        var source = CreateRole();

        var copy = RolePermissionMatrix.CreateCopy(source);
        copy.DefaultTeams.Add("業務二組");
        copy.RolePermission.Groups[0].Enable = false;
        copy.RolePermission.Groups[0].Permissions[0].Actions[PermissionActions.View] = false;

        Assert.Equal(["業務一組"], source.DefaultTeams);
        Assert.True(source.RolePermission.Groups[0].Enable);
        Assert.True(source.RolePermission.Groups[0].Permissions[0].Actions[PermissionActions.View]);
    }

    private static RolePermissionGroup CreateGroup() => new()
    {
        Name = "資料定義管理功能",
        Permissions =
        [
            new RolePermissionNode { Name = "分類清單" },
            new RolePermissionNode { Name = "團隊清單" },
        ],
    };

    private static RoleViewAdapterModel CreateRole()
    {
        var group = CreateGroup();
        group.Enable = true;
        group.Permissions[0].Actions[PermissionActions.View] = true;

        return new RoleViewAdapterModel
        {
            Id = 42,
            ConcurrencyStamp = "stamp",
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 10, 1),
            DeletedBy = "admin",
            Name = "業務人員",
            TabViewJson = "分類清單:view",
            DefaultTeams = ["業務一組"],
            RequireTwoFactor = true,
            RolePermission = new RolePermission { Groups = [group] },
        };
    }
}
