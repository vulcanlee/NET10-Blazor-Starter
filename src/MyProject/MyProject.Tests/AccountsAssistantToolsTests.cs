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

    /// <summary>真的 SQLite：MyUserService＋AuditLogQueryService；稽核用記錄替身。</summary>
    private sealed class DbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
        private readonly MyUserService users;
        private readonly int roleId;

        private DbFixture(SqliteConnection connection, BackendDBContext context, int roleId)
        {
            this.connection = connection;
            Context = context;
            this.roleId = roleId;
            var mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
            users = new MyUserService(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<MyUserService>(),
                new RbacWriteService(context, NullLogger<RbacWriteService>.Instance),
                new AuditLogService(new TestDbContextFactory(connection), loggerFactory.CreateLogger<AuditLogService>()),
                new CurrentUserService(), Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AuditContentPath = Path.Combine(Path.GetTempPath(), "aa-audit-" + Guid.NewGuid().ToString("N"));
            var auditQuery = new AuditLogQueryService(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<AuditLogQueryService>(),
                new AuditContentFileStore(Options.Create(settings), loggerFactory.CreateLogger<AuditContentFileStore>()));
            Tools = new AccountsAssistantTools(users, roles: null!, auditQuery, Audit,
                new CurrentUserService { CurrentUser = new CurrentUser { Id = 99, Account = "support" } });
        }

        public BackendDBContext Context { get; }

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

        public async Task<int> AddUserAsync(string account, bool status = true)
        {
            var result = await users.AddAsync(new MyUserAdapterModel
            {
                Account = account,
                Name = account,
                Password = "Passw0rd",
                Status = status,
                RoleViewId = roleId,
            });
            Assert.True(result.Success, result.Message);
            Context.ChangeTracker.Clear();
            return await Context.MyUser.AsNoTracking().Where(x => x.Account == account).Select(x => x.Id).SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
