using Microsoft.EntityFrameworkCore;
using MyProject.Web.Ai.Agents.AdminAssistant;
using Fixture = MyProject.Tests.AccountsAssistantToolsTests.DbFixture;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「帳號與權限」的不可復原工具（0.9.159 起）：徹底刪除只找已軟刪除的、重設 2FA 擋自己與未啟用、
/// 清空稽核後寫一筆 <c>Audit.ClearAll</c>。徹底刪除與重設 2FA 的稽核由服務寫，工具不重複寫。
/// </summary>
public sealed class AccountsIrreversibleToolsTests
{
    [Fact]
    public async Task PurgeUsers_OnlySoftDeletedAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");
        await fixture.AddUserAsync("temp-b");
        await fixture.Tools.DeleteUsersAsync(["temp-a"]);

        var json = await fixture.Tools.PurgeUsersAsync(["temp-a", "temp-b"]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.False(await fixture.Context.MyUser.IgnoreQueryFilters().AnyAsync(x => x.Account == "temp-a"));
        Assert.False((await fixture.UserAsync("temp-b")).IsDeleted);
        Assert.True(await fixture.Context.AuditLog.AnyAsync(x => x.Action == "User.Purge"));
        Assert.Empty(fixture.Audit.Entries);
    }

    /// <summary>
    /// 徹底刪除前要能確認對象（實機發現：原本查不到已軟刪除的帳號，助理只好拒絕）：
    /// search_users 的 status=deleted 與 list_roles 的 includeDeleted 比照頁面的「顯示已刪除」，稽核記 showDeleted=true。
    /// </summary>
    [Fact]
    public async Task SearchUsersAndListRoles_CanListSoftDeleted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");
        await fixture.AddUserAsync("temp-b");
        await fixture.AddRoleAsync("暫時角色");
        await fixture.Tools.DeleteUsersAsync(["temp-a"]);
        await fixture.Tools.DeleteRolesAsync(["暫時角色"]);
        fixture.Audit.Entries.Clear();

        var deletedUsers = await fixture.Tools.SearchUsersAsync(status: "deleted");
        var roles = await fixture.Tools.ListRolesAsync(includeDeleted: true);

        Assert.Contains("temp-a", deletedUsers, StringComparison.Ordinal);
        Assert.DoesNotContain("temp-b", deletedUsers, StringComparison.Ordinal);
        Assert.Contains("暫時角色", roles, StringComparison.Ordinal);
        Assert.Contains("\"deleted\":true", roles, StringComparison.Ordinal);
        Assert.All(fixture.Audit.Entries, entry => Assert.Contains("showDeleted=true", entry.Detail, StringComparison.Ordinal));
        Assert.Equal(2, fixture.Audit.Entries.Count);
    }

    [Fact]
    public async Task PurgeUsers_MoreThan50_Refused()
    {
        await using var fixture = await Fixture.CreateAsync();

        var text = await fixture.Tools.PurgeUsersAsync([.. Enumerable.Range(1, 51).Select(i => $"u{i}")]);

        Assert.Contains("50", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgeRoles_SoftDeletedRole_Removed()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync("暫時角色");
        await fixture.Tools.DeleteRolesAsync(["暫時角色"]);

        var json = await fixture.Tools.PurgeRolesAsync(["暫時角色", "一般"]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.False(await fixture.Context.RoleView.IgnoreQueryFilters().AnyAsync(x => x.Name == "暫時角色"));
        Assert.True(await fixture.Context.RoleView.AnyAsync(x => x.Name == "一般"));
        Assert.True(await fixture.Context.AuditLog.AnyAsync(x => x.Action == "Role.Purge"));
    }

    [Fact]
    public async Task ResetTwoFactor_EnabledUser_ResetAndAuditedByService()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.AddUserAsync("temp-a");
        await fixture.Context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorEnabled, true));
        var stampBefore = (await fixture.UserAsync("temp-a")).SecurityStamp;

        var json = await fixture.Tools.ResetTwoFactorAsync(["temp-a"]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        var user = await fixture.UserAsync("temp-a");
        Assert.False(user.TwoFactorEnabled);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
        Assert.True(await fixture.Context.AuditLog.AnyAsync(x => x.Action == "User.TwoFactorReset"));
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task ResetTwoFactor_SelfOrNotEnabled_NotExecuted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var selfId = await fixture.AddUserAsync("me", isAdmin: true);
        await fixture.Context.MyUser.Where(x => x.Id == selfId).ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorEnabled, true));
        fixture.Current.CurrentUser.Id = selfId;
        await fixture.AddUserAsync("temp-a");

        var json = await fixture.Tools.ResetTwoFactorAsync(["me", "temp-a"]);

        Assert.Contains("\"failed\":2", json, StringComparison.Ordinal);
        Assert.Contains("自己", json, StringComparison.Ordinal);
        Assert.Contains("沒有啟用", json, StringComparison.Ordinal);
        Assert.True((await fixture.UserAsync("me")).TwoFactorEnabled);
        Assert.False(await fixture.Context.AuditLog.AnyAsync(x => x.Action == "User.TwoFactorReset"));
    }

    [Fact]
    public async Task ClearAuditLogs_RemovesAll_ThenWritesClearAllAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("temp-a");
        await fixture.Tools.DeleteUsersAsync(["temp-a"]);
        Assert.True(await fixture.Context.AuditLog.AnyAsync());

        var text = await fixture.Tools.ClearAuditLogsAsync();

        Assert.Contains("已清空", text, StringComparison.Ordinal);
        Assert.False(await fixture.Context.AuditLog.AnyAsync());
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal("Audit.ClearAll", entry.Action);
        Assert.StartsWith("清空全部稽核紀錄：已清空", entry.Detail, StringComparison.Ordinal);
        Assert.EndsWith("; via=admin-assistant", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void IrreversibleTools_FlaggedWithNotes()
    {
        Assert.Equal(4, AccountsAssistantTools.IrreversibleTools.Count);
        Assert.All(AccountsAssistantTools.IrreversibleTools, tool =>
        {
            Assert.True(tool.IsMutating && tool.IsIrreversible, tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.ApprovalNote), tool.Name);
        });
    }
}
