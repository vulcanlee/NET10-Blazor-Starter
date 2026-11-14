using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Business.Startup;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 軟刪除（0.9.95 起：使用者、角色）。
///
/// 這兩張表牽涉登入與權限，釘住的重點是「已刪除的東西真的失效、又不會因此把別人鎖在門外」：
/// 已刪除的角色不再給權限（關聯刻意保留，所以權限判斷必須經 RoleView 過濾）、仍是主要角色的角色不能刪、
/// support 與自己不能刪、已刪除的使用者無法登入、Google 登入不會替已刪除的人建出重複帳號。
/// </summary>
public sealed class SoftDeleteUserRoleTests
{
    // ------------------------------------------------------------------ 已刪除的角色不再給權限

    [Fact]
    public async Task DeletedAdditionalRole_ShouldStopGrantingPermissions_AndRestoreBringsThemBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var primary = await fixture.AddRoleAsync("一般", "PermA");
        var extra = await fixture.AddRoleAsync("加值", "PermX");
        var userId = await fixture.AddUserAsync("amy", primary, extra);
        Assert.Contains("PermX", await fixture.PermissionKeysAsync(userId));

        Assert.True((await fixture.RoleService().DeleteAsync(extra)).Success);
        var afterDelete = await fixture.PermissionKeysAsync(userId);
        Assert.Contains("PermA", afterDelete);
        Assert.DoesNotContain("PermX", afterDelete);

        Assert.True((await fixture.RoleService().RestoreAsync(extra)).Success);
        Assert.Contains("PermX", await fixture.PermissionKeysAsync(userId));
    }

    [Fact]
    public async Task DeletedRoleReachedOnlyThroughLegacyRoleViewId_ShouldGrantNothing()
    {
        // 沒有 UserRole、只有 MyUser.RoleViewId（舊資料）也要被過濾；只過濾 UserRole 那條查詢會漏掉這個來源。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("舊角色", "PermLegacy");
        fixture.Context.MyUser.Add(new MyUser { Account = "legacy", Name = "legacy", Password = "x", RoleViewId = role });
        await fixture.Context.SaveChangesAsync();
        var userId = await fixture.Context.MyUser.Where(x => x.Account == "legacy").Select(x => x.Id).SingleAsync();
        Assert.Contains("PermLegacy", await fixture.PermissionKeysAsync(userId));

        await fixture.Context.RoleView.Where(x => x.Id == role).ExecuteUpdateAsync(x => x.SetProperty(r => r.IsDeleted, true));

        Assert.Empty(await fixture.PermissionKeysAsync(userId));
    }

    // ------------------------------------------------------------------ 刪除角色的限制

    [Fact]
    public async Task RoleDelete_WhileActiveOrDisabledUsersUseItAsPrimary_ShouldBeRejectedWithTheirAccounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("業務", "PermS");
        var amy = await fixture.AddUserAsync("amy", role);
        var ben = await fixture.AddUserAsync("ben", role, status: false);
        var stampBefore = await fixture.Context.RoleView.Where(x => x.Id == role).Select(x => x.ConcurrencyStamp).SingleAsync();

        var rejected = await fixture.RoleService().DeleteAsync(role);

        Assert.False(rejected.Success);
        Assert.Contains("2 位", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("amy", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("ben", rejected.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Id == role).Select(x => x.IsDeleted).SingleAsync());

        // 事前就要擋下、不可以「先寫入再靠存檔後複查還原」：那樣版本號會被換掉，正在編輯這個角色的人存檔時會被誤判為衝突。
        Assert.Equal(stampBefore, await fixture.Context.RoleView.Where(x => x.Id == role).Select(x => x.ConcurrencyStamp).SingleAsync());

        // 只剩已刪除的使用者以它為主要角色時就可以刪（他們不會再登入，不會被登出迴圈卡住）。
        Assert.True((await fixture.UserService().DeleteAsync(amy)).Success);
        Assert.True((await fixture.UserService().DeleteAsync(ben)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(role)).Success);
    }

    [Fact]
    public async Task RoleDelete_WhenSomeoneIsAssignedItMeanwhile_ShouldRollBack()
    {
        // 「刪除角色的檢查」與「使用者存檔時驗證角色」各在不同的 DbContext。模擬刪除剛存檔、
        // 另一個人恰好把某位使用者設成這個主要角色：存檔後的複查必須把角色還原並擋下。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("業務", "PermS");
        var other = await fixture.AddRoleAsync("其他", "PermO");
        var amy = await fixture.AddUserAsync("amy", other);
        var interceptor = new AssignPrimaryRoleAfterSaveInterceptor(amy, role);

        var result = await fixture.RoleService(interceptor).DeleteAsync(role);

        Assert.True(interceptor.Fired, "攔截器沒有觸發，這個測試沒有測到競態。");
        Assert.False(result.Success);
        Assert.Contains("amy", result.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).Where(x => x.Id == role).Select(x => x.IsDeleted).SingleAsync());
    }

    [Fact]
    public async Task DefaultRole_ShouldNotBeDeletable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");

        var result = await fixture.RoleService().DeleteAsync(role);

        Assert.False(result.Success);
        Assert.Contains(MagicObjectHelper.預設角色, result.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 帳號與角色名稱唯一性（0.9.171 起）

    /// <summary>帳號去頭尾空白、不分大小寫不能重複；存的是去掉空白後的值；修改自己不會撞到自己。</summary>
    [Fact]
    public async Task Account_ShouldBeTrimmed_AndUniqueCaseInsensitively()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var users = fixture.UserService();
        await fixture.AddUserAsync("admin", role);

        Assert.False((await users.BeforeAddCheckAsync(NewUser(" ADMIN ", role))).Success);
        Assert.True((await users.BeforeAddCheckAsync(NewUser(" Tester ", role))).Success);
        Assert.True((await users.AddAsync(NewUser(" Tester ", role))).Success);
        var tester = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Name == "Tester");
        Assert.Equal("Tester", tester.Account);

        var renamed = NewUser("Admin", role);
        renamed.Id = tester.Id;
        Assert.False((await users.BeforeUpdateCheckAsync(renamed)).Success);
        var self = NewUser(" tester ", role);
        self.Id = tester.Id;
        Assert.True((await users.BeforeUpdateCheckAsync(self)).Success);
    }

    [Fact]
    public async Task RoleName_ShouldBeTrimmed_AndUniqueCaseInsensitively()
    {
        await using var fixture = await Fixture.CreateAsync();
        var roles = fixture.RoleService();
        var manager = await fixture.AddRoleAsync("Manager", "PermA");

        Assert.False((await roles.BeforeAddCheckAsync(new RoleViewAdapterModel { Name = " manager " })).Success);
        Assert.False((await roles.BeforeUpdateCheckAsync(new RoleViewAdapterModel { Id = await fixture.AddRoleAsync("Viewer", "PermB"), Name = "MANAGER" })).Success);
        Assert.True((await roles.BeforeUpdateCheckAsync(new RoleViewAdapterModel { Id = manager, Name = " manager " })).Success);

        Assert.True((await roles.DeleteAsync(manager)).Success);
        await fixture.AddRoleAsync("MANAGER", "PermC");
        var restore = await roles.RestoreAsync(manager);
        Assert.False(restore.Success);
        Assert.Contains("同名", restore.Message, StringComparison.Ordinal);
    }

    /// <summary>登入不分大小寫、去頭尾空白（0.9.171 起）；輸入「ADMIN」也登得進 admin。</summary>
    [Fact]
    public async Task Login_ShouldMatchAccountCaseInsensitively()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        await fixture.AddUserAsync("admin", role);
        var hash = SecurePasswordHasher.HashPassword("pass-word-123");
        await fixture.Context.MyUser.Where(x => x.Account == "admin")
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.Password, hash).SetProperty(u => u.Salt, (string?)null));

        var upper = await fixture.LoginService().LoginAsync("ADMIN", "pass-word-123");
        var spaced = await fixture.LoginService().LoginAsync(" admin ", "pass-word-123");
        var wrong = await fixture.LoginService().LoginAsync("ADMIN", "wrong");

        Assert.Equal("admin", upper.User?.Account);
        Assert.Equal("admin", spaced.User?.Account);
        Assert.Null(wrong.User);
    }

    /// <summary>
    /// Google 登入要建立新帳號時，帳號（email）已被別的使用者用（大小寫不同也算）：拒絕，不建立新使用者、不自動合併。
    /// </summary>
    [Fact]
    public async Task ExternalLogin_WhenAccountIsTakenByAnotherUser_ShouldRefuseWithoutCreating()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermA");
        await fixture.AddUserAsync("Pat@Example.com", role, email: "someone-else@example.com");
        var before = await fixture.Context.MyUser.CountAsync();

        var result = await fixture.ExternalLogin("sub-pat", "pat@example.com");

        Assert.Equal(ExternalLoginOutcome.AccountTaken, result.Evaluate(DateTime.UtcNow));
        Assert.Equal(before, await fixture.Context.MyUser.CountAsync());
    }

    /// <summary>
    /// 審查發現：SQLite 的 lower() 只折疊 A-Z，C# 的 ToLower() 會折疊所有字母。兩邊要用同一套（都在 SQL 端），
    /// 否則「Émile」這類帳號連大小寫完全照打都登不進去（升級前是精確比對，登得進去）。
    /// </summary>
    [Fact]
    public async Task NonAsciiAccount_ShouldStillLogIn_AndBeCheckedForDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        await fixture.AddUserAsync("Émile", role);
        var hash = SecurePasswordHasher.HashPassword("pass-word-123");
        await fixture.Context.MyUser.Where(x => x.Account == "Émile")
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.Password, hash).SetProperty(u => u.Salt, (string?)null));

        var login = await fixture.LoginService().LoginAsync("Émile", "pass-word-123");

        Assert.Equal("Émile", login.User?.Account);
        Assert.False((await fixture.UserService().BeforeAddCheckAsync(NewUser("Émile", role))).Success);
    }

    /// <summary>
    /// 審查發現：support 種子不分大小寫比對時，可能找到另一位只差大小寫的「一般」使用者並把他提升成管理員、改掉他的密碼。
    /// 這種情況要拒絕並讓啟動停下來；大小寫不同但本來就是管理員（設定檔改了大小寫）則視為同一個 support。
    /// </summary>
    [Fact]
    public async Task SupportSeeder_ShouldNotPromoteANonAdminWhoseAccountOnlyDiffersInCase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermA");
        await fixture.AddUserAsync("Support", role);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SeedSupportAsync("support"));

        var user = await fixture.Context.MyUser.AsNoTracking().SingleAsync();
        Assert.False(user.IsAdmin);
        Assert.Equal("x", user.Password);
    }

    [Fact]
    public async Task SupportSeeder_ShouldReuseAnAdminWhoseAccountOnlyDiffersInCase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermA");
        var id = await fixture.AddUserAsync("Support", role);
        await fixture.Context.MyUser.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsAdmin, true));

        await fixture.SeedSupportAsync("support");

        Assert.Equal(1, await fixture.Context.MyUser.CountAsync());
    }

    /// <summary>審查發現：已刪除的使用者帳號名稱就是這個 Email 時也要拒絕，否則新建之後那位已刪除的使用者就還原不了。</summary>
    [Fact]
    public async Task ExternalLogin_WhenADeletedUserHasThatAccount_ShouldAlsoRefuse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermA");
        var pat = await fixture.AddUserAsync("pat@example.com", role, email: "other@example.com");
        Assert.True((await fixture.UserService().DeleteAsync(pat)).Success);

        var result = await fixture.ExternalLogin("sub-pat", "PAT@example.com");

        Assert.Equal(ExternalLoginOutcome.AccountTaken, result.Evaluate(DateTime.UtcNow));
        Assert.Equal(1, await fixture.Context.MyUser.IgnoreQueryFilters().CountAsync());
    }

    private static MyUserAdapterModel NewUser(string account, int roleId) => new()
    {
        Account = account,
        Name = account.Trim(),
        Password = "Pass-word-1234!",
        RoleViewId = roleId,
        Status = true,
    };

    [Fact]
    public async Task RoleRestore_WithSameNameActiveRole_ShouldBeRejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = await fixture.AddRoleAsync("稽核", "PermA");
        Assert.True((await fixture.RoleService().DeleteAsync(old)).Success);
        await fixture.AddRoleAsync("稽核", "PermB");

        var result = await fixture.RoleService().RestoreAsync(old);

        Assert.False(result.Success);
        Assert.Contains("同名", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RolePurge_WhenADeletedUserStillHasItAsPrimary_ShouldBeRejectedWithAClearMessage()
    {
        // 主要角色是 Restrict 外鍵，連已刪除的使用者都算；用有過濾的集合計算會讓資料庫丟出籠統的錯誤。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("臨時", "PermT");
        var amy = await fixture.AddUserAsync("amy", role);
        Assert.True((await fixture.UserService().DeleteAsync(amy)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(role)).Success);

        var rejected = await fixture.RoleService().PurgeAsync(role);

        Assert.False(rejected.Success);
        Assert.Contains("amy（已刪除）", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync(x => x.Id == role));

        Assert.True((await fixture.UserService().PurgeAsync(amy)).Success);
        Assert.True((await fixture.RoleService().PurgeAsync(role)).Success);
        Assert.Equal(0, await fixture.Context.RoleView.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync(x => x.Id == role));
    }

    [Fact]
    public async Task RoleEditedThroughClone_ShouldSave()
    {
        // 0.9.93～0.9.94 的缺陷：RoleViewAdapterModel.Clone() 漏了版本號，從畫面修改任何角色都被當成並行衝突。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("編輯測試", "PermE");
        var service = fixture.RoleService();
        var editing = (await service.GetAsync(role)).Clone();
        editing.Name = "編輯測試（改名）";

        var result = await service.UpdateAsync(editing);

        Assert.True(result.Success, result.Message);
        Assert.Equal("編輯測試（改名）", await fixture.Context.RoleView.Where(x => x.Id == role).Select(x => x.Name).SingleAsync());
    }

    // ------------------------------------------------------------------ 刪除使用者的限制

    [Fact]
    public async Task SupportAccount_ShouldNotBeDeletable_EvenWhenRenamedOrDifferentCase()
    {
        await using var fixture = await Fixture.CreateAsync(supportAccount: "ops");
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var ops = await fixture.AddUserAsync("OPS", role);
        var service = fixture.UserService();

        var result = await service.DeleteAsync(ops);

        Assert.False(result.Success);
        Assert.Contains("OPS", result.Message, StringComparison.Ordinal);
        Assert.False(service.CanDelete(new MyUserAdapterModel { Id = ops, Account = "OPS" }));
    }

    [Fact]
    public async Task CurrentUser_ShouldNotDeleteThemselves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var me = await fixture.AddUserAsync("me", role);
        fixture.SignInAs(me, "me");

        var result = await fixture.UserService().DeleteAsync(me);

        Assert.False(result.Success);
        Assert.Contains("自己", result.Message, StringComparison.Ordinal);

        // 沒有登入者（背景作業，Id = 0）時不套用「不可刪除自己」。
        fixture.SignInAs(0, string.Empty);
        Assert.True((await fixture.UserService().DeleteAsync(me)).Success);
    }

    // ------------------------------------------------------------------ 使用者與已刪除角色

    [Fact]
    public async Task EditingUser_ShouldKeepLinkToDeletedExtraRole_SoRestoringTheRoleRestoresIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var primary = await fixture.AddRoleAsync("一般", "PermA");
        var extra = await fixture.AddRoleAsync("加值", "PermX");
        var userId = await fixture.AddUserAsync("amy", primary, extra);
        var users = fixture.UserService();
        Assert.True((await fixture.RoleService().DeleteAsync(extra)).Success);

        var (additional, teams) = await users.GetUserAssignmentsAsync(userId);
        Assert.DoesNotContain(extra, additional);

        var stamp = await fixture.Context.MyUser.Where(x => x.Id == userId).Select(x => x.ConcurrencyStamp).SingleAsync();
        var save = await users.UpdateAsync(new MyUserAdapterModel
        {
            Id = userId,
            Account = "amy",
            Name = "Amy",
            Status = true,
            RoleViewId = primary,
            AdditionalRoleIds = additional,
            TeamNames = teams,
            ConcurrencyStamp = stamp,
        });
        Assert.True(save.Success, save.Message);
        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == userId && x.RoleViewId == extra));

        Assert.True((await fixture.RoleService().RestoreAsync(extra)).Success);
        Assert.Contains("PermX", await fixture.PermissionKeysAsync(userId));
    }

    [Fact]
    public async Task SavingUser_WithDeletedRole_ShouldBeRejected_ButNoPrimaryRoleIsAllowed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var active = await fixture.AddRoleAsync("一般", "PermA");
        var deleted = await fixture.AddRoleAsync("停用中", "PermD");
        Assert.True((await fixture.RoleService().DeleteAsync(deleted)).Success);
        var users = fixture.UserService();

        var deletedPrimary = await users.AddAsync(new MyUserAdapterModel { Account = "p", Name = "p", Password = "Passw0rd", RoleViewId = deleted });
        var deletedExtra = await users.AddAsync(new MyUserAdapterModel { Account = "e", Name = "e", Password = "Passw0rd", RoleViewId = active, AdditionalRoleIds = [deleted] });
        var noRole = await users.AddAsync(new MyUserAdapterModel { Account = "n", Name = "n", Password = "Passw0rd", RoleViewId = null });

        Assert.False(deletedPrimary.Success);
        Assert.Contains("已被刪除", deletedPrimary.Message, StringComparison.Ordinal);
        Assert.False(deletedExtra.Success);
        Assert.True(noRole.Success, noRole.Message);
    }

    // ------------------------------------------------------------------ 還原與永久刪除使用者

    [Fact]
    public async Task UserRestore_ShouldCheckAccountGoogleIdAndPrimaryRole()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var users = fixture.UserService();

        // 帳號衝突（0.9.171 起不分大小寫：大小寫不同也算衝突，與新增時的檢查一致）
        var alice = await fixture.AddUserAsync("alice", role);
        Assert.True((await users.DeleteAsync(alice)).Success);
        var newAlice = await fixture.AddUserAsync("Alice", role);
        var accountConflict = await users.RestoreAsync(alice);
        Assert.False(accountConflict.Success);
        Assert.Contains("alice", accountConflict.Message, StringComparison.OrdinalIgnoreCase);
        await fixture.Context.MyUser.Where(x => x.Id == newAlice).ExecuteUpdateAsync(x => x.SetProperty(u => u.Account, "alice2"));
        Assert.True((await users.RestoreAsync(alice)).Success);

        // GoogleId 衝突
        var g1 = await fixture.AddUserAsync("g1", role, googleId: "sub-1");
        Assert.True((await users.DeleteAsync(g1)).Success);
        await fixture.AddUserAsync("g2", role, googleId: "sub-1");
        var googleConflict = await users.RestoreAsync(g1);
        Assert.False(googleConflict.Success);
        Assert.Contains("g2", googleConflict.Message, StringComparison.Ordinal);

        // 主要角色已刪除 → 擋下；沒有主要角色 → 允許
        var temp = await fixture.AddRoleAsync("臨時", "PermT");
        var tom = await fixture.AddUserAsync("tom", temp);
        Assert.True((await users.DeleteAsync(tom)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(temp)).Success);
        var roleDeleted = await users.RestoreAsync(tom);
        Assert.False(roleDeleted.Success);
        Assert.Contains("臨時", roleDeleted.Message, StringComparison.Ordinal);

        var nobody = await fixture.AddUserAsync("nobody", roleId: null);
        Assert.True((await users.DeleteAsync(nobody)).Success);
        Assert.True((await users.RestoreAsync(nobody)).Success);
    }

    [Fact]
    public async Task UserPurge_ShouldOnlyWorkOnDeletedUsers_AndRemoveTheirLinks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var team = new Team { Name = "研發部" };
        fixture.Context.Team.Add(team);
        await fixture.Context.SaveChangesAsync();
        var userId = await fixture.AddUserAsync("amy", role);
        fixture.Context.UserTeam.Add(new UserTeam { MyUserId = userId, TeamId = team.Id });
        await fixture.Context.SaveChangesAsync();
        var users = fixture.UserService();

        Assert.False((await users.PurgeAsync(userId)).Success, "未刪除的使用者不可直接永久刪除。");

        Assert.True((await users.DeleteAsync(userId)).Success);
        Assert.Equal(1, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == userId));
        Assert.True((await users.PurgeAsync(userId)).Success);

        Assert.Equal(0, await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync(x => x.Id == userId));
        Assert.Equal(0, await fixture.Context.UserRole.CountAsync(x => x.MyUserId == userId));
        Assert.Equal(0, await fixture.Context.UserTeam.CountAsync(x => x.MyUserId == userId));
    }

    [Fact]
    public async Task DeletedUsersList_ShouldShowTheirDeletedPrimaryRoleName()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("臨時", "PermT");
        var amy = await fixture.AddUserAsync("amy", role);
        Assert.True((await fixture.UserService().DeleteAsync(amy)).Success);
        Assert.True((await fixture.RoleService().DeleteAsync(role)).Success);

        var deleted = (await fixture.UserService().GetDeletedAsync(AllRows())).Result.Single();

        Assert.Equal("amy", deleted.Account);
        Assert.Equal("臨時", deleted.RoleViewName);
        Assert.Equal(string.Empty, deleted.Password);
        Assert.Equal("alice", deleted.DeletedBy);
    }

    // ------------------------------------------------------------------ 已刪除的使用者無法登入

    [Fact]
    public async Task DeletedUser_ShouldNotLogIn_RefreshOrKeepPasswordResetTokens()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync("一般", "PermA");
        var users = fixture.UserService();
        Assert.True((await users.AddAsync(new MyUserAdapterModel { Account = "amy", Name = "amy", Password = "pw-123456", Status = true, RoleViewId = role })).Success);
        var userId = await fixture.Context.MyUser.Where(x => x.Account == "amy").Select(x => x.Id).SingleAsync();
        fixture.Context.PasswordResetToken.Add(new PasswordResetToken { MyUserId = userId, TokenHash = "hash", CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) });
        await fixture.Context.SaveChangesAsync();
        Assert.NotNull((await fixture.LoginService().LoginAsync("amy", "pw-123456")).User);

        Assert.True((await users.DeleteAsync(userId)).Success);

        var (message, user) = await fixture.LoginService().LoginAsync("amy", "pw-123456");
        Assert.Null(user);
        Assert.Equal(MyUserServiceLogin.InvalidCredentialsMessage, message);
        Assert.Null(await fixture.LoginService().GetActiveUserAsync(userId));
        Assert.Equal(0, await fixture.Context.PasswordResetToken.CountAsync(x => x.MyUserId == userId));
        Assert.Equal(0, (await users.GetAsync(userId)).Id);
    }

    // ------------------------------------------------------------------ Google 登入

    [Fact]
    public async Task GoogleLogin_WithDeletedUsersGoogleId_ShouldRefuseWithoutCreatingAnAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");
        var deleted = await fixture.AddUserAsync("gina", role, googleId: "sub-9", email: "gina@example.com");
        Assert.True((await fixture.UserService().DeleteAsync(deleted)).Success);

        var result = await fixture.ExternalLogin("sub-9", "other@example.com");

        Assert.True(result.IsDeleted);
        Assert.Equal(deleted, result.User.Id);
        Assert.Equal(1, await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
    }

    [Fact]
    public async Task GoogleLogin_WithDeletedUsersEmail_ShouldRefuse_IgnoringCase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");
        var deleted = await fixture.AddUserAsync("gina", role, email: "Gina@Example.com");
        Assert.True((await fixture.UserService().DeleteAsync(deleted)).Success);

        var result = await fixture.ExternalLogin("sub-new", "gina@example.com");

        Assert.True(result.IsDeleted);
        Assert.Equal(1, await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).CountAsync());
        Assert.Null(await fixture.Context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).Select(x => x.GoogleId).SingleAsync());
    }

    [Fact]
    public async Task GoogleLogin_DeletedGoogleIdShouldWinOverActiveEmail()
    {
        // 使用者決定的順序：GoogleId 優先。否則已刪除者的 Google 帳號會被連結到同 Email 的另一人，之後他就無法還原。
        await using var fixture = await Fixture.CreateAsync();
        var role = await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");
        var deleted = await fixture.AddUserAsync("gina", role, googleId: "sub-9");
        Assert.True((await fixture.UserService().DeleteAsync(deleted)).Success);
        var active = await fixture.AddUserAsync("eve", role, email: "shared@example.com");

        var result = await fixture.ExternalLogin("sub-9", "shared@example.com");

        Assert.True(result.IsDeleted);
        Assert.Equal(deleted, result.User.Id);
        Assert.Null(await fixture.Context.MyUser.Where(x => x.Id == active).Select(x => x.GoogleId).SingleAsync());
    }

    [Fact]
    public async Task GoogleLogin_WithNoMatch_ShouldStillCreateADisabledAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddRoleAsync(MagicObjectHelper.預設角色, "PermD");

        var result = await fixture.ExternalLogin("sub-1", "new@example.com");

        Assert.False(result.IsDeleted);
        Assert.False(result.User.Status);
        Assert.Equal("new@example.com", result.User.Account);
    }

    // ------------------------------------------------------------------ 模型與慣例

    [Fact]
    public void SoftDeletableEntities_ShouldCarryTheNamedFilter()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);

        var softDeletable = context.Model.GetEntityTypes()
            .Where(x => typeof(ISoftDeletable).IsAssignableFrom(x.ClrType))
            .ToList();

        Assert.Equal(
            new[] { nameof(Category), nameof(MyUser), nameof(Project), nameof(RoleView), nameof(Team) },
            softDeletable.Select(x => x.ClrType.Name).Order().ToArray());
        Assert.All(softDeletable, entityType =>
            Assert.Contains(entityType.GetDeclaredQueryFilters(), f => f.Key == ISoftDeletable.FilterName));
    }

    [Fact]
    public void EveryConcurrencyStampApply_ShouldBeFollowedByProtectFlags()
    {
        // 整筆覆蓋的存檔會把 IsDeleted 寫回 false。行為測試摸不到這個競態（存檔前的查詢已擋掉大部分情況），
        // 所以用原始碼守門：呼叫 ConcurrencyStampHelper.Apply 的檔案，Apply 與 ProtectFlags 的次數要一樣。
        var root = FindSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains("MyProject.Tests", StringComparison.Ordinal))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(f => CountOf(f.Text, "ConcurrencyStampHelper.Apply(") != CountOf(f.Text, "SoftDeleteHelper.ProtectFlags("))
            .Select(f => Path.GetRelativePath(root, f.Path))
            .ToList();

        Assert.True(offenders.Count == 0, $"這些檔案呼叫 ConcurrencyStampHelper.Apply 但沒有對應的 ProtectFlags：{string.Join("、", offenders)}");
    }

    private static int CountOf(string text, string token)
    {
        var count = 0;
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FindSourceRoot() => SourceTree.SourceRoot;

    private static DataRequest AllRows() => new() { CurrentPage = 1, PageSize = 100, Take = 0 };

    /// <summary>角色剛被標記為已刪除並存檔後，模擬另一個人把某位使用者設成這個主要角色（只觸發一次）。</summary>
    private sealed class AssignPrimaryRoleAfterSaveInterceptor(int userId, int roleId) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            var roleJustDeleted = context.ChangeTracker.Entries<RoleView>().Any(e => e.Entity.Id == roleId && e.Entity.IsDeleted);
            if (!Fired && roleJustDeleted)
            {
                Fired = true;
                await context.Database.ExecuteSqlRawAsync("UPDATE MyUser SET RoleViewId = {0} WHERE Id = {1}", [roleId, userId], cancellationToken);
            }

            return result;
        }
    }

    private sealed class InterceptingFactory(SqliteConnection connection, IInterceptor interceptor) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
            => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly CurrentUserService currentUser = new();
        private readonly BootstrapSettings bootstrapSettings;

        private Fixture(SqliteConnection connection, BackendDBContext context, string supportAccount)
        {
            this.connection = connection;
            Context = context;
            bootstrapSettings = new BootstrapSettings { SupportAccount = supportAccount };

            // Id 刻意設很大，避免和測試建立的使用者撞號而觸發「不可刪除自己」。
            currentUser.CurrentUser = new CurrentUser { Id = 999_999, Account = "alice" };
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync(string supportAccount = "support")
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context, supportAccount);
        }

        public void SignInAs(int id, string account) => currentUser.CurrentUser = new CurrentUser { Id = id, Account = account };

        public MyUserService UserService()
            => new(new TestDbContextFactory(connection), NullLogger<MyUserService>.Instance,
                new RbacWriteService(NewContext(), NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser,
                Options.Create(bootstrapSettings), PasswordTestDefaults.Policy(supportAccount: bootstrapSettings.SupportAccount));

        public RoleViewService RoleService(IInterceptor? interceptor = null)
            => new(interceptor is null ? new TestDbContextFactory(connection) : new InterceptingFactory(connection, interceptor), NullLogger<RoleViewService>.Instance, new RolePermissionService(),
                new RbacWriteService(NewContext(), NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser);

        public MyUserServiceLogin LoginService()
            => new(NewContext(), new ConfigurationBuilder().Build(), NullLogger<MyUserServiceLogin>.Instance,
                new RolePermissionService(), new RecordingAuditLogService(), PasswordTestDefaults.Lockout(), new RecordingNotificationSender(), TimeProvider.System,
                TwoFactorTestDefaults.Service(new TestDbContextFactory(connection), supportAccount: bootstrapSettings.SupportAccount));

        public async Task SeedSupportAsync(string supportAccount)
        {
            await using var context = NewContext();
            await new SupportUserSeeder(
                    context,
                    Options.Create(new BootstrapSettings { SupportAccount = supportAccount, SupportPassword = "Seed-Pass-1234!", SupportName = "support" }),
                    NullLogger<SupportUserSeeder>.Instance)
                .SeedAsync(CancellationToken.None);
        }

        public Task<ExternalLoginResult> ExternalLogin(string subject, string email)
            => new ExternalLoginService(NewContext(), NullLogger<ExternalLoginService>.Instance, new RecordingAuditLogService(), new RecordingNotificationSender())
                .FindOrCreateAsync("Google", subject, email, "Google User", MagicObjectHelper.預設角色);

        public async Task<IReadOnlyCollection<string>> PermissionKeysAsync(int userId)
        {
            await using var context = NewContext();
            return await new PermissionChecker(context, NullLogger<PermissionChecker>.Instance).GetEffectivePermissionKeysAsync(userId);
        }

        /// <summary>建立角色並寫入它的權限對應（與角色管理存檔相同的雙寫）。</summary>
        public async Task<int> AddRoleAsync(string name, string permissionKey)
        {
            var role = new RoleView { Name = name, TabViewJson = $"""["{permissionKey}"]""" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            await new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance).SyncRolePermissionsAsync(role.Id, [permissionKey]);
            Context.ChangeTracker.Clear();
            return role.Id;
        }

        /// <summary>直接寫入使用者與 UserRole（主要角色 + 額外角色），略過畫面驗證。</summary>
        public async Task<int> AddUserAsync(string account, int? roleId, int? extraRoleId = null, bool status = true, string? googleId = null, string? email = null)
        {
            var user = new MyUser { Account = account, Name = account, Password = "x", Status = status, RoleViewId = roleId, GoogleId = googleId, Email = email };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            var roleIds = new[] { roleId, extraRoleId }.Where(x => x.HasValue).Select(x => x!.Value).ToList();
            if (roleIds.Count > 0)
            {
                await new RbacWriteService(Context, NullLogger<RbacWriteService>.Instance).SyncUserRolesAsync(user.Id, roleIds);
            }

            Context.ChangeTracker.Clear();
            return user.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }

        private BackendDBContext NewContext() => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
    }
}
