using Microsoft.EntityFrameworkCore;
using MyProject.Web.Ai.Agents.AdminAssistant;
using Fixture = MyProject.Tests.AccountsAssistantToolsTests.DbFixture;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「帳號與權限」的異動工具（0.9.158 起）：批次逐筆結果、上限、最後一位啟用中的管理員、不能對自己做的事、
/// 改角色時保留團隊與額外角色、權限鍵驗證、軟刪除與還原；稽核由服務自己寫，工具不重複寫。
/// </summary>
public sealed class AccountsMutationToolsTests
{
    [Fact]
    public async Task SetUsersEnabled_DisablesBatch_WithPerItemResults()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");
        await fixture.AddUserAsync("temp-b");

        var json = await fixture.Tools.SetUsersEnabledAsync(["temp-a", "temp-b", "ghost"], enabled: false);

        Assert.Contains("\"succeeded\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.Contains("找不到帳號", json, StringComparison.Ordinal);
        Assert.False((await fixture.UserAsync("temp-a")).Status);
        Assert.False((await fixture.UserAsync("temp-b")).Status);
        Assert.Empty(fixture.Audit.Entries);
        Assert.True(await fixture.Context.AuditLog.AnyAsync(x => x.Action == "User.Disable"));
    }

    [Fact]
    public async Task SetUsersEnabled_MoreThan50_RejectsWholeBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");

        var text = await fixture.Tools.SetUsersEnabledAsync([.. Enumerable.Range(1, 51).Select(i => $"u{i}")], enabled: false);

        Assert.Contains("50", text, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("temp-a")).Status);
    }

    [Fact]
    public async Task DeleteUsers_WouldLeaveNoEnabledAdmin_RejectsWholeBatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("admin1", isAdmin: true);
        await fixture.AddUserAsync("temp-a");

        var text = await fixture.Tools.DeleteUsersAsync(["admin1", "temp-a"]);

        Assert.Contains("管理員", text, StringComparison.Ordinal);
        Assert.False((await fixture.UserAsync("admin1")).IsDeleted);
        Assert.False((await fixture.UserAsync("temp-a")).IsDeleted);
    }

    [Fact]
    public async Task SetUsersEnabled_DisableLastEnabledAdmin_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("admin1", isAdmin: true);

        var text = await fixture.Tools.SetUsersEnabledAsync(["admin1"], enabled: false);

        Assert.Contains("管理員", text, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("admin1")).Status);
    }

    /// <summary>帳號區分大小寫：Bob 與 bob 可以同時存在。給 BOB 時不能猜是誰（核准窗只看得到 BOB），要回報有歧義、不執行。</summary>
    [Fact]
    public async Task SetUsersEnabled_AmbiguousCaseInsensitiveName_NotExecuted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("Bob");
        await fixture.AddUserAsync("bob");

        var json = await fixture.Tools.SetUsersEnabledAsync(["BOB"], enabled: false);

        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("Bob")).Status);
        Assert.True((await fixture.UserAsync("bob")).Status);
    }

    [Fact]
    public async Task DeleteThenRestoreUsers_RoundTrip()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");

        Assert.Contains("\"succeeded\":1", await fixture.Tools.DeleteUsersAsync(["temp-a"]), StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("temp-a")).IsDeleted);
        Assert.Contains("\"succeeded\":1", await fixture.Tools.RestoreUsersAsync(["temp-a"]), StringComparison.Ordinal);
        Assert.False((await fixture.UserAsync("temp-a")).IsDeleted);
    }

    [Fact]
    public async Task ForceLogout_Self_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var selfId = await fixture.AddUserAsync("me", isAdmin: true);
        fixture.Current.CurrentUser.Id = selfId;
        fixture.Current.CurrentUser.Account = "me";
        var stampBefore = (await fixture.UserAsync("me")).SecurityStamp;

        var json = await fixture.Tools.ForceLogoutUsersAsync(["me"]);

        Assert.Contains("自己", json, StringComparison.Ordinal);
        Assert.Equal(stampBefore, (await fixture.UserAsync("me")).SecurityStamp);
    }

    [Fact]
    public async Task UnlockUsers_NotLocked_ReportsPerItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");

        var json = await fixture.Tools.UnlockUsersAsync(["temp-a"]);

        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.Contains("沒有被鎖定", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetUserAdmin_RemoveFromSelf_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("admin2", isAdmin: true);
        var selfId = await fixture.AddUserAsync("me", isAdmin: true);
        fixture.Current.CurrentUser.Id = selfId;

        var text = await fixture.Tools.SetUserAdminAsync("me", isAdmin: false);

        Assert.Contains("自己", text, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("me")).IsAdmin);
    }

    [Fact]
    public async Task SetUserAdmin_RemoveLastEnabledAdmin_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("admin1", isAdmin: true);

        var text = await fixture.Tools.SetUserAdminAsync("admin1", isAdmin: false);

        Assert.Contains("管理員", text, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("admin1")).IsAdmin);
    }

    [Fact]
    public async Task SetUserAdmin_Grant_KeepsTeamsAndAdditionalRoles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var extraRole = await fixture.AddRoleAsync("額外");
        await fixture.AddTeamAsync("團隊A");
        var id = await fixture.AddUserAsync("temp-a", additionalRoleIds: [extraRole], teamNames: ["團隊A"]);

        var text = await fixture.Tools.SetUserAdminAsync("temp-a", isAdmin: true);

        Assert.Contains("已", text, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("temp-a")).IsAdmin);
        var (roles, teams) = await fixture.AssignmentsAsync(id);
        Assert.Equal([extraRole], roles);
        Assert.Equal(["團隊A"], teams);
    }

    [Fact]
    public async Task SetUserRoles_UnknownRole_RejectedWithAvailableList()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");

        var text = await fixture.Tools.SetUserRolesAsync("temp-a", primaryRole: "不存在", additionalRoles: null);

        Assert.Contains("不存在", text, StringComparison.Ordinal);
        Assert.Contains("一般", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetUserRoles_ReplacesAdditionalRoles_KeepsTeams()
    {
        await using var fixture = await Fixture.CreateAsync();
        var r1 = await fixture.AddRoleAsync("角色1");
        var r2 = await fixture.AddRoleAsync("角色2");
        await fixture.AddTeamAsync("團隊A");
        var id = await fixture.AddUserAsync("temp-a", additionalRoleIds: [r1], teamNames: ["團隊A"]);

        await fixture.Tools.SetUserRolesAsync("temp-a", primaryRole: null, additionalRoles: ["角色2"]);

        var (roles, teams) = await fixture.AssignmentsAsync(id);
        Assert.Equal([r2], roles);
        Assert.Equal(["團隊A"], teams);
    }

    /// <summary>改自己的角色會換掉工作階段版本，等於把自己強制登出（頁面存檔後會換發 Cookie，工具沒有），所以擋下。</summary>
    [Fact]
    public async Task SetUserRoles_Self_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync("角色2");
        var selfId = await fixture.AddUserAsync("me", isAdmin: true);
        fixture.Current.CurrentUser.Id = selfId;

        var text = await fixture.Tools.SetUserRolesAsync("me", primaryRole: "角色2", additionalRoles: null);

        Assert.Contains("自己", text, StringComparison.Ordinal);
        Assert.Equal(fixture.DefaultRoleId, (await fixture.UserAsync("me")).RoleViewId);
    }

    /// <summary>角色只有頁面鍵（＝全部動作）時移除單一動作：展開成其餘動作，不能誤報「本來就沒有」。</summary>
    [Fact]
    public async Task UpdateRolePermissions_RemoveActionFromBarePageKey_ExpandsRemainingActions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Tools.UpdateRolePermissionsAsync("一般", addKeys: ["專案項目"], removeKeys: null);

        var json = await fixture.Tools.UpdateRolePermissionsAsync("一般", addKeys: null, removeKeys: ["專案項目:delete"]);

        Assert.Contains("\"applied\":true", json, StringComparison.Ordinal);
        var keys = AccountsAssistantTools.ParsePermissionKeys(
            await fixture.Context.RoleView.AsNoTracking().Where(x => x.Name == "一般").Select(x => x.TabViewJson).SingleAsync());
        Assert.Equal(["專案項目:create", "專案項目:edit", "專案項目:view"], keys.Order());
    }

    /// <summary>已有頁面鍵時再加入單一動作：已涵蓋，不修改、不寫稽核。</summary>
    [Fact]
    public async Task UpdateRolePermissions_AddActionCoveredByBarePageKey_NoChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Tools.UpdateRolePermissionsAsync("一般", addKeys: ["專案項目"], removeKeys: null);
        var updatesBefore = await fixture.Context.AuditLog.CountAsync(x => x.Action == "Role.Update");

        var json = await fixture.Tools.UpdateRolePermissionsAsync("一般", addKeys: ["專案項目:edit"], removeKeys: null);

        Assert.Contains("\"applied\":false", json, StringComparison.Ordinal);
        Assert.Contains("alreadyCovered", json, StringComparison.Ordinal);
        Assert.Equal(updatesBefore, await fixture.Context.AuditLog.CountAsync(x => x.Action == "Role.Update"));
    }

    [Fact]
    public async Task UpdateRolePermissions_InvalidKeysReported_ValidApplied()
    {
        await using var fixture = await Fixture.CreateAsync();

        var json = await fixture.Tools.UpdateRolePermissionsAsync("一般", addKeys: ["專案項目:edit", "不存在的鍵"], removeKeys: null);

        Assert.Contains("不存在的鍵", json, StringComparison.Ordinal);
        var tabViewJson = await fixture.Context.RoleView.AsNoTracking().Where(x => x.Name == "一般").Select(x => x.TabViewJson).SingleAsync();
        Assert.Contains("專案項目:edit", tabViewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("不存在的鍵", tabViewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteThenRestoreRoles_RoundTrip()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync("暫時角色");

        Assert.Contains("\"succeeded\":1", await fixture.Tools.DeleteRolesAsync(["暫時角色"]), StringComparison.Ordinal);
        Assert.True(await fixture.Context.RoleView.IgnoreQueryFilters().AnyAsync(x => x.Name == "暫時角色" && x.IsDeleted));
        Assert.Contains("\"succeeded\":1", await fixture.Tools.RestoreRolesAsync(["暫時角色"]), StringComparison.Ordinal);
        Assert.True(await fixture.Context.RoleView.AnyAsync(x => x.Name == "暫時角色"));
    }
}
