using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// Agent 對話的服務層（0.9.143 起）。
///
/// 重點：
/// 1. 只能存取自己的對話：別人的讀不到、改不了、刪不掉、也覆寫不了。
/// 2. 內容寫進檔案、資料表只放中繼資料；刪除與過期一定連同內容檔。
/// 3. 過期依「最後一次對話」（UpdatedAt），不是建立時間；改標題不算對話。
/// 4. <c>SaveAsync</c> 絕不拋出；寫檔失敗資料列照樣建立。
/// </summary>
public sealed class AiAgentConversationServiceTests
{
    private const int Alice = 7;
    private const int Bob = 8;

    [Fact]
    public async Task Save_ShouldInsertRowAndWriteContentFile_ThenOverwriteOnNextSave()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(Alice);
        var id = Guid.NewGuid();

        await service.SaveAsync(Snapshot(id, "第一則", "你好"));
        await service.SaveAsync(Snapshot(id, "不會用到的標題", "你好", "再一則", pending: true));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(Alice, row.UserId);
        Assert.Equal("alice", row.Account);
        Assert.Equal("第一則", row.Title);
        Assert.True(row.HasPendingApproval);
        Assert.Equal($"{row.CreatedAt:yyyyMM}/{id:N}.json", row.ContentFile);

        var loaded = await service.LoadMineAsync(id);
        Assert.NotNull(loaded?.Content);
        Assert.Equal(["你好", "再一則"], loaded.Content.Transcript.Select(entry => entry.Text));
        Assert.Equal("v", loaded.Content.Session.GetProperty("k").GetString());
    }

    [Fact]
    public async Task OtherUsersConversation_ShouldBeInvisibleAndUntouchable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        await fixture.CreateService(Alice).SaveAsync(Snapshot(id, "Alice 的對話", "秘密"));
        var bob = fixture.CreateService(Bob);

        Assert.Null(await bob.LoadMineAsync(id));
        Assert.Empty(await bob.ListMineAsync(AgentName));
        Assert.False((await bob.RenameAsync(id, "被改了")).Success);
        Assert.False((await bob.DeleteAsync(id)).Success);

        // 用同一個 ConversationId 存也覆寫不了別人的內容。
        await bob.SaveAsync(Snapshot(id, "Bob", "蓋掉"));
        var loaded = await fixture.CreateService(Alice).LoadMineAsync(id);
        Assert.Equal("秘密", Assert.Single(loaded!.Content!.Transcript).Text);
        Assert.Equal("Alice 的對話", Assert.Single(await fixture.ListAsync()).Title);
    }

    [Fact]
    public async Task ListMine_ShouldOnlyListThisAgent_NewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(Alice);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await service.SaveAsync(Snapshot(older, "舊的", "a"));
        await service.SaveAsync(Snapshot(newer, "新的", "b"));
        await service.SaveAsync(Snapshot(Guid.NewGuid(), "別的 Agent", "c") with { AgentName = "Other" });
        await fixture.SetUpdatedAtAsync(older, DateTime.Now.AddDays(-1));

        var list = await service.ListMineAsync(AgentName);

        Assert.Equal([newer, older], list.Select(item => item.ConversationId));
    }

    [Fact]
    public async Task Rename_ShouldValidate_AndNotTouchLastConversationTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(Alice);
        var id = Guid.NewGuid();
        await service.SaveAsync(Snapshot(id, "原本", "a"));
        var updatedAt = new DateTime(2026, 1, 2, 3, 4, 5);
        await fixture.SetUpdatedAtAsync(id, updatedAt);

        Assert.False((await service.RenameAsync(id, "   ")).Success);
        Assert.False((await service.RenameAsync(id, new string('字', AiAgentConversationService.MaxTitleLength + 1))).Success);
        Assert.True((await service.RenameAsync(id, "  新標題  ")).Success);

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal("新標題", row.Title);
        Assert.Equal(updatedAt, row.UpdatedAt);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task Delete_ShouldRemoveRowAndFile_AndAudit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(Alice);
        var id = Guid.NewGuid();
        await service.SaveAsync(Snapshot(id, "要刪的", "a"));
        var file = fixture.FullPath(Assert.Single(await fixture.ListAsync()).ContentFile!);
        Assert.True(File.Exists(file));

        var result = await service.DeleteAsync(id);

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.False(File.Exists(file));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((AuditActions.AiAgentConversation.Delete, Alice, "alice"), (audit.Action, audit.ActorUserId, audit.ActorAccount));
        Assert.DoesNotContain("要刪的", audit.Detail, StringComparison.Ordinal);
    }

    /// <summary>過期看最後一次對話：很早建立、最近還在聊的不能刪。</summary>
    [Fact]
    public async Task PurgeExpired_ShouldUseLastConversationTime_AndDeleteFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(Alice, retentionDays: 30);
        var stale = Guid.NewGuid();
        var active = Guid.NewGuid();
        await service.SaveAsync(Snapshot(stale, "舊", "a"));
        await service.SaveAsync(Snapshot(active, "還在聊", "b"));
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        await fixture.SetUpdatedAtAsync(stale, now.AddDays(-31));
        await fixture.SetTimesAsync(active, createdAt: now.AddDays(-200), updatedAt: now.AddDays(-1));
        var staleFile = fixture.FullPath((await fixture.ListAsync()).Single(x => x.ConversationId == stale).ContentFile!);

        var stats = await service.GetRetentionStatsAsync(now);
        var removed = await service.PurgeExpiredAsync(now);

        Assert.Equal((2, 1), (stats.Total, stats.Expired!.Value));
        Assert.Equal(1, removed);
        Assert.Equal(active, Assert.Single(await fixture.ListAsync()).ConversationId);
        Assert.False(File.Exists(staleFile));
        Assert.NotNull((await service.LoadMineAsync(active))?.Content);
    }

    /// <summary>寫檔失敗（目錄設在檔案上）不影響資料列；之後讀回時內容為 null，不拋出。</summary>
    [Fact]
    public async Task Save_WhenFileCannotBeWritten_ShouldStillInsertRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var blocker = Path.Combine(fixture.RootPath, "blocker.txt");
        await File.WriteAllTextAsync(blocker, "x");
        var service = fixture.CreateService(Alice, rootPath: blocker);
        var id = Guid.NewGuid();

        await service.SaveAsync(Snapshot(id, "標題", "a"));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Null(row.ContentFile);
        var loaded = await service.LoadMineAsync(id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.Content);
    }

    /// <summary>資料庫裡的路徑被竄改成 ../：讀不到根目錄以外的檔，也刪不到。</summary>
    [Fact]
    public async Task ContentFile_OutsideRoot_ShouldBeIgnored()
    {
        await using var fixture = await Fixture.CreateAsync();
        var outside = Path.Combine(Path.GetDirectoryName(fixture.RootPath)!, $"outside-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(outside, "{}");
        try
        {
            var service = fixture.CreateService(Alice);
            var id = Guid.NewGuid();
            await service.SaveAsync(Snapshot(id, "標題", "a"));
            await fixture.SetContentFileAsync(id, $"../{Path.GetFileName(outside)}");

            Assert.Null((await service.LoadMineAsync(id))!.Content);
            Assert.True((await service.DeleteAsync(id)).Success);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("你好", "你好")]
    [InlineData("  第一行\n\n第二行  ", "第一行 第二行")]
    [InlineData("一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十多出來", "一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十…")]
    public void TitleFrom_ShouldCollapseWhitespaceAndTruncate(string message, string expected)
        => Assert.Equal(expected, AiAgentConversationService.TitleFrom(message));

    private const string AgentName = "AgentPlayground";

    private static AiAgentConversationSnapshot Snapshot(Guid id, string title, params string[] messages)
        => Snapshot(id, title, messages, pending: false);

    private static AiAgentConversationSnapshot Snapshot(Guid id, string title, string first, string second, bool pending)
        => Snapshot(id, title, [first, second], pending);

    private static AiAgentConversationSnapshot Snapshot(Guid id, string title, string[] messages, bool pending)
        => new(id, AgentName, title, pending, new AiAgentConversationContent
        {
            Session = JsonDocument.Parse("""{"k":"v"}""").RootElement.Clone(),
            Transcript = messages.Select(text => new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.User, Text = text }).ToList(),
        });

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });

        private Fixture(SqliteConnection connection, string rootPath)
        {
            this.connection = connection;
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public RecordingAuditLogService Audit { get; } = new();

        public string FullPath(string relativePath)
            => Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options))
            {
                await context.Database.EnsureCreatedAsync();
            }

            var path = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new Fixture(connection, path);
        }

        public AiAgentConversationService CreateService(int userId, int retentionDays = 90, string? rootPath = null)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AiAgentConversationPath = rootPath ?? RootPath;
            var currentUser = new CurrentUserService
            {
                CurrentUser = new CurrentUser { Id = userId, Account = userId == Alice ? "alice" : "bob" },
            };

            return new AiAgentConversationService(
                new TestDbContextFactory(connection),
                new AiAgentConversationFileStore(Options.Create(settings), loggerFactory.CreateLogger<AiAgentConversationFileStore>()),
                currentUser,
                Audit,
                new StaticOptionsMonitor<AiAgentConversationSettings>(new AiAgentConversationSettings { RetentionDays = retentionDays }),
                loggerFactory.CreateLogger<AiAgentConversationService>());
        }

        public async Task<List<AiAgentConversation>> ListAsync()
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            return await context.AiAgentConversation.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        public Task SetUpdatedAtAsync(Guid id, DateTime updatedAt) => UpdateAsync(id, item => item.UpdatedAt = updatedAt);

        public Task SetTimesAsync(Guid id, DateTime createdAt, DateTime updatedAt)
            => UpdateAsync(id, item => (item.CreatedAt, item.UpdatedAt) = (createdAt, updatedAt));

        public Task SetContentFileAsync(Guid id, string contentFile) => UpdateAsync(id, item => item.ContentFile = contentFile);

        private async Task UpdateAsync(Guid id, Action<AiAgentConversation> change)
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            change(await context.AiAgentConversation.SingleAsync(x => x.ConversationId == id));
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            loggerFactory.Dispose();
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
