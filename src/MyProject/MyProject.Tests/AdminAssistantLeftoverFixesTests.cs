using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas.Models;
using MyProject.Models.AdapterModel;
using MyProject.Web.Ai.Agents.AdminAssistant;
using Fixture = MyProject.Tests.AccountsAssistantToolsTests.DbFixture;

namespace MyProject.Tests;

/// <summary>
/// 里程碑 2～3 審查留下、0.9.159 一起處理的小問題：縮短保留天數判斷保守化、還原時找得到精確名稱、
/// 刪除角色去重、修改帳號保留其他欄位、Audit.View 的 detail 與頁面一致、未開 2FA 的管理員只算會用密碼登入的啟用帳號。
/// </summary>
public sealed class AdminAssistantLeftoverFixesTests
{
    [Theory]
    [InlineData("365", "30", true)]
    [InlineData("365", "400", false)]
    [InlineData("0", "30", true)]
    [InlineData("365", "0", false)]
    [InlineData(null, "30", true)]
    [InlineData("365", "abc", true)]
    public void WouldShortenRetention_UnparsableCountsAsShortening(string? current, string? next, bool expected)
        => Assert.Equal(expected, OpsAssistantTools.WouldShortenRetention(current, next));

    /// <summary>縮短保留天數的工具只接受「確實會縮短」：任一邊無法解析時不能當成縮短（那是可復原工具的保守規則）。</summary>
    [Theory]
    [InlineData("365", "30", true)]
    [InlineData("365", "400", false)]
    [InlineData(null, "30", false)]
    [InlineData("365", "abc", false)]
    public void DefinitelyShortensRetention_RequiresBothParsable(string? current, string? next, bool expected)
        => Assert.Equal(expected, OpsAssistantTools.DefinitelyShortensRetention(current, next));

    /// <summary>自訂清除中途失敗、但已經刪了資料：同資料保留頁仍要寫稽核（審查發現 0.9.159）；一筆都沒刪就不寫。</summary>
    [Fact]
    public void CustomPurgeAuditDetail_WrittenWhenAnythingRemoved_EvenIfFailed()
    {
        var cutoff = new MyProject.Web.DataRetention.CustomPurgeCutoff(30, null);

        var partial = OpsAssistantTools.CustomPurgeAuditDetail(new MyProject.Web.DataRetention.CustomPurgeOutcome(12, 3, "User.Purge", "MyUser", Failed: true), cutoff);
        var none = OpsAssistantTools.CustomPurgeAuditDetail(new MyProject.Web.DataRetention.CustomPurgeOutcome(0, 5, "User.Purge", "MyUser", Failed: true), cutoff);

        Assert.Equal("source=data-retention; mode=keepDays; value=30; rows=12; failed=true; via=admin-assistant", partial);
        Assert.Null(none);
    }

    [Fact]
    public async Task RestoreUsers_ExactNameBeyondFirstFiftyMatches_Found()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddUserAsync("u");
        await fixture.Tools.DeleteUsersAsync(["u"]);
        var others = Enumerable.Range(1, 55).Select(i => $"u-{i}").ToArray();
        foreach (var account in others)
        {
            await fixture.AddUserAsync(account);
        }

        await fixture.Tools.DeleteUsersAsync(others[..50]);
        await fixture.Tools.DeleteUsersAsync(others[50..]);

        var json = await fixture.Tools.RestoreUsersAsync(["u"]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.False((await fixture.UserAsync("u")).IsDeleted);
    }

    [Fact]
    public async Task DeleteRoles_SameRoleListedTwiceByCase_ProcessedOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync("Ops");

        var json = await fixture.Tools.DeleteRolesAsync(["Ops", "OPS"]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetUserRoles_KeepsEmailMustChangePasswordAndLockout()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync("角色2");
        var id = await fixture.AddUserAsync("temp-a");
        var lockout = DateTime.UtcNow.AddHours(1);
        await fixture.Context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Email, "a@example.com")
            .SetProperty(x => x.MustChangePassword, true)
            .SetProperty(x => x.LockoutEndUtc, lockout));

        await fixture.Tools.SetUserRolesAsync("temp-a", primaryRole: null, additionalRoles: ["角色2"]);
        await fixture.Tools.SetUserAdminAsync("temp-a", isAdmin: true);

        var user = await fixture.UserAsync("temp-a");
        Assert.True(user.IsAdmin);
        Assert.Equal("a@example.com", user.Email);
        Assert.True(user.MustChangePassword);
        Assert.NotNull(user.LockoutEndUtc);
    }

    [Fact]
    public async Task GetAuditLogContent_NoContentFile_ReportsExistsAndAuditsActionLikeThePage()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.AuditLog.Add(new AuditLog { Action = "User.Disable", OccurredAt = DateTime.UtcNow, Success = true });
        await fixture.Context.SaveChangesAsync();
        var id = await fixture.Context.AuditLog.Select(x => x.Id).SingleAsync();

        var text = await fixture.Tools.GetAuditLogContentAsync(id);
        var missing = await fixture.Tools.GetAuditLogContentAsync(id + 100);

        Assert.Contains("沒有詳細內容", text, StringComparison.Ordinal);
        Assert.DoesNotContain("不存在", text, StringComparison.Ordinal);
        Assert.Contains("不存在", missing, StringComparison.Ordinal);
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal("Audit.View", entry.Action);
        Assert.Equal("action=User.Disable; via=admin-assistant", entry.Detail);
    }

    [Fact]
    public void AdminsWithoutTwoFactor_OnlyEnabledPasswordAdmins()
    {
        List<MyUserAdapterModel> users =
        [
            new() { Id = 1, Account = "active", IsAdmin = true, Status = true },
            new() { Id = 2, Account = "disabled", IsAdmin = true, Status = false },
            new() { Id = 3, Account = "google", IsAdmin = true, Status = true, OAuthProvider = "Google" },
            new() { Id = 4, Account = "secured", IsAdmin = true, Status = true, TwoFactorEnabled = true },
        ];

        var snapshot = AccountsAssistantTools.BuildAccountSecurity(users, new Dictionary<int, DateTime>(), DateTime.Now, DateTime.UtcNow, 90);

        Assert.Equal(["active"], snapshot.AdminsWithoutTwoFactor);
    }
}
