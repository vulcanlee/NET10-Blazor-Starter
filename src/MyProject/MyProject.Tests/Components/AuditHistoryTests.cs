using AutoMapper;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 資料頁的「異動歷程」（0.9.173 起）：按鈕只給管理員；對話窗只列那一筆的異動（不含瀏覽類），點一筆讀詳細內容。
/// </summary>
public sealed class AuditHistoryTests : ComponentTestBase
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection;
    private readonly RecordingAuditLogService audit = new();
    private readonly AuditContentFileStore contentStore;

    public AuditHistoryTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options))
        {
            context.Database.EnsureCreated();
        }

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AuditContentPath = contentRoot;
        contentStore = new AuditContentFileStore(Options.Create(settings), NullLogger<AuditContentFileStore>.Instance);
        var mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), NullLoggerFactory.Instance).CreateMapper();

        Services.AddLogging();
        Services.AddSingleton(new AuditLogQueryService(
            new TestDbContextFactory(connection), mapper, NullLogger<AuditLogQueryService>.Instance, contentStore));
        Services.AddSingleton<IAuditLogService>(audit);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            connection.Dispose();
            if (Directory.Exists(contentRoot))
            {
                Directory.Delete(contentRoot, recursive: true);
            }
        }

        base.Dispose(disposing);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Button_ShouldRenderOnlyForAdmins(bool isAdmin, int expectedButtons)
    {
        SignIn(isAdmin);

        // 頁面上它放在 ActionColumn 的 <Space> 裡；SpaceItem 需要外層 Space。
        var cut = Render<AntDesign.Space>(parameters => parameters.AddChildContent<AuditHistoryButton>());

        Assert.Equal(expectedButtons, cut.FindAll("button.crud-action-button").Count);
    }

    [Fact]
    public async Task Dialog_ShouldListOnlyChangesOfThatRecord_AndReadContentOnSelect()
    {
        SignIn(isAdmin: true);
        var diffFile = await contentStore.WriteAsync(DateTime.UtcNow, "Name: 分類A → 分類B");
        await SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow.AddHours(-2), Action = "Category.Create", TargetType = "Category", TargetId = "12", ActorAccount = "alice", ActorUserId = 2, Detail = "name=分類A" },
            new AuditLog { OccurredAt = DateTime.UtcNow.AddHours(-1), Action = "Category.Update", TargetType = "Category", TargetId = "12", ActorAccount = "bob", ActorUserId = 3, Detail = "name=分類B", ContentFile = diffFile },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Category.View", TargetType = "Category", TargetId = "12", ActorAccount = "carol" },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Category.Update", TargetType = "Category", TargetId = "112", ActorAccount = "dave" });
        var cut = Render<AuditHistoryDialog>();

        await cut.InvokeAsync(() => cut.Instance.ShowAsync("Category", "12", "分類B"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("alice", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("bob", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("carol", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("dave", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("分類B", cut.Markup, StringComparison.Ordinal);
        });

        // 最新的（Category.Update）在最上面；點它讀出修改前後差異。
        await cut.InvokeAsync(() => cut.FindAll("button.audit-history-item")[0].Click());

        cut.WaitForAssertion(() => Assert.Contains("Name: 分類A → 分類B", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Audit.Search && e.Detail!.Contains("via=history", StringComparison.Ordinal));
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.Audit.View);
    }

    [Fact]
    public async Task Dialog_WithoutChanges_ShowsEmptyHint()
    {
        SignIn(isAdmin: true);
        var cut = Render<AuditHistoryDialog>();

        await cut.InvokeAsync(() => cut.Instance.ShowAsync("Team", "5", "業務一組"));

        cut.WaitForAssertion(() => Assert.Contains("沒有異動紀錄", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dialog_ForNonAdmin_ShouldNotLoadAnything()
    {
        SignIn(isAdmin: false);
        await SeedAsync(new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Team.Update", TargetType = "Team", TargetId = "5", ActorAccount = "alice" });
        var cut = Render<AuditHistoryDialog>();

        await cut.InvokeAsync(() => cut.Instance.ShowAsync("Team", "5", "業務一組"));

        Assert.DoesNotContain("alice", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }

    private void SignIn(bool isAdmin)
        => Services.AddSingleton(new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "admin", IsAdmin = isAdmin } });

    private async Task SeedAsync(params AuditLog[] rows)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.AuditLog.AddRange(rows);
        await context.SaveChangesAsync();
    }
}
