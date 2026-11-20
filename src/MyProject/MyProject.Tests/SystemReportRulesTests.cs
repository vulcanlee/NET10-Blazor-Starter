using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Backup;
using MyProject.Web.Health;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;
using MyProject.Web.SystemReports;

namespace MyProject.Tests;

/// <summary>
/// 系統巡檢報告的規則（0.10.39 起）：燈號只由這些純函式判定。每條規則測門檻的兩側，
/// 並守住「報告文字不帶路徑、例外內文、帳號」與「通知內文不放 AI 說明」。
/// </summary>
public sealed class SystemReportRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 11, 20, 7, 30, 0);
    private static readonly SystemReportSettings Settings = new();

    // ───────────── 容量 ─────────────

    [Theory]
    [InlineData(4.9, SystemReportLevels.Red)]
    [InlineData(5.0, SystemReportLevels.Orange)]
    [InlineData(14.9, SystemReportLevels.Orange)]
    [InlineData(15.0, null)]
    public void Capacity_FreePercent_Thresholds(double freePercent, string? expected)
    {
        var total = 1000 * Gb;
        var facts = Facts(volumes: [Volume("D:\\", total, (long)(total * freePercent / 100), Forecast.None)]);

        var finding = Rules(facts).SingleOrDefault(x => x.RuleId == "CAP-VOLUME");

        Assert.Equal(expected, finding?.Level);
    }

    [Fact]
    public void Capacity_FreeGb_IsRedEvenWhenThePercentLooksFine()
    {
        var facts = Facts(volumes: [Volume("D:\\", 30 * Gb, (long)(1.9 * Gb), Forecast.None)]);

        Assert.Equal(SystemReportLevels.Red, Rules(facts).Single(x => x.RuleId == "CAP-VOLUME").Level);
    }

    [Theory]
    [InlineData(7, SystemReportLevels.Red)]
    [InlineData(8, SystemReportLevels.Orange)]
    [InlineData(30, SystemReportLevels.Orange)]
    [InlineData(31, SystemReportLevels.Yellow)]
    [InlineData(90, SystemReportLevels.Yellow)]
    [InlineData(91, null)]
    public void Capacity_Forecast_Thresholds_WithDeadline(int daysToFull, string? expected)
    {
        var facts = Facts(volumes: [Volume("D:\\", 1000 * Gb, 500 * Gb, new CapacityForecastResult(30, Gb, daysToFull))]);

        var finding = Rules(facts).SingleOrDefault(x => x.RuleId == "CAP-VOLUME");

        Assert.Equal(expected, finding?.Level);
        if (finding is not null)
        {
            Assert.Equal(Now.Date.AddDays(daysToFull), finding.Deadline);
            Assert.Contains($"{daysToFull} 天後", finding.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Capacity_NotEnoughSnapshots_OnlyLooksAtFreeSpace()
    {
        var facts = Facts(volumes: [Volume("D:\\", 1000 * Gb, 500 * Gb, new CapacityForecastResult(3, null, null))]);

        Assert.DoesNotContain(Rules(facts), x => x.RuleId == "CAP-VOLUME");
    }

    [Fact]
    public void Capacity_ThresholdsComeFromSettings()
    {
        var facts = Facts(volumes: [Volume("D:\\", 1000 * Gb, 500 * Gb, Forecast.None)]);

        var finding = Rules(facts, new SystemReportSettings { CapacityRedFreePercent = 10, CapacityOrangeFreePercent = 60 }).Single(x => x.RuleId == "CAP-VOLUME");

        Assert.Equal(SystemReportLevels.Orange, finding.Level);
    }

    [Fact]
    public void Capacity_LargeWal_IsYellow()
    {
        var facts = Facts(capacity: Capacity(database: new CapacityDatabaseUsage("C:\\", 100 * 1024 * 1024, 300L * 1024 * 1024, 0)));

        Assert.Equal(SystemReportLevels.Yellow, Rules(facts).Single(x => x.RuleId == "CAP-WAL").Level);
    }

    // ───────────── 備份 ─────────────

    [Fact]
    public void Backup_None_IsRed()
    {
        Assert.Equal(SystemReportLevels.Red, Rules(Facts(backups: [])).Single(x => x.RuleId == "BKP-NONE").Level);
    }

    [Theory]
    [InlineData(25, null)]
    [InlineData(27, SystemReportLevels.Yellow)]
    [InlineData(49, SystemReportLevels.Yellow)]
    [InlineData(51, SystemReportLevels.Red)]
    public void Backup_Age_ComparedWithTheBackupInterval(int hoursOld, string? expected)
    {
        // 每天備份一次：超過 1 個週期＋2 小時黃燈、2 個週期＋2 小時紅燈。
        var facts = Facts(backups: [Backup(Now.AddHours(-hoursOld))], backupInterval: TimeSpan.FromDays(1));

        Assert.Equal(expected, Rules(facts).SingleOrDefault(x => x.RuleId == "BKP-AGE")?.Level);
    }

    [Fact]
    public void Backup_JobDisabled_IsOrange_WithTheDateTheBackupGoesStale()
    {
        var newest = Now.AddHours(-3);
        var facts = Facts(backups: [Backup(newest)], jobs: Jobs(Job(SystemBackupJob.JobName, enabled: false)));

        var finding = Rules(facts).Single(x => x.RuleId == "BKP-DISABLED");

        Assert.Equal(SystemReportLevels.Orange, finding.Level);
        Assert.Equal(newest.AddDays(1).Date, finding.Deadline);
        Assert.DoesNotContain(Rules(facts), x => x.RuleId == "JOB-DISABLED");
    }

    [Fact]
    public void Backup_DiskCannotFitTheNextBackup_IsOrange()
    {
        var capacity = Capacity(
            folders: [new CapacityFolderUsage("BackupPath", "系統備份", "E:\\", 10 * Gb, 7, CapacitySnapshotKinds.StatusOk)],
            database: new CapacityDatabaseUsage("C:\\", 2 * Gb, 0, 0));
        var facts = Facts(
            backups: [Backup(Now.AddHours(-3), size: 2 * Gb)],
            capacity: capacity,
            volumes: [Volume("E:\\", 1000 * Gb, 3 * Gb, Forecast.None)]);

        Assert.Equal(SystemReportLevels.Orange, Rules(facts).Single(x => x.RuleId == "BKP-SPACE").Level);
    }

    [Fact]
    public void Backup_OnTheSameDiskAsTheDatabase_IsYellow()
    {
        var capacity = Capacity(
            folders: [new CapacityFolderUsage("BackupPath", "系統備份", "C:\\", Gb, 1, CapacitySnapshotKinds.StatusOk)],
            database: new CapacityDatabaseUsage("C:\\", Gb, 0, 0));

        Assert.Equal(SystemReportLevels.Yellow, Rules(Facts(capacity: capacity)).Single(x => x.RuleId == "BKP-SAMEVOL").Level);
    }

    // ───────────── 錯誤、排程、寄信、AI、安全 ─────────────

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, SystemReportLevels.Yellow)]
    [InlineData(99, SystemReportLevels.Yellow)]
    [InlineData(100, SystemReportLevels.Red)]
    public void Errors_LogCount_Thresholds(int errors, string? expected)
    {
        Assert.Equal(expected, Rules(Facts(errorLogs: errors)).SingleOrDefault(x => x.RuleId == "ERR-LOG")?.Level);
    }

    [Fact]
    public void Errors_NewSevereException_IsRed()
    {
        var facts = Facts(metrics: Metrics(m => m with { NewExceptionSignatures = 2, NewExceptionsSevere = 1 }));

        Assert.Equal(SystemReportLevels.Red, Rules(facts).Single(x => x.RuleId == "ERR-EXC-NEW").Level);
    }

    [Fact]
    public void Jobs_LastRunFailed_IsRed_AndRecoveredFailure_IsYellow()
    {
        var jobs = Jobs(
            Job("AuditLogRetention", lastFinished: JobRunStatuses.Failed),
            Job("TokenUsageLogRetention", lastFinished: JobRunStatuses.Succeeded));
        var facts = Facts(jobs: jobs, metrics: Metrics(m => m with { FailedJobRuns = new Dictionary<string, int> { ["AuditLogRetention"] = 1, ["TokenUsageLogRetention"] = 2 } }));

        var findings = Rules(facts);

        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "JOB-FAILED").Level);
        var recovered = findings.Single(x => x.RuleId == "JOB-RECOVERED");
        Assert.Contains("TokenUsageLogRetention", recovered.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditLogRetention", recovered.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Jobs_MasterSwitchOff_IsRed_AndDisabledSystemReportJobsAreNotNagged()
    {
        var jobs = Jobs(Job(SystemReportDailyJob.JobName, enabled: false)) with { SchedulingEnabled = false };

        var findings = Rules(Facts(jobs: jobs));

        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "JOB-MASTER").Level);
        Assert.DoesNotContain(findings, x => x.RuleId == "JOB-DISABLED");
    }

    [Fact]
    public void AuditIntegrityFailure_IsASecurityFinding_NotAJobFinding()
    {
        var findings = Rules(Facts(jobs: Jobs(Job(AuditIntegrityVerifyJob.JobName, lastFinished: JobRunStatuses.Failed))));

        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "SEC-INTEGRITY").Level);
        Assert.DoesNotContain(findings, x => x.RuleId == "JOB-FAILED");
    }

    [Theory]
    [InlineData(10, 3, SystemReportLevels.Red)]
    [InlineData(4, 1, SystemReportLevels.Red)]
    [InlineData(10, 2, SystemReportLevels.Yellow)]
    [InlineData(10, 0, null)]
    public void Email_Failures(int created, int failed, string? expected)
    {
        var facts = Facts(metrics: Metrics(m => m with { EmailsCreated = created, EmailsFailed = failed }));

        Assert.Equal(expected, Rules(facts).SingleOrDefault(x => x.RuleId == "MAIL-FAILED")?.Level);
    }

    [Fact]
    public void Email_ProviderNone_IsYellow_AndStuckMailIsRed()
    {
        Assert.Equal(SystemReportLevels.Yellow, Rules(Facts(emailProvider: "None")).Single(x => x.RuleId == "MAIL-OFF").Level);
        Assert.Equal(SystemReportLevels.Red, Rules(Facts(metrics: Metrics(m => m with { EmailsStuckPending = 1 }))).Single(x => x.RuleId == "MAIL-STUCK").Level);
    }

    [Fact]
    public void Ai_RulesOnlyApplyWhenAiIsConfigured()
    {
        var metrics = Metrics(m => m with { AiCalls = 10, AiFailedCalls = 9 });

        Assert.DoesNotContain(Rules(Facts(metrics: metrics, aiConfigured: false)), x => x.RuleId == "AI-FAIL");
        Assert.Equal(SystemReportLevels.Red, Rules(Facts(metrics: metrics, aiConfigured: true)).Single(x => x.RuleId == "AI-FAIL").Level);
    }

    [Fact]
    public void Ai_QuotaProjectedToRunOutBeforeMonthEnd_IsOrange_WithTheDate()
    {
        // 11/1 起 19.3 天用了 NT$ 800，上限 1000：照速度約 11/25 用完。
        var quota = new AiQuotaLimitStatus(AiQuotaScope.Global, AiQuotaPeriod.Monthly, 1000, 800, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));

        var finding = Rules(Facts(aiConfigured: true, quota: [quota])).Single(x => x.RuleId == "AI-QUOTA");

        Assert.Equal(SystemReportLevels.Orange, finding.Level);
        Assert.Equal(new DateTime(2026, 11, 25), finding.Deadline);
    }

    [Fact]
    public void Security_TokenReuseAndBursts_AreRed_LockedAndNo2fa_AreYellow()
    {
        var metrics = Metrics(m => m with
        {
            SecurityEvents = new Dictionary<string, int> { [AuditActions.Token.ReuseDetected] = 1 },
            LoginFailureBursts = 1,
            LockedAccounts = 2,
            AdminsWithoutTwoFactor = 1,
            EnabledAdmins = 3,
        });

        var findings = Rules(Facts(metrics: metrics));

        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "SEC-REUSE").Level);
        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "SEC-BURST").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "SEC-LOCKED").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "SEC-ADMIN2FA").Level);
    }

    [Fact]
    public void Health_UsesOnlyItsStatus_NeverTheEvidence()
    {
        var item = new SystemHealthItem
        {
            Name = "檔案系統",
            Category = "FileSystem",
            Status = SystemHealthStatus.Unhealthy,
            Evidence = "D:\\Secret\\Backup 無法寫入；SigningKey=abc",
            FailureMessage = "D:\\Secret\\Backup 無法寫入",
        };

        var finding = Rules(Facts(health: [item])).Single(x => x.RuleId == "HLT-FILESYSTEM");

        Assert.Equal(SystemReportLevels.Red, finding.Level);
        Assert.DoesNotContain("Secret", Text(finding), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFailure_BecomesAYellowFinding()
    {
        var facts = Facts();
        facts.Failures.Add(new SystemReportSourceFailure(SystemReportSections.Backup, "備份清單"));

        var finding = Rules(facts).Single(x => x.RuleId == "SYS-SOURCE");

        Assert.Equal((SystemReportLevels.Yellow, SystemReportSections.Backup), (finding.Level, finding.Section));
    }

    [Fact]
    public void Findings_AreOrderedBySeverity_AndEveryFindingExplainsWhatToDo()
    {
        var facts = Facts(errorLogs: 5, emailProvider: "None", backups: []);

        var findings = Rules(facts);

        Assert.Equal(findings.OrderByDescending(x => SystemReportLevels.Rank(x.Level)).Select(x => x.RuleId), findings.Select(x => x.RuleId));
        Assert.All(findings, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Title));
            Assert.False(string.IsNullOrWhiteSpace(x.Detail));
            Assert.False(string.IsNullOrWhiteSpace(x.IfIgnored));
            Assert.False(string.IsNullOrWhiteSpace(x.HowToFix));
            Assert.False(string.IsNullOrWhiteSpace(x.AfterFix));
            Assert.NotEqual(SystemReportLevels.Green, x.Level);
        });
    }

    [Fact]
    public void Jobs_SettingsErrorOverdueDisabledAndInterrupted()
    {
        var overdue = Job("AuditLogRetention") with { OverdueSlotUtc = Now.ToUniversalTime().AddHours(-3) };
        var interruptedRun = new JobRunAdapterModel { JobName = "NotificationRetention", Status = JobRunStatuses.Interrupted };
        var interrupted = Job("NotificationRetention") with { LastRun = interruptedRun };
        var jobs = Jobs(overdue, interrupted, Job("EmailLogRetention", enabled: false)) with { SettingsError = "Cron 錯誤" };

        var findings = Rules(Facts(jobs: jobs));

        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "JOB-SETTINGS").Level);
        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "JOB-OVERDUE").Level);
        Assert.Contains("EmailLogRetention", findings.Single(x => x.RuleId == "JOB-DISABLED").Detail, StringComparison.Ordinal);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "JOB-INTERRUPTED").Level);
    }

    [Fact]
    public void Health_DegradedIsYellow_AndYellowOnlyItemsNeverGoRed()
    {
        SystemHealthItem Item(string category, SystemHealthStatus status)
            => new() { Name = category, Category = category, Status = status, Evidence = string.Empty };

        var findings = Rules(Facts(health:
        [
            Item("Database", SystemHealthStatus.Degraded),
            Item("Security", SystemHealthStatus.Unhealthy),
            Item("Host", SystemHealthStatus.Unhealthy),
            Item("Cache", SystemHealthStatus.Healthy),
        ]));

        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "HLT-DATABASE").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "HLT-SECURITY").Level);
        Assert.DoesNotContain(findings, x => x.RuleId is "HLT-HOST" or "HLT-CACHE");
    }

    [Fact]
    public void Errors_BrowserSignatures_AndCapacityFreelist()
    {
        var metrics = new SystemReportMetrics { ActiveExceptionSignaturesBySource = new Dictionary<string, int> { [ExceptionSources.Browser] = 5 } };
        var capacity = Capacity(database: new CapacityDatabaseUsage("C:\\", 1000L * 1024 * 1024, 0, 600L * 1024 * 1024));

        var findings = Rules(Facts(metrics: metrics, capacity: capacity));

        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "ERR-CLIENT").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "CAP-FREELIST").Level);
    }

    [Fact]
    public void Ai_BlockedUnpricedAndQuota()
    {
        var metrics = new SystemReportMetrics
        {
            AiCalls = 100,
            AiFailedCalls = 10,
            AiUnpricedCalls = 3,
            SecurityEvents = new Dictionary<string, int> { [AuditActions.Ai.QuotaBlocked] = 2 },
        };
        var reached = new AiQuotaLimitStatus(AiQuotaScope.Global, AiQuotaPeriod.Monthly, 1000, 1000, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1));
        // 週期明天結束、用了 81%：照速度不會超過，只是黃燈。
        var nearEnd = new AiQuotaLimitStatus(AiQuotaScope.Global, AiQuotaPeriod.Monthly, 10000, 8100, new DateTime(2026, 10, 22), new DateTime(2026, 11, 21));

        var findings = Rules(Facts(metrics: metrics, aiConfigured: true, quota: [reached]));
        var warning = Rules(Facts(aiConfigured: true, quota: [nearEnd]));

        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "AI-FAIL").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "AI-BLOCKED").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "AI-UNPRICED").Level);
        Assert.Equal(SystemReportLevels.Red, findings.Single(x => x.RuleId == "AI-QUOTA").Level);
        Assert.Equal(SystemReportLevels.Yellow, warning.Single(x => x.RuleId == "AI-QUOTA").Level);
    }

    [Fact]
    public void Security_ManyLoginFailuresAndDenials_AreYellow()
    {
        var metrics = Metrics(m => m with
        {
            SecurityEvents = new Dictionary<string, int>
            {
                [AuditActions.Login.Failed] = 15,
                [AuditActions.Login.TwoFactorFailed] = 5,
                [AuditActions.Permission.Denied] = 20,
            },
        });

        var findings = Rules(Facts(metrics: metrics));

        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "SEC-LOGINFAIL").Level);
        Assert.Equal(SystemReportLevels.Yellow, findings.Single(x => x.RuleId == "SEC-DENIED").Level);
    }

    // ───────────── 摘要、通知與 AI 輸入 ─────────────

    [Fact]
    public void Summary_OverallIsTheWorstFinding_WithExpansionAndPredictions()
    {
        var facts = Facts(volumes: [Volume("D:\\", 1000 * Gb, 400 * Gb, new CapacityForecastResult(30, 2 * Gb, 200))], backups: []);
        var findings = Rules(facts);

        var content = SystemReportSummarizer.Build(Guid.NewGuid(), facts, findings, "測試系統", "0.10.39");

        Assert.Equal(SystemReportLevels.Red, content.Level);
        Assert.StartsWith("有 1 個已發生的問題", content.Verdict, StringComparison.Ordinal);
        var expansion = Assert.Single(content.Expansions);
        Assert.Equal(Now.Date.AddDays(200), expansion.FullDate);
        Assert.Equal((long)(2 * Gb * 365 * 1.15), expansion.SuggestedBytes);
        Assert.Contains(content.Predictions, x => x.Contains($"約 {Now.Date.AddDays(200):yyyy/MM/dd} 用完", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_AllGreen()
    {
        var facts = Facts();

        var content = SystemReportSummarizer.Build(Guid.NewGuid(), facts, Rules(facts), "測試系統", "0.10.39");

        Assert.Equal(SystemReportLevels.Green, content.Level);
        Assert.Equal("系統正常，沒有需要處理的事項。", content.Verdict);
        Assert.Contains("照目前的狀況，下一期應該維持正常。", content.Predictions);
    }

    [Fact]
    public void Notification_HasTitlePeriodLevel_AndNeverTheAiNarrative()
    {
        var facts = Facts(backups: []);
        var content = SystemReportSummarizer.Build(Guid.NewGuid(), facts, Rules(facts), "測試系統", "0.10.39");
        content.Narrative = "## 整體狀況\nAI 寫的祕密句子";

        Assert.Equal("系統巡檢日報 2026/11/19：紅燈", SystemReportDelivery.Title(content));
        var body = SystemReportDelivery.Body(content);
        Assert.Contains("【紅燈】", body, StringComparison.Ordinal);
        Assert.Contains("沒有任何系統備份", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AI 寫的祕密句子", body, StringComparison.Ordinal);
        Assert.True(body.Length <= SystemReportDelivery.MaxBodyLength);
        Assert.Equal($"/system-reports?report={content.ReportId:N}", SystemReportDelivery.Link(content.ReportId));
    }

    [Fact]
    public void NarratorInput_IsDeIdentified_AndTrimmedFromTheLeastSevere()
    {
        var facts = Facts(backups: []);
        var content = SystemReportSummarizer.Build(Guid.NewGuid(), facts, Rules(facts), "測試系統", "0.10.39");
        for (var i = 0; i < 400; i++)
        {
            content.Findings.Add(new SystemReportFindingDto { RuleId = "X", Level = SystemReportLevels.Yellow, Title = $"黃燈 {i}", Detail = new string('字', 40) });
        }

        var message = SystemReportNarrator.BuildUserMessage(content);

        Assert.True(message.Length <= SystemReportNarrator.MaxInputChars);
        Assert.Contains("沒有任何系統備份", message, StringComparison.Ordinal);
        Assert.DoesNotContain("黃燈 399", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ifIgnored", message, StringComparison.Ordinal);
        Assert.DoesNotContain(content.ReportId.ToString(), message, StringComparison.Ordinal);
        Assert.Contains("\"level\":\"紅燈\"", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Red\"", message, StringComparison.Ordinal);
    }

    // ───────────── 測試資料 ─────────────

    private enum Forecast
    {
        None,
    }

    private static List<SystemReportFindingDto> Rules(SystemReportFacts facts, SystemReportSettings? settings = null)
        => SystemReportRules.Evaluate(facts, settings ?? Settings);

    private static string Text(SystemReportFindingDto finding)
        => string.Join("|", finding.Title, finding.Detail, finding.IfIgnored, finding.HowToFix, finding.AfterFix);

    private static SystemReportVolumeFact Volume(string root, long total, long free, Forecast _)
        => Volume(root, total, free, new CapacityForecastResult(30, 0, null));

    private static SystemReportVolumeFact Volume(string root, long total, long free, CapacityForecastResult forecast)
        => new(new CapacityVolumeUsage(root, total, free, ["資料庫"]), forecast);

    private static CapacityProbeResult Capacity(IReadOnlyList<CapacityFolderUsage>? folders = null, CapacityDatabaseUsage? database = null)
        => new() { Volumes = [], Folders = folders ?? [], Database = database };

    private static BackupFileInfo Backup(DateTime createdAt, long size = 1024)
        => new($"backup-{createdAt:yyyyMMdd-HHmmss}.zip", size, createdAt, null);

    private static SystemReportMetrics Metrics(Func<MetricsBuilder, MetricsBuilder> configure)
        => configure(new MetricsBuilder()).Build();

    private static ScheduledJobOverviewItem Job(string name, bool enabled = true, string? lastFinished = null)
    {
        var run = lastFinished is null ? null : new JobRunAdapterModel { JobName = name, Status = lastFinished };
        return new ScheduledJobOverviewItem(name, name, string.Empty, "0 3 * * *", enabled, null, run, run, false, null);
    }

    /// <summary>排程總覽：預設有一個啟用中的系統備份，傳入同名的作業就取代它。</summary>
    private static ScheduledJobOverview Jobs(params ScheduledJobOverviewItem[] items)
    {
        var byName = new Dictionary<string, ScheduledJobOverviewItem> { [SystemBackupJob.JobName] = Job(SystemBackupJob.JobName) };
        foreach (var item in items)
        {
            byName[item.Name] = item;
        }

        return new ScheduledJobOverview(true, null, byName.Values.ToList());
    }

    private static SystemReportFacts Facts(
        IReadOnlyList<SystemReportVolumeFact>? volumes = null,
        CapacityProbeResult? capacity = null,
        IReadOnlyList<BackupFileInfo>? backups = null,
        TimeSpan? backupInterval = null,
        ScheduledJobOverview? jobs = null,
        SystemReportMetrics? metrics = null,
        int errorLogs = 0,
        string emailProvider = "Smtp",
        bool aiConfigured = false,
        IReadOnlyList<AiQuotaLimitStatus>? quota = null,
        IReadOnlyList<SystemHealthItem>? health = null)
        => new()
        {
            Period = new SystemReportPeriod(SystemReportFrequencies.Daily, Now.Date.AddDays(-1), Now.Date),
            NowLocal = Now,
            Health = health ?? [],
            Jobs = jobs ?? Jobs(),
            Backups = backups ?? [Backup(Now.AddHours(-5))],
            BackupInterval = backupInterval ?? TimeSpan.FromDays(1),
            NextBackupLocal = Now.Date.AddDays(1).AddHours(2),
            Metrics = metrics ?? new MetricsBuilder().Build(),
            ErrorLogCount = errorLogs,
            Capacity = capacity ?? Capacity(),
            Volumes = volumes ?? [],
            EmailProvider = emailProvider,
            AiConfigured = aiConfigured,
            Quota = quota,
        };

    /// <summary><see cref="SystemReportMetrics"/> 只有 init 屬性；用 record 的 with 方便每個測試只改需要的欄位。</summary>
    private sealed record MetricsBuilder
    {
        public int NewExceptionSignatures { get; init; }

        public int NewExceptionsSevere { get; init; }

        public IReadOnlyDictionary<string, int> FailedJobRuns { get; init; } = new Dictionary<string, int>();

        public int EmailsCreated { get; init; }

        public int EmailsFailed { get; init; }

        public int EmailsStuckPending { get; init; }

        public int AiCalls { get; init; }

        public int AiFailedCalls { get; init; }

        public IReadOnlyDictionary<string, int> SecurityEvents { get; init; } = new Dictionary<string, int>();

        public int LoginFailureBursts { get; init; }

        public int LockedAccounts { get; init; }

        public int EnabledAdmins { get; init; }

        public int AdminsWithoutTwoFactor { get; init; }

        public SystemReportMetrics Build() => new()
        {
            NewExceptionSignatures = NewExceptionSignatures,
            NewExceptionsSevere = NewExceptionsSevere,
            FailedJobRuns = FailedJobRuns,
            EmailsCreated = EmailsCreated,
            EmailsFailed = EmailsFailed,
            EmailsStuckPending = EmailsStuckPending,
            AiCalls = AiCalls,
            AiFailedCalls = AiFailedCalls,
            SecurityEvents = SecurityEvents,
            LoginFailureBursts = LoginFailureBursts,
            LockedAccounts = LockedAccounts,
            EnabledAdmins = EnabledAdmins,
            AdminsWithoutTwoFactor = AdminsWithoutTwoFactor,
        };
    }
}
