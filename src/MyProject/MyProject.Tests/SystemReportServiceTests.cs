using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Tests;

/// <summary>
/// 系統巡檢報告與容量快照的存放（0.10.39 起），以及「巡檢報告與容量快照清理」作業。
/// 重點：內容只進內容檔、篩選與分頁、同一天的排程報告只算一次、容量快照一天一份、「下次會清掉幾筆」等於實際清掉的筆數。
/// </summary>
public sealed class SystemReportServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 20, 7, 30, 0);
    private static readonly ScheduledJobContext Manual = new(42, JobRunTriggers.Manual, null, "support", 7);

    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private SqliteConnection connection = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Save_WritesJsonContentFile_AndGetReadsItBack()
    {
        var service = ReportService();
        var draft = Draft(Now, SystemReportLevels.Orange);

        var saved = await service.SaveAsync(draft);
        var detail = await service.GetAsync(draft.ReportId);

        Assert.NotNull(saved);
        Assert.Equal(SystemReportLevels.Orange, saved.Level);
        Assert.Equal(draft.ContentJson, detail!.ContentJson);
        await using var context = CreateContext();
        var row = await context.SystemReport.SingleAsync();
        Assert.Equal($"202611/{draft.ReportId:N}.json", row.ContentFile);
        Assert.True(File.Exists(Path.Combine(rootPath, AdminPatrolReportFileStore.FolderName, "202611", $"{draft.ReportId:N}.json")));
    }

    [Fact]
    public async Task Save_WithoutAContentFolder_ReturnsNull_AndWritesNoRow()
    {
        var service = ReportService(rootPath: string.Empty);

        Assert.Null(await service.SaveAsync(Draft(Now, SystemReportLevels.Green)));
        await using var context = CreateContext();
        Assert.Empty(await context.SystemReport.ToListAsync());
    }

    [Fact]
    public async Task Search_FiltersByFrequencyLevelAndDate_NewestFirst_Paged()
    {
        var service = ReportService();
        for (var day = 1; day <= 5; day++)
        {
            await service.SaveAsync(Draft(new DateTime(2026, 11, day, 7, 30, 0), day % 2 == 0 ? SystemReportLevels.Red : SystemReportLevels.Green));
        }

        await service.SaveAsync(Draft(new DateTime(2026, 11, 6, 9, 0, 0), SystemReportLevels.Red, SystemReportFrequencies.Custom));

        var red = await service.SearchAsync(new SystemReportFilter(null, SystemReportLevels.Red, null, null, 1, 20));
        var daily = await service.SearchAsync(new SystemReportFilter(SystemReportFrequencies.Daily, null, new DateTime(2026, 11, 2), new DateTime(2026, 11, 4), 1, 2));

        Assert.Equal(3, red.TotalCount);
        Assert.Equal([new DateTime(2026, 11, 6, 9, 0, 0), new DateTime(2026, 11, 4, 7, 30, 0), new DateTime(2026, 11, 2, 7, 30, 0)], red.Items.Select(x => x.CreatedAt));
        Assert.Equal(3, daily.TotalCount);
        Assert.Equal([new DateTime(2026, 11, 4, 7, 30, 0), new DateTime(2026, 11, 3, 7, 30, 0)], daily.Items.Select(x => x.CreatedAt));
    }

    [Fact]
    public async Task ScheduledReportExists_OnlyCountsScheduledAndCatchUp()
    {
        var service = ReportService();
        var day = new DateTime(2026, 11, 19);
        await service.SaveAsync(Draft(Now, SystemReportLevels.Green, trigger: SystemReportTriggers.Manual, periodStart: day));

        Assert.False(await service.ScheduledReportExistsAsync(SystemReportFrequencies.Daily, day));
        await service.SaveAsync(Draft(Now, SystemReportLevels.Green, trigger: SystemReportTriggers.CatchUp, periodStart: day));
        Assert.True(await service.ScheduledReportExistsAsync(SystemReportFrequencies.Daily, day));
        Assert.False(await service.ScheduledReportExistsAsync(SystemReportFrequencies.Daily, day.AddDays(-1)));
    }

    [Fact]
    public async Task DeliveryStatus_IsUpdated()
    {
        var service = ReportService();
        var draft = Draft(Now, SystemReportLevels.Red);
        await service.SaveAsync(draft);

        await service.UpdateDeliveryStatusAsync(draft.ReportId, SystemReportDeliveryStatuses.Sent);

        Assert.Equal(SystemReportDeliveryStatuses.Sent, (await service.GetAsync(draft.ReportId))!.Summary.DeliveryStatus);
    }

    [Fact]
    public async Task Snapshots_OneSetPerDay_SavingAgainReplacesIt()
    {
        var service = SnapshotService();
        await service.SaveDayAsync(Now.Date, Now, [Row(Now.Date, "C:\\", 10), Row(Now.Date, "D:\\", 20)]);
        await service.SaveDayAsync(Now.Date, Now.AddHours(1), [Row(Now.Date, "C:\\", 11)]);
        await service.SaveDayAsync(Now.Date.AddDays(-1), Now.AddDays(-1), [Row(Now.Date.AddDays(-1), "C:\\", 9)]);

        var history = await service.GetHistoryAsync(Now.Date.AddDays(-7));

        Assert.True(await service.HasDayAsync(Now.Date));
        Assert.Equal([(Now.Date.AddDays(-1), 9L), (Now.Date, 11L)], history.Select(x => (x.SnapshotDate, x.UsedBytes!.Value)));
    }

    [Fact]
    public async Task RetentionStats_MatchWhatThePurgeRemoves()
    {
        var reports = ReportService(retentionDays: 30);
        var snapshots = SnapshotService(retentionDays: 30);
        await reports.SaveAsync(Draft(Now.AddDays(-31), SystemReportLevels.Green));
        await reports.SaveAsync(Draft(Now.AddDays(-29), SystemReportLevels.Green));
        await snapshots.SaveDayAsync(Now.Date.AddDays(-31), Now, [Row(Now.Date.AddDays(-31), "C:\\", 1)]);
        await snapshots.SaveDayAsync(Now.Date.AddDays(-30), Now, [Row(Now.Date.AddDays(-30), "C:\\", 1)]);

        var reportStats = await reports.GetRetentionStatsAsync(Now);
        var snapshotStats = await snapshots.GetRetentionStatsAsync(Now);

        Assert.Equal((2, 1), (reportStats.Total, reportStats.Expired!.Value));
        Assert.Equal((2, 1), (snapshotStats.Total, snapshotStats.Expired!.Value));
        Assert.Equal(1, await reports.PurgeExpiredAsync(Now));
        Assert.Equal(1, await snapshots.PurgeExpiredAsync(Now));
        Assert.Equal(0, (await reports.GetRetentionStatsAsync(Now)).Expired);
    }

    [Fact]
    public async Task RetentionJob_PurgesReportsLegacyReportsAndSnapshots_AndAuditsOnlyWhenRemoved()
    {
        var audit = new RecordingAuditLogService();
        var legacy = LegacyService(retentionDays: 30);
        await legacy.SaveAsync(new AdminPatrolReportDraft(
            Guid.NewGuid(), Now.AddDays(-40), AdminPatrolStatuses.Normal, string.Empty, JobRunTriggers.Schedule, null, 1, null, false, "## 總結\n舊的。"));
        await ReportService(retentionDays: 30).SaveAsync(Draft(Now.AddDays(-40), SystemReportLevels.Green));
        await SnapshotService(retentionDays: 30).SaveDayAsync(Now.Date.AddDays(-40), Now, [Row(Now.Date.AddDays(-40), "C:\\", 1)]);
        var job = RetentionJob(audit, legacy, retentionDays: 30);

        var first = await job.ExecuteAsync(Manual, CancellationToken.None);
        var second = await job.ExecuteAsync(Manual, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal("刪除 2 份超過保留天數（日報 30 天、週報 365 天、月報 1095 天）的巡檢報告、1 筆超過 30 天的容量快照。", first.Message);
        Assert.True(second.Succeeded);
        Assert.Equal([AuditActions.AdminPatrol.AutoPurge, AuditActions.SystemReport.AutoPurge], audit.Entries.Select(x => x.Action));
        Assert.Contains("reports=1", audit.Entries[1].Detail, StringComparison.Ordinal);
        Assert.Contains("snapshots=1", audit.Entries[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetentionJob_ServiceFails_ReturnsFailure()
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE SystemReport";
            await command.ExecuteNonQueryAsync();
        }

        var job = RetentionJob(new RecordingAuditLogService(), LegacyService());

        Assert.False((await job.ExecuteAsync(Manual, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task Metrics_CountOnlyTheRange_AndReturnNoAccountsOrMessages()
    {
        var startLocal = new DateTime(2026, 11, 19);
        var endLocal = startLocal.AddDays(1);
        var startUtc = startLocal.AddHours(-8);
        var endUtc = endLocal.AddHours(-8);
        var nowUtc = new DateTime(2026, 11, 19, 23, 30, 0);
        await using (var context = CreateContext())
        {
            var admin = new MyUser { Account = "admin1", Name = "admin1", IsAdmin = true, Status = true, TwoFactorEnabled = false };
            var locked = new MyUser { Account = "locked", Name = "locked", Status = true, LockoutEndUtc = nowUtc.AddHours(1) };
            context.MyUser.AddRange(admin, locked);
            await context.SaveChangesAsync();

            context.ExceptionLog.AddRange(
                new ExceptionLog { Signature = "a", Source = ExceptionSources.Background, Message = "祕密訊息", OccurrenceCount = 1, FirstOccurredAt = startLocal.AddHours(1), LastOccurredAt = startLocal.AddHours(2) },
                new ExceptionLog { Signature = "b", Source = ExceptionSources.Browser, OccurrenceCount = 3, FirstOccurredAt = startLocal.AddDays(-5), LastOccurredAt = startLocal.AddHours(3) },
                new ExceptionLog { Signature = "c", Source = ExceptionSources.Ui, OccurrenceCount = 1, FirstOccurredAt = startLocal.AddDays(-5), LastOccurredAt = startLocal.AddDays(-4) });
            context.JobRun.AddRange(
                new JobRun { JobName = "AuditLogRetention", Status = JobRunStatuses.Failed, Trigger = "Schedule", StartedAtUtc = startUtc.AddHours(1) },
                new JobRun { JobName = "AuditLogRetention", Status = JobRunStatuses.Failed, Trigger = "Schedule", StartedAtUtc = startUtc.AddDays(-1) },
                new JobRun { JobName = "SystemBackup", Status = JobRunStatuses.Interrupted, Trigger = "Schedule", StartedAtUtc = startUtc.AddHours(2) });
            context.EmailOutbox.AddRange(
                new EmailOutbox { Kind = "Notification", To = "a@example.com", Subject = "s", Status = EmailOutboxStatus.Failed, CreatedAtUtc = startUtc.AddHours(1) },
                new EmailOutbox { Kind = "Notification", To = "b@example.com", Subject = "s", Status = EmailOutboxStatus.Sent, CreatedAtUtc = startUtc.AddHours(1) },
                new EmailOutbox { Kind = "Notification", To = "c@example.com", Subject = "s", Status = EmailOutboxStatus.Pending, CreatedAtUtc = nowUtc.AddDays(-3) });
            context.TokenUsageLog.AddRange(
                new TokenUsageLog { Operation = "x", Model = "m", Success = true, CostTwd = 1.5, OccurredAt = startLocal.AddHours(1) },
                new TokenUsageLog { Operation = "x", Model = "m", Success = true, CostTwd = null, OccurredAt = startLocal.AddHours(1) },
                new TokenUsageLog { Operation = "x", Model = "m", Success = false, OccurredAt = startLocal.AddHours(1) },
                new TokenUsageLog { Operation = "x", Model = "m", Success = true, CostTwd = 99, OccurredAt = startLocal.AddDays(-2) });
            context.AuditLog.AddRange(
                new AuditLog { Action = AuditActions.Login.Failed, ActorAccount = "secret-account", OccurredAt = startUtc.AddHours(1) },
                new AuditLog { Action = AuditActions.Token.ReuseDetected, OccurredAt = startUtc.AddHours(1) },
                new AuditLog { Action = AuditActions.Login.Failed, OccurredAt = startUtc.AddDays(-1) });
            context.Notification.AddRange(
                new Notification { RecipientUserId = admin.Id, Category = NotificationCategories.LoginFailureBurst, Title = "爆量 A", CreatedAtUtc = startUtc.AddHours(1) },
                new Notification { RecipientUserId = locked.Id, Category = NotificationCategories.LoginFailureBurst, Title = "爆量 A", CreatedAtUtc = startUtc.AddHours(1) });
            await context.SaveChangesAsync();
        }

        var service = new SystemReportMetricsService(new TestDbContextFactory(connection), NullLogger<SystemReportMetricsService>.Instance);
        var metrics = await service.GetAsync(new SystemReportMetricsRange(startLocal, endLocal, startUtc, endUtc, nowUtc));

        Assert.Equal((1, 1), (metrics.NewExceptionSignatures, metrics.NewExceptionsSevere));
        Assert.Equal(2, metrics.ActiveExceptionSignaturesBySource.Values.Sum());
        Assert.Equal(1, metrics.FailedJobRuns["AuditLogRetention"]);
        Assert.Equal(1, metrics.InterruptedJobRuns);
        Assert.Equal((2, 1, 1), (metrics.EmailsCreated, metrics.EmailsFailed, metrics.EmailsStuckPending));
        Assert.Equal((3, 1, 1), (metrics.AiCalls, metrics.AiFailedCalls, metrics.AiUnpricedCalls));
        Assert.Equal(1.5, metrics.AiCostTwd, 3);
        Assert.Equal((1, 1, 0), (metrics.SecurityEvents[AuditActions.Login.Failed], metrics.SecurityEvents[AuditActions.Token.ReuseDetected], metrics.SecurityEvents[AuditActions.Permission.Denied]));
        Assert.Equal(1, metrics.LoginFailureBursts);
        Assert.Equal((1, 1, 1), (metrics.LockedAccounts, metrics.EnabledAdmins, metrics.AdminsWithoutTwoFactor));
    }

    // ───────────── 月報（0.10.41 起）─────────────

    [Fact]
    public async Task AiCost_GroupsByFeatureModelAndUser_AndComparesWithTheMonthBefore()
    {
        var start = new DateTime(2026, 10, 1);
        int aliceId;
        await using (var context = CreateContext())
        {
            var alice = new MyUser { Account = "alice", Name = "愛麗絲", Status = true };
            context.MyUser.Add(alice);
            await context.SaveChangesAsync();
            aliceId = alice.Id;
            context.TokenUsageLog.AddRange(
                new TokenUsageLog { Operation = "例外分析", Model = "gpt-a", UserId = aliceId, Account = "alice", Success = true, CostTwd = 3, OccurredAt = start.AddDays(2) },
                new TokenUsageLog { Operation = "例外分析", Model = "gpt-b", UserId = aliceId, Account = "alice", Success = true, CostTwd = 1, OccurredAt = start.AddDays(3) },
                new TokenUsageLog { Operation = "系統巡檢報告（AI 說明）", Model = "gpt-a", Success = true, CostTwd = null, OccurredAt = start.AddDays(4) },
                new TokenUsageLog { Operation = "例外分析", Model = "gpt-a", UserId = aliceId, Account = "alice", Success = false, OccurredAt = start.AddDays(5) },
                new TokenUsageLog { Operation = "例外分析", Model = "gpt-a", UserId = aliceId, Account = "alice", Success = true, CostTwd = 2, OccurredAt = start.AddDays(-3) },
                new TokenUsageLog { Operation = "例外分析", Model = "gpt-a", UserId = aliceId, Account = "alice", Success = true, CostTwd = 50, OccurredAt = start.AddMonths(1) });
            await context.SaveChangesAsync();
        }

        var cost = await Metrics().GetAiCostAsync(start, start.AddMonths(1), start.AddMonths(-1));

        Assert.Equal((4.0, 2.0, 4, 1, 1, 1, 1), (cost.CostTwd, cost.PreviousCostTwd, cost.Calls, cost.PreviousCalls, cost.FailedCalls, cost.UnpricedCalls, cost.UserCount));
        Assert.Equal(("例外分析", 3, 4.0, 2.0), (cost.ByOperation[0].Name, cost.ByOperation[0].Calls, cost.ByOperation[0].CostTwd, cost.ByOperation[0].PreviousCostTwd));
        Assert.Equal(["gpt-a", "gpt-b"], cost.ByModel.Select(x => x.Name));
        Assert.Equal(("alice", "愛麗絲", 3, 4.0), (cost.TopUsers[0].Account, cost.TopUsers[0].Name, cost.TopUsers[0].Calls, cost.TopUsers[0].CostTwd));
        Assert.Null(cost.TopUsers[1].Account);
    }

    [Fact]
    public async Task AccountReview_ListsAdminsStaleAccountsMissingTwoFactor_AndTheQuartersChanges()
    {
        var nowUtc = new DateTime(2027, 1, 1, 0, 30, 0);
        var nowLocal = new DateTime(2027, 1, 1, 8, 30, 0);
        var quarterStartUtc = new DateTime(2026, 9, 30, 16, 0, 0);
        var endUtc = new DateTime(2026, 12, 31, 16, 0, 0);
        await using (var context = CreateContext())
        {
            var admin = new MyUser { Account = "admin1", Name = "管理員一", IsAdmin = true, Status = true, TwoFactorEnabled = true, CreateAt = nowLocal.AddYears(-1) };
            var stale = new MyUser { Account = "stale", Name = "很久沒來", Status = true, CreateAt = nowLocal.AddYears(-1) };
            var fresh = new MyUser { Account = "fresh", Name = "剛建立", Status = true, CreateAt = nowLocal.AddDays(-3) };
            var google = new MyUser { Account = "g", Name = "Google", Status = true, OAuthProvider = "Google", CreateAt = nowLocal.AddYears(-1) };
            var disabled = new MyUser { Account = "off", Name = "停用", Status = false, CreateAt = nowLocal.AddYears(-1) };
            var role = new RoleView { Name = "業務" };
            context.AddRange(admin, stale, fresh, google, disabled, role);
            await context.SaveChangesAsync();
            context.AuditLog.AddRange(
                new AuditLog { Action = AuditActions.Login.Success, ActorUserId = admin.Id, OccurredAt = nowUtc.AddDays(-2) },
                new AuditLog { Action = AuditActions.Login.Success, ActorUserId = stale.Id, OccurredAt = nowUtc.AddDays(-200) },
                new AuditLog { Action = AuditActions.Login.SsoSuccess, ActorUserId = google.Id, OccurredAt = nowUtc.AddDays(-1) },
                new AuditLog { Action = AuditActions.User.Update, ActorAccount = "admin1", TargetType = nameof(MyUser), TargetId = stale.Id.ToString(), OccurredAt = quarterStartUtc.AddDays(10) },
                new AuditLog { Action = AuditActions.Role.Update, ActorAccount = "admin1", TargetType = nameof(RoleView), TargetId = role.Id.ToString(), OccurredAt = quarterStartUtc.AddDays(20) },
                new AuditLog { Action = AuditActions.User.Disable, ActorAccount = "admin1", TargetType = nameof(MyUser), TargetId = "9999", OccurredAt = quarterStartUtc.AddDays(30) },
                new AuditLog { Action = AuditActions.User.Update, ActorAccount = "admin1", TargetType = nameof(MyUser), TargetId = stale.Id.ToString(), OccurredAt = quarterStartUtc.AddDays(-1) },
                new AuditLog { Action = AuditActions.User.Update, Success = false, ActorAccount = "admin1", TargetType = nameof(MyUser), TargetId = stale.Id.ToString(), OccurredAt = quarterStartUtc.AddDays(11) });
            await context.SaveChangesAsync();
        }

        var review = await Metrics().GetAccountReviewAsync(quarterStartUtc, endUtc, nowUtc, nowLocal, 90);

        Assert.Equal((4, 1), (review.EnabledAccounts, review.DisabledAccounts));
        Assert.Equal("admin1", Assert.Single(review.Admins).Account);
        Assert.Equal("stale", Assert.Single(review.StaleAccounts).Account);
        Assert.Equal(["fresh", "stale"], review.WithoutTwoFactor.Select(x => x.Account));
        Assert.Equal(nowUtc.AddDays(-2), review.Admins[0].LastLoginUtc);
        Assert.Equal(["使用者 stale", "角色 業務", "使用者（編號 9999）"], review.Changes.Select(x => x.Target));
        Assert.Equal([AuditActions.User.Update, AuditActions.Role.Update, AuditActions.User.Disable], review.Changes.Select(x => x.Action));
    }

    private SystemReportMetricsService Metrics() => new(new TestDbContextFactory(connection), NullLogger<SystemReportMetricsService>.Instance);

    [Fact]
    public async Task Retention_IsPerFrequency()
    {
        var service = new SystemReportService(new TestDbContextFactory(connection), FileStore(),
            new StaticOptionsMonitor<SystemReportSettings>(new SystemReportSettings { DailyRetentionDays = 10, WeeklyRetentionDays = 100, MonthlyRetentionDays = 1000 }),
            NullLogger<SystemReportService>.Instance);
        foreach (var (frequency, age) in new[] { (SystemReportFrequencies.Daily, 11), (SystemReportFrequencies.Weekly, 11), (SystemReportFrequencies.Weekly, 101), (SystemReportFrequencies.Monthly, 101), (SystemReportFrequencies.Custom, 11) })
        {
            Assert.NotNull(await service.SaveAsync(Draft(Now.AddDays(-age), SystemReportLevels.Green) with { Frequency = frequency }));
        }

        Assert.Equal(3, (await service.GetRetentionStatsAsync(Now)).Expired);
        Assert.Equal(3, await service.PurgeExpiredAsync(Now));
        await using var context = CreateContext();
        Assert.Equal([SystemReportFrequencies.Monthly, SystemReportFrequencies.Weekly], (await context.SystemReport.Select(x => x.Frequency).ToListAsync()).Order());
    }

    private BackendDBContext CreateContext() => new TestDbContextFactory(connection).CreateDbContext();

    private static StaticOptionsMonitor<SystemReportSettings> Settings(int retentionDays)
        => new(new SystemReportSettings { DailyRetentionDays = retentionDays, CapacitySnapshotRetentionDays = Math.Max(30, retentionDays) });

    private AdminPatrolReportFileStore FileStore(string? root = null)
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiAgentConversationPath = root ?? rootPath;
        return new AdminPatrolReportFileStore(Options.Create(settings), NullLogger<AdminPatrolReportFileStore>.Instance);
    }

    private SystemReportService ReportService(int retentionDays = 90, string? rootPath = null)
        => new(new TestDbContextFactory(connection), FileStore(rootPath), Settings(retentionDays), NullLogger<SystemReportService>.Instance);

    private AdminPatrolReportService LegacyService(int retentionDays = 90)
        => new(new TestDbContextFactory(connection), FileStore(), Settings(retentionDays), NullLogger<AdminPatrolReportService>.Instance);

    private CapacitySnapshotService SnapshotService(int retentionDays = 730)
        => new(new TestDbContextFactory(connection), Settings(retentionDays), NullLogger<CapacitySnapshotService>.Instance);

    private AdminPatrolReportRetentionJob RetentionJob(RecordingAuditLogService audit, AdminPatrolReportService legacy, int retentionDays = 90)
        => new(legacy, ReportService(retentionDays), SnapshotService(retentionDays), audit,
            new ManualTimeProvider(new DateTimeOffset(Now, TimeSpan.FromHours(8))), NullLogger<AdminPatrolReportRetentionJob>.Instance);

    private static CapacitySnapshotRow Row(DateTime date, string key, long used)
        => new(date, CapacitySnapshotKinds.Volume, key, used, 100, 100 - used, null, CapacitySnapshotKinds.StatusOk);

    private static SystemReportDraft Draft(
        DateTime createdAt,
        string level,
        string frequency = SystemReportFrequencies.Daily,
        string trigger = SystemReportTriggers.Schedule,
        DateTime? periodStart = null)
    {
        var start = periodStart ?? createdAt.Date.AddDays(-1);
        var id = Guid.NewGuid();
        return new SystemReportDraft(id, frequency, start, start.AddDays(1), createdAt, level, 0, 0, 0, trigger, null, 1,
            SystemReportNarrativeStatuses.Skipped, null, SystemReportDeliveryStatuses.NotRequired, $"{{\"reportId\":\"{id}\"}}");
    }
}
