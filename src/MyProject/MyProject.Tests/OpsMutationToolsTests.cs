using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Components.Layout;
using MyProject.Web.Scheduling;
using Harness = MyProject.Tests.SystemParameterTests.Harness;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「系統設定與維運」的異動工具（0.9.158 起）：系統參數（縮短保留天數一律拒絕，留到里程碑 4）、
/// 排程作業（清理類不能立即執行）、公告（對象名稱轉 Id、刷新快取與廣播）；稽核同各頁，detail 加 via。
/// </summary>
public sealed class OpsMutationToolsTests : IDisposable
{
    private const string NormalKey = "SlowOperationSettings:DbCommandMs";
    private const string RetentionKey = "LogRetentionSettings:AuditLogDays";

    private readonly Harness harness = Harness.Create(configure: services => services.AddSingleton(
        new MapperConfiguration(c => c.AddProfile<AutoMapping>(), NullLoggerFactory.Instance).CreateMapper()));
    private readonly RecordingAuditLogService audit = new();
    private readonly CountingSignal signal = new();
    private readonly AnnouncementService announcements;
    private readonly OpsAssistantTools tools;

    public OpsMutationToolsTests()
    {
        var factory = harness.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        announcements = new AnnouncementService(factory, TimeProvider.System, NullLogger<AnnouncementService>.Instance);
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "admin", IsAdmin = true } };
        var cache = new AnnouncementCache(null!, TimeProvider.System, NullLogger<AnnouncementCache>.Instance);
        tools = new OpsAssistantTools(
            harness.Services.GetRequiredService<ScheduledJobOverviewService>(), harness.Manager, announcements,
            null!, null!, null!, audit, currentUser, cache, signal);
    }

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task SetSystemParameter_SavesAndAuditsWithVia()
    {
        var text = await tools.SetSystemParameterAsync(NormalKey, "4321");

        Assert.Contains("已更新", text, StringComparison.Ordinal);
        Assert.Equal("4321", (await harness.Manager.GetOverviewAsync()).Items.Single(item => item.Definition.Key == NormalKey).EffectiveValue);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("SystemParameter.Update", entry.Action);
        Assert.Contains($"key={NormalKey}", entry.Detail, StringComparison.Ordinal);
        Assert.EndsWith("; via=admin-assistant", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetSystemParameter_ShortensRetention_Refused()
    {
        var text = await tools.SetSystemParameterAsync(RetentionKey, "1");

        Assert.Contains("縮短", text, StringComparison.Ordinal);
        Assert.Equal("365", (await harness.Manager.GetOverviewAsync()).Items.Single(item => item.Definition.Key == RetentionKey).EffectiveValue);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task SetSystemParameter_LabelAccepted_UnknownRejected()
    {
        Assert.Contains("找不到", await tools.SetSystemParameterAsync("NoSuch:Key", "1"), StringComparison.Ordinal);
        Assert.Contains("400", await tools.SetSystemParameterAsync("稽核紀錄保留天數", "400"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetSystemParameter_AfterSave_ResetsAndAudits_NoOverrideReported()
    {
        Assert.Contains("沒有", await tools.ResetSystemParameterAsync(NormalKey), StringComparison.Ordinal);

        await tools.SetSystemParameterAsync(NormalKey, "4321");
        await tools.ResetSystemParameterAsync(NormalKey);

        Assert.Null((await harness.Manager.GetOverviewAsync()).Items.Single(item => item.Definition.Key == NormalKey).Override);
        Assert.Equal("SystemParameter.Reset", audit.Entries[^1].Action);
        Assert.EndsWith("; via=admin-assistant", audit.Entries[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetSystemParameter_WouldShortenRetention_Refused()
    {
        await tools.SetSystemParameterAsync(RetentionKey, "400");

        var text = await tools.ResetSystemParameterAsync(RetentionKey);

        Assert.Contains("縮短", text, StringComparison.Ordinal);
        Assert.NotNull((await harness.Manager.GetOverviewAsync()).Items.Single(item => item.Definition.Key == RetentionKey).Override);
    }

    [Fact]
    public async Task TriggerScheduledJob_PurgeJobRefused_UnknownRejected()
    {
        Assert.Contains("清理", await tools.TriggerScheduledJobAsync("AuditLogRetention"), StringComparison.Ordinal);
        Assert.Contains("清理", await tools.TriggerScheduledJobAsync("SoftDeletePurge"), StringComparison.Ordinal);
        Assert.Contains("找不到排程作業", await tools.TriggerScheduledJobAsync("NoSuchJob"), StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task TriggerScheduledJob_Backup_QueuedOnceAndAudited()
    {
        var first = await tools.TriggerScheduledJobAsync("系統備份");
        var second = await tools.TriggerScheduledJobAsync("SystemBackup");

        Assert.Contains("已排入", first, StringComparison.Ordinal);
        Assert.Contains("已經在", second, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Job.Trigger", entry.Action);
        Assert.Equal("job=SystemBackup; via=admin-assistant", entry.Detail);
    }

    [Fact]
    public async Task SetScheduledJobEnabled_Disable_PersistsAndAudits()
    {
        await tools.SetScheduledJobEnabledAsync("PasswordExpiryReminder", enabled: false);

        var overview = await harness.Services.GetRequiredService<ScheduledJobOverviewService>().GetOverviewAsync();
        Assert.False(overview.Items.Single(item => item.Name == "PasswordExpiryReminder").IsEnabled);
        Assert.Equal("Job.Disable", Assert.Single(audit.Entries).Action);
        Assert.Equal("job=PasswordExpiryReminder; via=admin-assistant", audit.Entries[0].Detail);
    }

    /// <summary>啟用清理類排程等於讓下一個時段永久刪除資料（停用期間累積的也一起），這一版只能停用、不能啟用。</summary>
    [Fact]
    public async Task SetScheduledJobEnabled_EnablePurgeJob_Refused_DisableAllowed()
    {
        await tools.SetScheduledJobEnabledAsync("AuditLogRetention", enabled: false);

        var text = await tools.SetScheduledJobEnabledAsync("AuditLogRetention", enabled: true);

        Assert.Contains("清理", text, StringComparison.Ordinal);
        var overview = await harness.Services.GetRequiredService<ScheduledJobOverviewService>().GetOverviewAsync();
        Assert.False(overview.Items.Single(item => item.Name == "AuditLogRetention").IsEnabled);
        Assert.Equal(["Job.Disable"], audit.Entries.Select(entry => entry.Action));
    }

    /// <summary>不能從助理立即執行或啟用的清理類排程用黑名單列出：新增排程時要在這裡決定它屬於哪一邊。</summary>
    [Fact]
    public void PurgeJobNames_CoverEveryJobExceptBackupAndReminder()
    {
        var names = harness.Services.GetRequiredService<ScheduledJobOverviewService>().Descriptors.Select(job => job.Name).ToHashSet();
        string[] allowed = ["SystemBackup", "PasswordExpiryReminder"];

        Assert.Equal(names.Except(allowed).Order(), OpsAssistantTools.PurgeJobNames.Order());
    }

    [Fact]
    public async Task CreateAnnouncement_EndDateOnly_LastsThroughThatDay()
    {
        await tools.CreateAnnouncementAsync("t", "c", "2026-11-07", "2026-11-08", null, null);

        var created = Assert.Single(await announcements.GetAllAsync());
        Assert.Equal(new DateTime(2026, 11, 7), created.StartAt);
        Assert.Equal(new DateTime(2026, 11, 8, 23, 59, 59), created.EndAt);
    }

    [Fact]
    public async Task CreateAnnouncement_RoleTarget_ResolvedAndBroadcast()
    {
        var roleId = await AddRoleAsync("一般");

        var text = await tools.CreateAnnouncementAsync("維護通知", "今晚 22:00 維護", "2026-11-07 08:00", "2026-11-08 08:00", "Role", "一般");

        var created = Assert.Single(await announcements.GetAllAsync());
        Assert.Contains(created.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        Assert.Equal(AnnouncementTargetKinds.Role, created.TargetKind);
        Assert.Equal(roleId, created.TargetId);
        Assert.Equal(new DateTime(2026, 11, 7, 8, 0, 0), created.StartAt);
        Assert.Equal("admin", created.CreatedByAccount);
        Assert.Equal(1, signal.AnnouncementsChanged);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Announcement.Create", entry.Action);
        Assert.Equal($"title=維護通知; target=Role:{roleId}; via=admin-assistant", entry.Detail);
    }

    [Fact]
    public async Task CreateAnnouncement_UnknownTargetOrBadTime_Rejected()
    {
        Assert.Contains("找不到角色", await tools.CreateAnnouncementAsync("t", "c", null, null, "Role", "不存在"), StringComparison.Ordinal);
        Assert.Contains("無法解析", await tools.CreateAnnouncementAsync("t", "c", "明天", null, null, null), StringComparison.Ordinal);
        Assert.Empty(await announcements.GetAllAsync());
        Assert.Equal(0, signal.AnnouncementsChanged);
    }

    [Fact]
    public async Task UpdateAnnouncement_ChangesOnlyGivenFields()
    {
        await tools.CreateAnnouncementAsync("舊標題", "內容不變", null, null, null, null);
        var id = (await announcements.GetAllAsync()).Single().Id;

        var text = await tools.UpdateAnnouncementAsync(id, title: "新標題", content: null, start: null, end: null);

        var updated = (await announcements.GetAllAsync()).Single();
        Assert.Contains("已更新", text, StringComparison.Ordinal);
        Assert.Equal("新標題", updated.Title);
        Assert.Equal("內容不變", updated.Content);
        Assert.Equal("Announcement.Update", audit.Entries[^1].Action);
        Assert.Equal(2, signal.AnnouncementsChanged);
        Assert.Contains($"\"id\":{id}", await tools.ListAnnouncementsAsync(), StringComparison.Ordinal);
    }

    private async Task<int> AddRoleAsync(string name)
    {
        await using var context = await harness.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
        var role = new RoleView { Name = name, TabViewJson = "[]" };
        context.RoleView.Add(role);
        await context.SaveChangesAsync();
        return role.Id;
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
