using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>系統管理助理「帳號與權限」的唯讀工具（0.9.157 起）：篩選、不外送密碼與 Salt、安全總覽、稽核比照頁面。</summary>
public sealed class AccountsAssistantToolsTests
{
    private static readonly DateTime NowUtc = new(2026, 11, 6, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NowLocal = new(2026, 11, 6, 10, 0, 0);

    [Theory]
    [InlineData("enabled", new[] { "alice", "admin1", "locked1" })]
    [InlineData("disabled", new[] { "bob" })]
    [InlineData("locked", new[] { "locked1" })]
    [InlineData("any", new[] { "alice", "bob", "admin1", "locked1" })]
    [InlineData(null, new[] { "alice", "bob", "admin1", "locked1" })]
    public void FilterUsers_ByStatus(string? status, string[] expected)
    {
        var (users, note) = AccountsAssistantTools.FilterUsers(SampleUsers(), status, adminOnly: null, NowUtc);

        Assert.Equal(expected, users.Select(user => user.Account).ToArray());
        Assert.Null(note);
    }

    [Fact]
    public void FilterUsers_AdminOnly_AndUnknownStatusExplained()
    {
        var (users, note) = AccountsAssistantTools.FilterUsers(SampleUsers(), "frozen", adminOnly: true, NowUtc);

        Assert.Equal(["admin1"], users.Select(user => user.Account).ToArray());
        Assert.Contains("frozen", note, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeUser_NeverContainsPasswordOrSalt()
    {
        var user = SampleUsers()[0];
        user.Salt = "S4LT-VALUE";
        user.Password = "P@ssw0rd-VALUE";

        var json = AdminAssistantToolText.ToJson(AccountsAssistantTools.DescribeUser(user, lastLoginLocal: NowLocal.AddDays(-1), NowUtc));

        Assert.DoesNotContain("S4LT-VALUE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("P@ssw0rd-VALUE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("salt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePermissionKeys_ReadsJsonList_AndToleratesInvalid()
    {
        Assert.Equal(["專案項目", "專案項目:edit"], AccountsAssistantTools.ParsePermissionKeys("[\"專案項目\",\"專案項目:edit\"]"));
        Assert.Empty(AccountsAssistantTools.ParsePermissionKeys("不是 JSON"));
        Assert.Empty(AccountsAssistantTools.ParsePermissionKeys(null));
    }

    /// <summary>
    /// 安全總覽：被鎖定、管理員未開 2FA、長期未登入（只看啟用中、建立超過 N 天的帳號；沒有紀錄的標示為無紀錄）。
    /// </summary>
    [Fact]
    public void BuildAccountSecurity_LockedAdminsAndInactive()
    {
        var users = SampleUsers();
        var lastLogins = new Dictionary<int, DateTime>
        {
            [1] = NowLocal.AddDays(-3),   // alice：最近登入
            [3] = NowLocal.AddDays(-200), // admin1：200 天前
        };

        var snapshot = AccountsAssistantTools.BuildAccountSecurity(users, lastLogins, NowLocal, NowUtc, inactiveDays: 90);

        Assert.Equal(4, snapshot.TotalCount);
        Assert.Equal(1, snapshot.DisabledCount);
        Assert.Equal(["locked1"], snapshot.LockedAccounts);
        var admin = Assert.Single(snapshot.Admins);
        Assert.Equal(("admin1", false), (admin.Account, admin.TwoFactorEnabled));
        Assert.Equal(["admin1", "locked1"], snapshot.Inactive.Select(item => item.Account).ToArray());
        Assert.Null(snapshot.Inactive.Single(item => item.Account == "locked1").LastLogin);
    }

    [Fact]
    public async Task SearchUsers_FiltersDisabled_AndAuditsLikeThePage()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await fixture.AddUserAsync("alice");
        await fixture.AddUserAsync("bob", status: false);

        var json = await fixture.Tools.SearchUsersAsync(keyword: null, status: "disabled", adminOnly: null);

        Assert.Contains("\"bob\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"alice\"", json, StringComparison.Ordinal);
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(AuditActions.User.Search, entry.Action);
        Assert.Contains("via=admin-assistant", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// 查某一筆資料的異動歷程（0.9.173 起）：targetType／targetId 精確比對（12 不會混到 112）；
    /// 有給目標、沒給起始時間時預設查 366 天（「這筆是誰改的」通常不是最近 24 小時的事）。
    /// </summary>
    [Fact]
    public async Task SearchAuditLogs_ByTarget_IsExact_AndLooksBackAYearByDefault()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Context.AuditLog.AddRange(
            new AuditLog { OccurredAt = DateTime.UtcNow.AddDays(-100), Action = "Category.Update", TargetType = "Category", TargetId = "12", ActorAccount = "alice" },
            new AuditLog { OccurredAt = DateTime.UtcNow.AddHours(-1), Action = "Category.Update", TargetType = "Category", TargetId = "112", ActorAccount = "bob" },
            new AuditLog { OccurredAt = DateTime.UtcNow.AddHours(-1), Action = "Team.Update", TargetType = "Team", TargetId = "12", ActorAccount = "carol" },
            // 查看紀錄比修改新：沒指定 action 時要排除，否則常被打開的資料，真正的修改會被擠出前幾筆。
            new AuditLog { OccurredAt = DateTime.UtcNow.AddMinutes(-5), Action = "Category.View", TargetType = "Category", TargetId = "12", ActorAccount = "erin" });
        await fixture.Context.SaveChangesAsync();

        var json = await fixture.Tools.SearchAuditLogsAsync(targetType: "Category", targetId: "12");

        Assert.Contains("\"alice\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"bob\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"carol\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"erin\"", json, StringComparison.Ordinal);
        var content = Assert.Single(fixture.Audit.Contents).Content!;
        Assert.Contains("targetType: Category", content, StringComparison.Ordinal);
        Assert.Contains("targetId: 12", content, StringComparison.Ordinal);
        // 模型給的目標識別比照稽核頁：資料庫摘要只記有沒有填。
        var detail = Assert.Single(fixture.Audit.Entries).Detail!;
        Assert.Contains("hasTargetId=True", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("targetId=12", detail, StringComparison.Ordinal);

        // 指定 action 時照指定的查（要看誰開過這一筆）。
        var viewed = await fixture.Tools.SearchAuditLogsAsync(action: "Category.View", targetType: "Category", targetId: "12");
        Assert.Contains("\"erin\"", viewed, StringComparison.Ordinal);
    }

    /// <summary>
    /// 稽核記錄範圍的開關（0.9.174 起預設都關）：模型不能把「沒有記錄」當成「沒有發生」。
    /// 查瀏覽類（誰看過）時 note 要說明沒有記錄；修改沒有差異時要說明是開關關閉。
    /// </summary>
    [Fact]
    public async Task AuditTools_WhenSwitchesAreOff_ShouldExplainWhatIsNotRecorded()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var update = new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Category.Update", TargetType = "Category", TargetId = "5" };
        fixture.Context.AuditLog.Add(update);
        await fixture.Context.SaveChangesAsync();

        var viewed = await fixture.Tools.SearchAuditLogsAsync(action: "Category.View", targetType: "Category", targetId: "5");
        var changed = await fixture.Tools.SearchAuditLogsAsync(action: "Category.Update");
        var content = await fixture.Tools.GetAuditLogContentAsync(update.Id);

        Assert.Contains("目前沒有記錄瀏覽類事件", viewed, StringComparison.Ordinal);
        Assert.DoesNotContain("目前沒有記錄瀏覽類事件", changed, StringComparison.Ordinal);
        Assert.Contains("稽核：記錄修改前後差異", content, StringComparison.Ordinal);
        Assert.Contains("目前關閉", content, StringComparison.Ordinal);
    }

    /// <summary>動作代碼是 User.*，目標類型卻是 MyUser：模型猜錯時要提醒，而且猜的文字不進資料庫摘要。</summary>
    [Fact]
    public async Task SearchAuditLogs_UnknownTargetType_ShouldWarnAndNotStoreTheText()
    {
        await using var fixture = await DbFixture.CreateAsync();
        fixture.Context.AuditLog.Add(new AuditLog { OccurredAt = DateTime.UtcNow, Action = "User.Update", TargetType = "MyUser", TargetId = "3" });
        await fixture.Context.SaveChangesAsync();

        var json = await fixture.Tools.SearchAuditLogsAsync(targetType: "User", targetId: "3");

        Assert.Contains("不在系統已知的清單裡", json, StringComparison.Ordinal);
        Assert.Contains("\"totalCount\":0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("targetType=User", fixture.Audit.Entries.Single().Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserDetail_ExactAccountMatch_AndAuditsView()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await fixture.AddUserAsync("alice2");
        var id = await fixture.AddUserAsync("alice");

        var json = await fixture.Tools.GetUserDetailAsync("alice");

        Assert.Contains("\"account\":\"alice\"", json, StringComparison.Ordinal);
        var entry = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(AuditActions.User.View, entry.Action);
        Assert.Contains("account=alice", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("via=admin-assistant", entry.Detail, StringComparison.Ordinal);
        Assert.Equal(id.ToString(), fixture.Audit.Contents[0].TargetId);
    }

    [Fact]
    public async Task GetUserDetail_Unknown_ReturnsMessageWithoutAudit()
    {
        await using var fixture = await DbFixture.CreateAsync();

        var text = await fixture.Tools.GetUserDetailAsync("nobody");

        Assert.Contains("找不到帳號", text, StringComparison.Ordinal);
        Assert.Empty(fixture.Audit.Entries);
    }

    private static List<MyUserAdapterModel> SampleUsers() =>
    [
        new() { Id = 1, Account = "alice", Name = "Alice", Status = true, CreateAt = NowLocal.AddDays(-400) },
        new() { Id = 2, Account = "bob", Name = "Bob", Status = false, CreateAt = NowLocal.AddDays(-400) },
        new() { Id = 3, Account = "admin1", Name = "Admin", Status = true, IsAdmin = true, TwoFactorEnabled = false, CreateAt = NowLocal.AddDays(-400) },
        new() { Id = 4, Account = "locked1", Name = "Locked", Status = true, LockoutEndUtc = NowUtc.AddMinutes(10), CreateAt = NowLocal.AddDays(-400) },
    ];

    /// <summary>
    /// 真的 SQLite：MyUserService、RoleViewService、AuditLogQueryService；工具自己寫的稽核用記錄替身（服務自己寫的稽核進資料庫）。
    /// 服務與工具共用同一個「目前使用者」（<see cref="Current"/>），自我保護才測得到。
    /// </summary>
    internal sealed class DbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly MyUserService users;

        private DbFixture(SqliteConnection connection, BackendDBContext context, int roleId)
        {
            this.connection = connection;
            Context = context;
            DefaultRoleId = roleId;
            var mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
            var auditLog = new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>());
            users = new MyUserService(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(context, NullLogger<RbacWriteService>.Instance), auditLog,
                Current, Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());
            var roles = new RoleViewService(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<RoleViewService>(),
                new RolePermissionService(), new RbacWriteService(context, NullLogger<RbacWriteService>.Instance), auditLog, Current);
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AuditContentPath = Path.Combine(Path.GetTempPath(), "aa-audit-" + Guid.NewGuid().ToString("N"));
            var auditQuery = new AuditLogQueryService(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<AuditLogQueryService>(),
                new AuditContentFileStore(Options.Create(settings), loggerFactory.CreateLogger<AuditContentFileStore>()));
            var twoFactor = new TwoFactorService(new TestDbContextFactory(connection), null!, null!, null!, null!,
                Options.Create(new BootstrapSettings()), TimeProvider.System, auditLog, Current, loggerFactory.CreateLogger<TwoFactorService>());
            Tools = new AccountsAssistantTools(users, roles, auditQuery, Audit, Current, new RolePermissionService(), twoFactor);
        }

        public BackendDBContext Context { get; }

        public int DefaultRoleId { get; }

        /// <summary>目前登入的使用者（預設是不存在的 Id 99；測「自己」時改成某個帳號的 Id）。</summary>
        public CurrentUserService Current { get; } = new() { CurrentUser = new CurrentUser { Id = 99, Account = "operator", IsAdmin = true } };

        public RecordingAuditLogService Audit { get; } = new();

        public AccountsAssistantTools Tools { get; }

        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            var role = new RoleView { Name = "一般", TabViewJson = "[]" };
            context.RoleView.Add(role);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return new DbFixture(connection, context, role.Id);
        }

        public async Task<int> AddUserAsync(string account, bool status = true, bool isAdmin = false,
            List<int>? additionalRoleIds = null, List<string>? teamNames = null)
        {
            var result = await users.AddAsync(new MyUserAdapterModel
            {
                Account = account,
                Name = account,
                Password = "Passw0rd",
                Status = status,
                IsAdmin = isAdmin,
                RoleViewId = DefaultRoleId,
                AdditionalRoleIds = additionalRoleIds ?? [],
                TeamNames = teamNames ?? [],
            });
            Assert.True(result.Success, result.Message);
            Context.ChangeTracker.Clear();
            return await Context.MyUser.AsNoTracking().Where(x => x.Account == account).Select(x => x.Id).SingleAsync();
        }

        public async Task<int> AddRoleAsync(string name)
        {
            var role = new RoleView { Name = name, TabViewJson = "[]" };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return role.Id;
        }

        public async Task AddTeamAsync(string name)
        {
            Context.Team.Add(new Team { Name = name, IsEnabled = true });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public async Task<MyUser> UserAsync(string account)
            => await Context.MyUser.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Account == account);

        public async Task<(List<int> AdditionalRoleIds, List<string> TeamNames)> AssignmentsAsync(int userId)
            => await users.GetUserAssignmentsAsync(userId);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
