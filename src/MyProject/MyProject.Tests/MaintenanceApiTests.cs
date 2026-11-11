using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 系統備份、資料保留與清理、資料庫用量頁的 Web API（0.10.6 起，WASM 遷移 M7）：只限管理員（<c>[AdminOnly]</c>），
/// 稽核寫法與 Server 版頁面相同。備份下載改用一次性票證（取代只收 Cookie 的 <c>api/backups/{檔名}/download</c>），
/// 兌換時重新確認帳號、工作階段版本與「仍是管理員」，只認備份目錄裡、檔名合格的檔案。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class MaintenanceApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "Passw0rd-1";
    private static readonly Regex Offset = new(@"T\d{2}:\d{2}:\d{2}(\.\d+)?[+-]\d{2}:\d{2}");
    private static readonly Regex Envelope = new("\"timestamp\":\"[^\"]*\"");
    private readonly ApiTestApplicationFactory factory;

    public MaintenanceApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    private string BackupRoot => factory.Services.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem.BackupPath;

    // ───────────────────────────── 系統備份 ─────────────────────────────

    [Fact]
    public async Task Backups_List_ShouldReturnFilesNewestFirst_WithTheManifest_TheJob_AndLocalTimesWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var older = await WriteBackupAsync("backup-19990101-000000.zip", withManifest: true);
        var newer = await WriteBackupAsync("backup-19990101-000000-2.zip", withManifest: false);

        var response = await client.GetAsync("/api/v1/backups");
        var text = await response.Content.ReadAsStringAsync();
        var list = Read<BackupListDto>(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var names = list.Items.Select(x => x.FileName).ToList();
        Assert.True(names.IndexOf(newer) >= 0 && names.IndexOf(newer) < names.IndexOf(older), string.Join(", ", names));
        var withManifest = list.Items.Single(x => x.FileName == older);
        Assert.Equal(new DateTime(1999, 1, 1), withManifest.CreatedAtLocal);
        Assert.Equal("0.9.99 (2026/10/04)", withManifest.Manifest!.SystemVersion);
        Assert.Equal(JobRunTriggers.Manual, withManifest.Manifest.Trigger);
        Assert.Equal(("ProjectFilePath", 3), (withManifest.Manifest.Folders.Single().SettingKey, withManifest.Manifest.Folders.Single().FileCount));
        Assert.Null(list.Items.Single(x => x.FileName == newer).Manifest);
        Assert.Equal("SystemBackup", list.Job!.Name);
        Assert.False(Offset.IsMatch(Envelope.Replace(text, string.Empty)), text);
    }

    [Fact]
    public async Task Backups_Run_ShouldQueueTheBackupJob_AndAuditLikeTheScreen()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var response = await client.PostAsync("/api/v1/backups/run", null);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<object>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("已開始備份。", result!.Message);
        var audit = await SingleAuditAsync(AuditActions.Job.Trigger, admin.Id);
        Assert.Equal(("ScheduledJob", "SystemBackup", "job=SystemBackup"), (audit.TargetType, audit.TargetId, audit.Detail));
    }

    [Fact]
    public async Task Backups_Delete_ShouldRemoveTheFile_AuditOnce_AndReturn404WhenItIsGone()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var name = await WriteBackupAsync($"backup-19990102-{Random.Shared.Next(100000, 999999)}.zip", withManifest: false);

        var deleted = await client.DeleteAsync($"/api/v1/backups/{name}");
        var again = await client.DeleteAsync($"/api/v1/backups/{name}");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.False(File.Exists(Path.Combine(BackupRoot, name)));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("找不到這份備份，可能已被刪除。", (await again.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        var audit = await SingleAuditAsync(AuditActions.Backup.Delete, admin.Id);
        Assert.Equal(("Backup", name, $"file={name}"), (audit.TargetType, audit.TargetId, audit.Detail));
    }

    [Fact]
    public async Task Backups_Ticket_ShouldDownloadTheFileOnce_AndAuditWhenItIsRedeemed()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var name = await WriteBackupAsync($"backup-19990103-{Random.Shared.Next(100000, 999999)}.zip", withManifest: true);
        var expected = await File.ReadAllBytesAsync(Path.Combine(BackupRoot, name));

        var ticket = await SendAsync<DownloadTicketDto>(client, HttpMethod.Post, $"/api/v1/backups/{name}/ticket");
        Assert.StartsWith("/api/v1/downloads/", ticket.Url);
        Assert.Equal(60, ticket.ExpiresInSeconds);
        Assert.Equal(0, await CountAuditAsync(AuditActions.Backup.Download, admin.Id));

        // 瀏覽器原生下載不帶 Token：兌換端點不需要登入，安全性來自票證。
        using var anonymous = factory.CreateClient();
        var download = await anonymous.GetAsync(ticket.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(name, download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(expected, await download.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ticket.Url)).StatusCode);
        var audit = await SingleAuditAsync(AuditActions.Backup.Download, admin.Id);
        Assert.Equal(("Backup", name, $"file={name}"), (audit.TargetType, audit.TargetId, audit.Detail));
    }

    [Fact]
    public async Task Backups_Ticket_ShouldBeRefused_WhenTheUserIsNoLongerAnAdmin_TheSessionWasRevoked_OrTheFileIsGone()
    {
        var name = await WriteBackupAsync($"backup-19990104-{Random.Shared.Next(100000, 999999)}.zip", withManifest: false);
        using var anonymous = factory.CreateClient();

        var demoted = await AddUserAsync(isAdmin: true);
        using (var client = await SignedInClientAsync(demoted))
        {
            var ticket = await SendAsync<DownloadTicketDto>(client, HttpMethod.Post, $"/api/v1/backups/{name}/ticket");
            await using var context = await CreateContextAsync();
            await context.MyUser.Where(x => x.Id == demoted.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsAdmin, false));
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ticket.Url)).StatusCode);
        }

        var revoked = await AddUserAsync(isAdmin: true);
        using (var client = await SignedInClientAsync(revoked))
        {
            var ticket = await SendAsync<DownloadTicketDto>(client, HttpMethod.Post, $"/api/v1/backups/{name}/ticket");
            await factory.Services.GetRequiredService<ISecurityStampService>().RotateAsync(revoked.Id);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ticket.Url)).StatusCode);
        }

        var admin = await AddUserAsync(isAdmin: true);
        using (var client = await SignedInClientAsync(admin))
        {
            var ticket = await SendAsync<DownloadTicketDto>(client, HttpMethod.Post, $"/api/v1/backups/{name}/ticket");
            File.Delete(Path.Combine(BackupRoot, name));
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ticket.Url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/backups/{name}/ticket", null)).StatusCode);
        }

        await using var db = await CreateContextAsync();
        Assert.Equal(0, await db.AuditLog.CountAsync(x => x.Action == AuditActions.Backup.Download && x.TargetId == name));
    }

    [Theory]
    [InlineData("..%2Fappsettings.json")]
    [InlineData("..%2F..%2FDB%2FBackendDB.db")]
    [InlineData("notes.zip")]
    [InlineData("backup-19990105-000000.zip.partial")]
    public async Task Backups_TicketAndDelete_ShouldOnlyAcceptBackupFilesInTheBackupFolder(string fileName)
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        Directory.CreateDirectory(BackupRoot);
        await File.WriteAllTextAsync(Path.Combine(BackupRoot, "notes.zip"), "not a backup");
        await File.WriteAllTextAsync(Path.Combine(BackupRoot, "backup-19990105-000000.zip.partial"), "partial");

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/backups/{fileName}/ticket", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/backups/{fileName}")).StatusCode);
        Assert.True(File.Exists(Path.Combine(BackupRoot, "notes.zip")));
    }

    // ───────────────────────────── 資料保留與清理 ─────────────────────────────

    [Fact]
    public async Task DataRetention_Overview_ShouldListEveryKind_WithTheServerToday_AndLocalTimesWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.GetAsync("/api/v1/data-retention");
        var text = await response.Content.ReadAsStringAsync();
        var overview = Read<DataRetentionOverviewDto>(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DateTime.Today, overview.TodayLocal);
        Assert.Equal(
            ["deleted-project", "deleted-category", "deleted-team", "deleted-user", "deleted-role", "exception-log", "token-usage", "ai-call-log",
             "agent-conversation", "admin-patrol-report", "audit-log", "notification", "email-log", "refresh-session", "backup", "job-run", "nlog", "announcement"],
            overview.Rows.Select(x => x.Key));
        var audit = overview.Rows.Single(x => x.Key == "audit-log");
        Assert.Equal("AuditLogRetention", audit.Job!.Name);
        Assert.True(audit.CanCustomPurge);
        Assert.NotNull(overview.Rows.Single(x => x.Key == "backup").Folder);
        Assert.False(Offset.IsMatch(Envelope.Replace(text, string.Empty)), text);
    }

    [Fact]
    public async Task DataRetention_PurgeNow_ShouldQueueTheJob_AndAuditLikeTheScreen()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var response = await client.PostAsync("/api/v1/data-retention/notification/purge-now", null);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<object>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("已開始清理「", result!.Message);
        Assert.EndsWith("」，完成後這一頁會自動更新。", result.Message);
        var audit = await SingleAuditAsync(AuditActions.Job.Trigger, admin.Id);
        Assert.Equal(("ScheduledJob", "NotificationRetention"), (audit.TargetType, audit.TargetId));
        Assert.Matches(@"^job=NotificationRetention; source=data-retention; expected=\d+$", audit.Detail);
    }

    [Fact]
    public async Task DataRetention_PurgeNow_ShouldReturn404ForAnUnknownKind_And400WhenTheKindHasNoJob()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/data-retention/no-such-kind/purge-now", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/data-retention/nlog/purge-now", null)).StatusCode);
        Assert.Equal(0, await CountAuditAsync(AuditActions.Job.Trigger, admin.Id));
    }

    [Fact]
    public async Task DataRetention_CustomPurge_ShouldPreviewThenDelete_AndAuditLikeTheScreen()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("自訂清除");
        await using (var context = await CreateContextAsync())
        {
            context.EmailOutbox.AddRange(
                new EmailOutbox { CreatedAtUtc = new DateTime(1999, 6, 1, 0, 0, 0, DateTimeKind.Utc), Kind = "Test", To = "a@e2e.test", Subject = tag, Status = EmailOutboxStatus.Sent, Attempts = 1 },
                new EmailOutbox { CreatedAtUtc = new DateTime(1999, 6, 2, 0, 0, 0, DateTimeKind.Utc), Kind = "Test", To = "b@e2e.test", Subject = tag, Status = EmailOutboxStatus.Failed, Attempts = 5 });
            await context.SaveChangesAsync();
        }

        var request = new CustomPurgeRequestDto { BeforeDate = new DateOnly(2000, 1, 2) };
        var preview = await SendAsync<int>(client, HttpMethod.Post, "/api/v1/data-retention/email-log/preview", request);
        var outcome = await SendAsync<CustomPurgeResultDto>(client, HttpMethod.Post, "/api/v1/data-retention/email-log/custom-purge", request);

        Assert.Equal(2, preview);
        Assert.Equal((2, 0, false), (outcome.Removed, outcome.Left, outcome.Failed));
        await using (var context = await CreateContextAsync())
        {
            Assert.False(await context.EmailOutbox.AnyAsync(x => x.Subject == tag));
        }

        var audit = await SingleAuditAsync(AuditActions.EmailLog.Purge, admin.Id);
        Assert.Equal(("EmailOutbox", "*", "source=data-retention; mode=beforeDate; value=2000-01-02; rows=2"), (audit.TargetType, audit.TargetId, audit.Detail));

        // 沒有刪到東西時不寫稽核（同 Server 版）。
        await SendAsync<CustomPurgeResultDto>(client, HttpMethod.Post, "/api/v1/data-retention/email-log/custom-purge", request);
        Assert.Equal(1, await CountAuditAsync(AuditActions.EmailLog.Purge, admin.Id));
    }

    [Theory]
    [InlineData(null, null, "請選擇「只留最近 N 天」或「刪除這一天之前」其中一種。")]
    [InlineData(0, null, "保留天數要在 1～3650 天之間。")]
    [InlineData(null, "2999-01-01", "日期不能晚於今天。")]
    public async Task DataRetention_CustomPurge_ShouldRejectAnInvalidCutoff(int? keepDays, string? beforeDate, string message)
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var request = new CustomPurgeRequestDto { KeepDays = keepDays, BeforeDate = beforeDate is null ? null : DateOnly.Parse(beforeDate) };

        foreach (var action in new[] { "preview", "custom-purge" })
        {
            var response = await client.PostAsJsonAsync($"/api/v1/data-retention/email-log/{action}", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(message, (await response.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        }
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("admin-patrol-report")]
    [InlineData("no-such-kind")]
    public async Task DataRetention_CustomPurge_ShouldReturn400ForKindsWithoutIt(string rowKey)
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var request = new CustomPurgeRequestDto { KeepDays = 30 };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/data-retention/{rowKey}/preview", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/data-retention/{rowKey}/custom-purge", request)).StatusCode);
    }

    // ───────────────────────────── 資料庫用量 ─────────────────────────────

    [Fact]
    public async Task DatabaseUsage_ShouldReturnTheReport_WithoutTheDatabasePath()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var databasePath = factory.Services.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem.DatabasePath;

        var response = await client.GetAsync("/api/v1/database-usage");
        var text = await response.Content.ReadAsStringAsync();
        var report = Read<DatabaseUsageDto>(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, report.ErrorMessage);
        Assert.Equal("BackendDB.db", report.DatabaseFileName);
        Assert.True(report.MainDbBytes > 0);
        Assert.Equal(report.MainDbBytes + report.WalBytes + report.ShmBytes, report.TotalOnDiskBytes);
        Assert.Equal(report.PageCount * report.PageSize, report.AllocatedBytes);
        Assert.Contains(report.Tables, x => x.TableName == nameof(BackendDBContext.MyUser) && x.RowCount > 0);
        Assert.DoesNotContain(JsonEncodedText.Encode(databasePath).ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("databaseFilePath", text, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── 權限 ─────────────────────────────

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);
        var name = await WriteBackupAsync($"backup-19990106-{Random.Shared.Next(100000, 999999)}.zip", withManifest: false);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/api/v1/backups", null),
            (HttpMethod.Post, "/api/v1/backups/run", null),
            (HttpMethod.Delete, $"/api/v1/backups/{name}", null),
            (HttpMethod.Post, $"/api/v1/backups/{name}/ticket", null),
            (HttpMethod.Get, "/api/v1/data-retention", null),
            (HttpMethod.Post, "/api/v1/data-retention/notification/purge-now", null),
            (HttpMethod.Post, "/api/v1/data-retention/email-log/preview", new CustomPurgeRequestDto { KeepDays = 30 }),
            (HttpMethod.Post, "/api/v1/data-retention/email-log/custom-purge", new CustomPurgeRequestDto { KeepDays = 30 }),
            (HttpMethod.Get, "/api/v1/database-usage", null),
        };

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        Assert.True(File.Exists(Path.Combine(BackupRoot, name)));
        await using var context = await CreateContextAsync();
        Assert.Equal(calls.Length, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
    }

    [Fact]
    public async Task TheOldCookieDownloadEndpoint_ShouldBeGone()
    {
        var name = await WriteBackupAsync($"backup-19990107-{Random.Shared.Next(100000, 999999)}.zip", withManifest: false);
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.GetAsync($"/api/backups/{name}/download");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(typeof(MyProject.Web.Controllers.DownloadsController).Assembly.GetType("MyProject.Web.Controllers.BackupController"));
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private async Task<string> WriteBackupAsync(string fileName, bool withManifest)
    {
        Directory.CreateDirectory(BackupRoot);
        var path = Path.Combine(BackupRoot, fileName);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await using (var writer = new StreamWriter(zip.CreateEntry("db/BackendDB.db").Open()))
            {
                await writer.WriteAsync(new string('x', 4096));
            }

            if (withManifest)
            {
                await using var stream = zip.CreateEntry("manifest.json").Open();
                await JsonSerializer.SerializeAsync(stream, new
                {
                    formatVersion = 1,
                    systemVersion = "0.9.99 (2026/10/04)",
                    trigger = JobRunTriggers.Manual,
                    folders = new[] { new { entry = "files/ProjectFile", settingKey = "ProjectFilePath", fileCount = 3 } },
                    missingFileCount = 0,
                });
            }
        }

        return fileName;
    }

    private static T Read<T>(string text) => JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;

    private async Task<AuditLog> SingleAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.SingleAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return Read<T>(text);
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = Unique("m7-");
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);
        return user;
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
