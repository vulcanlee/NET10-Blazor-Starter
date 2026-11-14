using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Backup;
using MyProject.Web.Shell;
using MyProject.Web.Scheduling;
using Harness = MyProject.Tests.SystemParameterTests.Harness;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「系統設定與維運」的不可復原工具（0.9.159 起）：刪除公告與備份、立即執行或啟用清理類排程、縮短保留天數。
/// 稽核同各頁（頁面寫的），成功後才寫並加 via；不是清理類、不會縮短的請求轉回可復原的工具。
/// </summary>
public sealed class OpsIrreversibleToolsTests : IDisposable
{
    private const string RetentionKey = "LogRetentionSettings:AuditLogDays";

    private readonly Harness harness = Harness.Create();
    private readonly string backupRoot = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingAuditLogService audit = new();
    private readonly CountingSignal signal = new();
    private readonly AnnouncementService announcements;
    private readonly OpsAssistantTools tools;

    public OpsIrreversibleToolsTests()
    {
        Directory.CreateDirectory(backupRoot);
        var settings = new SystemSettings();
        settings.ExternalFileSystem.BackupPath = backupRoot;
        var backups = new BackupStore(Options.Create(settings), NullLogger<BackupStore>.Instance);
        var factory = harness.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        announcements = new AnnouncementService(factory, TimeProvider.System, NullLogger<AnnouncementService>.Instance);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "admin", IsAdmin = true } };
        tools = new OpsAssistantTools(
            harness.Services.GetRequiredService<ScheduledJobOverviewService>(), harness.Manager, announcements, backups, null!, null!,
            audit, currentUser, new AnnouncementCache(null!, TimeProvider.System, NullLogger<AnnouncementCache>.Instance), signal);
    }

    public void Dispose()
    {
        harness.Dispose();
        try
        {
            Directory.Delete(backupRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task DeleteAnnouncements_PerItem_AuditsTitle_BroadcastsOnce()
    {
        await announcements.AddAsync(new AnnouncementAdapterModel { Title = "甲", Content = "c", StartAt = DateTime.Now }, "admin");
        await announcements.AddAsync(new AnnouncementAdapterModel { Title = "乙", Content = "c", StartAt = DateTime.Now }, "admin");
        var ids = (await announcements.GetAllAsync()).Select(item => item.Id).ToArray();

        var json = await tools.DeleteAnnouncementsAsync([.. ids, 9999]);

        Assert.Contains("\"succeeded\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.Empty(await announcements.GetAllAsync());
        Assert.Equal(1, signal.AnnouncementsChanged);
        Assert.Equal(2, audit.Entries.Count(entry => entry.Action == "Announcement.Delete"));
        Assert.Contains(audit.Entries, entry => entry.Detail == "title=甲; via=admin-assistant");
    }

    [Fact]
    public async Task DeleteBackups_ListedFileOnly_AuditsFileName()
    {
        const string name = "backup-20261101-020000.zip";
        await File.WriteAllTextAsync(Path.Combine(backupRoot, name), "x");

        var json = await tools.DeleteBackupsAsync([name, "../secret.zip", "backup-20200101-000000.zip"]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":2", json, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(backupRoot, name)));
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Backup.Delete", entry.Action);
        Assert.Equal($"file={name}; via=admin-assistant", entry.Detail);
    }

    [Fact]
    public async Task RunPurgeJob_PurgeJobQueued_NonPurgeJobRedirected()
    {
        var redirected = await tools.RunPurgeJobAsync("SystemBackup");
        var queued = await tools.RunPurgeJobAsync("auditlogretention");

        Assert.Contains("trigger_scheduled_job", redirected, StringComparison.Ordinal);
        Assert.Contains("已排入", queued, StringComparison.Ordinal);
        Assert.Equal("job=AuditLogRetention; via=admin-assistant", Assert.Single(audit.Entries).Detail);
    }

    [Fact]
    public async Task EnablePurgeJob_EnablesOnlyPurgeJobs()
    {
        await tools.SetScheduledJobEnabledAsync("AuditLogRetention", enabled: false);

        var redirected = await tools.EnablePurgeJobAsync("PasswordExpiryReminder");
        await tools.EnablePurgeJobAsync("AuditLogRetention");

        Assert.Contains("set_scheduled_job_enabled", redirected, StringComparison.Ordinal);
        var overview = await harness.Services.GetRequiredService<ScheduledJobOverviewService>().GetOverviewAsync();
        Assert.True(overview.Items.Single(item => item.Name == "AuditLogRetention").IsEnabled);
        Assert.Equal(["Job.Disable", "Job.Enable"], audit.Entries.Select(entry => entry.Action));
    }

    [Fact]
    public async Task ShortenRetention_Shortens_AndRefusesWhenNotShortening()
    {
        var longer = await tools.ShortenRetentionAsync(RetentionKey, "400");
        var notRetention = await tools.ShortenRetentionAsync("SlowOperationSettings:DbCommandMs", "10");
        var text = await tools.ShortenRetentionAsync(RetentionKey, "30");

        Assert.Contains("set_system_parameter", longer, StringComparison.Ordinal);
        Assert.Contains("保留天數", notRetention, StringComparison.Ordinal);
        Assert.Contains("已更新", text, StringComparison.Ordinal);
        Assert.Equal("30", (await harness.Manager.GetOverviewAsync()).Items.Single(item => item.Definition.Key == RetentionKey).EffectiveValue);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("SystemParameter.Update", entry.Action);
        Assert.EndsWith("; via=admin-assistant", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortenRetention_Default_ResetsWhenThatShortens()
    {
        await tools.SetSystemParameterAsync(RetentionKey, "400");

        await tools.ShortenRetentionAsync(RetentionKey, "default");

        Assert.Null((await harness.Manager.GetOverviewAsync()).Items.Single(item => item.Definition.Key == RetentionKey).Override);
        Assert.Equal("SystemParameter.Reset", audit.Entries[^1].Action);
    }

    [Fact]
    public void IrreversibleTools_FlaggedWithNotes()
    {
        Assert.Equal(
            ["delete_announcements", "delete_backups", "run_purge_job", "enable_purge_job", "shorten_retention"],
            OpsAssistantTools.IrreversibleTools.Select(tool => tool.Name));
        Assert.True(OpsAssistantTools.CustomPurgeTool.IsIrreversible);
        Assert.All(OpsAssistantTools.IrreversibleTools.Append(OpsAssistantTools.CustomPurgeTool), tool =>
        {
            Assert.True(tool.IsMutating && tool.IsIrreversible, tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.ApprovalNote), tool.Name);
        });
    }

    private sealed class CountingSignal : INotificationSignal
    {
        public int AnnouncementsChanged { get; private set; }

        public IDisposable Subscribe(int userId, Func<Task> handler) => throw new NotSupportedException();

        public IDisposable SubscribeAnnouncements(Func<Task> handler) => throw new NotSupportedException();

        public void Publish(IEnumerable<int> userIds)
        {
        }

        public void PublishAnnouncementsChanged() => AnnouncementsChanged++;
    }
}
