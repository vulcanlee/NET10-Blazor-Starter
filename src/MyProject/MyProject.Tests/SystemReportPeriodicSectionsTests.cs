using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Web.Backup;
using MyProject.Web.SystemReports;

namespace MyProject.Tests;

/// <summary>
/// 週報、月報的章節（0.10.41 起）：日報沒有章節；週報有趨勢、容量成長、備份可還原驗證；月報再加 AI 費用；
/// 季末的月報加帳號清查。帳號與姓名只能出現在表格，重點（會進通知、信件與 AI）只有數字；備份驗證失敗是紅燈。
/// </summary>
public sealed class SystemReportPeriodicSectionsTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2027, 1, 1, 8, 30, 0);

    [Fact]
    public void Daily_HasNoSections()
        => Assert.Empty(SystemReportPeriodicSections.Build(Facts(SystemReportPeriod.LastComplete(SystemReportFrequencies.Daily, Now))));

    [Fact]
    public void Weekly_HasTrendGrowthAndBackup_NotAiCostOrReview()
    {
        var sections = SystemReportPeriodicSections.Build(Facts(SystemReportPeriod.LastComplete(SystemReportFrequencies.Weekly, Now), verification: Passed()));

        Assert.Equal([SystemReportSectionKeys.Trend, SystemReportSectionKeys.Growth, SystemReportSectionKeys.BackupVerify], sections.Select(x => x.Key));
        Assert.Contains("上一週", sections[0].Highlights[0], StringComparison.Ordinal);
        Assert.Contains("可以還原", sections[2].Highlights[0], StringComparison.Ordinal);
    }

    [Fact]
    public void QuarterEndMonthly_HasEverySection_AndAccountsOnlyInTables()
    {
        var period = SystemReportPeriod.LastComplete(SystemReportFrequencies.Monthly, Now);
        var sections = SystemReportPeriodicSections.Build(Facts(period, verification: Passed(), aiCost: AiCost(), review: Review()));

        Assert.Equal(
            [SystemReportSectionKeys.Trend, SystemReportSectionKeys.Growth, SystemReportSectionKeys.BackupVerify, SystemReportSectionKeys.AiCost, SystemReportSectionKeys.AccountReview],
            sections.Select(x => x.Key));
        var review = sections.Single(x => x.Key == SystemReportSectionKeys.AccountReview);
        Assert.Equal("帳號與權限清查（2026/10～2026/12）", review.Title);
        Assert.Contains(review.Tables, t => t.Rows.Any(r => r.Contains("secret-admin")));
        Assert.Contains(sections.Single(x => x.Key == SystemReportSectionKeys.AiCost).Tables, t => t.Rows.Any(r => r.Contains("secret-user")));

        // 重點會進通知、信件與 AI 說明：不可以有帳號或姓名。
        var highlights = string.Join("|", sections.SelectMany(x => x.Highlights));
        foreach (var secret in new[] { "secret-admin", "secret-user", "祕密姓名" })
        {
            Assert.DoesNotContain(secret, highlights, StringComparison.Ordinal);
        }

        var content = new SystemReportContentDto { Frequency = period.Frequency, PeriodStart = period.Start, PeriodEnd = period.End, Sections = sections, Verdict = "結論。", Level = SystemReportLevels.Green };
        var aiInput = SystemReportNarrator.BuildUserMessage(content);
        var body = SystemReportDelivery.Body(content);
        foreach (var secret in new[] { "secret-admin", "secret-user", "祕密姓名" })
        {
            Assert.DoesNotContain(secret, aiInput, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }

        Assert.Contains("帳號與權限清查", aiInput, StringComparison.Ordinal);
        Assert.Contains("【AI 費用】", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Trend_ComparesWithThePreviousPeriod()
    {
        var facts = Facts(SystemReportPeriod.LastComplete(SystemReportFrequencies.Weekly, Now),
            metrics: new SystemReportMetrics { EmailsFailed = 6, AiCostTwd = 3 },
            previous: new SystemReportMetrics { EmailsFailed = 2, AiCostTwd = 3 });

        var trend = SystemReportPeriodicSections.Build(facts)[0];
        var rows = trend.Tables[0].Rows.ToDictionary(x => x[0]);

        Assert.Equal("+4（+200%）", rows["寄送失敗的信"][3]);
        Assert.Equal("持平", rows["AI 費用"][3]);
        Assert.Contains("寄送失敗的信 2 → 6", trend.Highlights[0], StringComparison.Ordinal);
        Assert.Equal("+5", SystemReportPeriodicSections.Change(5, 0));
        Assert.Equal("-1（-50%）", SystemReportPeriodicSections.Change(1, 2));
    }

    [Fact]
    public void Growth_UsesSnapshotsInThePeriod_AndProjectsOneYear()
    {
        var period = SystemReportPeriod.LastComplete(SystemReportFrequencies.Monthly, Now);
        var volume = new SystemReportVolumeFact(new CapacityVolumeUsage("D:\\", 100 * Gb, 20 * Gb, ["資料庫"]), new CapacityForecastResult(30, Gb / 2, 40));
        var history = new List<CapacitySnapshotRow>
        {
            new(period.Start, CapacitySnapshotKinds.Volume, "D:\\", 60 * Gb, 100 * Gb, 40 * Gb, null, CapacitySnapshotKinds.StatusOk),
            new(period.Start.AddDays(-10), CapacitySnapshotKinds.Database, CapacityProbe.DatabaseMainKey, Gb, null, null, null, CapacitySnapshotKinds.StatusOk),
            new(period.Start.AddDays(1), CapacitySnapshotKinds.Database, CapacityProbe.DatabaseMainKey, 2 * Gb, null, null, null, CapacitySnapshotKinds.StatusOk),
        };
        var facts = Facts(period, volumes: [volume], history: history, database: new CapacityDatabaseUsage("D:\\", 3 * Gb, 0, 0));

        var growth = SystemReportPeriodicSections.Build(facts).Single(x => x.Key == SystemReportSectionKeys.Growth);

        Assert.Equal(["D:\\", "60 GB", "80 GB", "+20 GB"], growth.Tables[0].Rows[0].Take(4));
        Assert.Contains("不夠", growth.Tables[0].Rows[0][5], StringComparison.Ordinal);
        Assert.Equal(["資料庫", "2 GB", "3 GB", "+1 GB"], growth.Tables[1].Rows[0]);
        Assert.Contains("增加 1 GB", growth.Highlights[0], StringComparison.Ordinal);
        Assert.Contains("一年後", growth.Highlights[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BackupVerify_FailedIsRed_UnverifiableIsYellow_PassedIsSilent()
    {
        var period = SystemReportPeriod.LastComplete(SystemReportFrequencies.Weekly, Now);
        var failed = Verification(BackupVerificationOutcomes.Failed, new BackupVerificationCheck("資料庫雜湊相符", false, "SHA-256 不同。"));
        var unverifiable = Verification(BackupVerificationOutcomes.Unverifiable, new BackupVerificationCheck("資料庫檔案", false, "空間不夠。"));

        var red = SystemReportRules.Evaluate(Facts(period, verification: failed), new SystemReportSettings()).Single(x => x.RuleId == "BKP-VERIFY");
        var yellow = SystemReportRules.Evaluate(Facts(period, verification: unverifiable), new SystemReportSettings()).Single(x => x.RuleId == "BKP-VERIFY");

        Assert.Equal(SystemReportLevels.Red, red.Level);
        Assert.Contains("資料庫雜湊相符", red.Detail, StringComparison.Ordinal);
        Assert.Equal(SystemReportLevels.Yellow, yellow.Level);
        Assert.DoesNotContain(SystemReportRules.Evaluate(Facts(period, verification: Passed()), new SystemReportSettings()), x => x.RuleId == "BKP-VERIFY");
        Assert.Contains("無法還原", SystemReportPeriodicSections.Build(Facts(period, verification: failed)).Single(x => x.Key == SystemReportSectionKeys.BackupVerify).Highlights[0], StringComparison.Ordinal);
        Assert.Contains("沒有任何備份", SystemReportPeriodicSections.Build(Facts(period, backups: [])).Single(x => x.Key == SystemReportSectionKeys.BackupVerify).Highlights[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AccountReview_LongListsAreCappedWithANote()
    {
        var many = Enumerable.Range(0, SystemReportMetricsService.MaxReviewRows + 5)
            .Select(i => new SystemReportReviewAccount($"u{i}", "名", false, null, false, false, null)).ToList();
        var review = new SystemReportAccountReview { StaleDays = 90, WithoutTwoFactor = many };

        var section = SystemReportPeriodicSections.Build(Facts(SystemReportPeriod.LastComplete(SystemReportFrequencies.Monthly, Now), review: review))
            .Single(x => x.Key == SystemReportSectionKeys.AccountReview);

        Assert.Equal(SystemReportMetricsService.MaxReviewRows, section.Tables.Single(x => x.Caption == "沒有兩步驟驗證").Rows.Count);
        Assert.Contains(section.Notes, x => x.Contains($"共 {SystemReportMetricsService.MaxReviewRows + 5:N0} 筆", StringComparison.Ordinal));
    }

    [Fact]
    public void Pdf_RendersTheSections()
    {
        var period = SystemReportPeriod.LastComplete(SystemReportFrequencies.Monthly, Now);
        var content = SystemReportPdfBuilderTests.Content(SystemReportLevels.Yellow, narrative: null);
        content.Frequency = period.Frequency;
        content.Sections = SystemReportPeriodicSections.Build(Facts(period, verification: Passed(), aiCost: AiCost(), review: Review()));

        var bytes = SystemReportPdfBuilder.Build(content);

        Assert.True(bytes.Length > SystemReportPdfBuilder.Build(SystemReportPdfBuilderTests.Content(SystemReportLevels.Yellow, narrative: null)).Length);
    }

    // ───────────── 測試資料 ─────────────

    private static BackupVerificationResult Passed()
        => Verification(BackupVerificationOutcomes.Passed, new BackupVerificationCheck("壓縮檔可以開啟", true, "共 3 個項目。"));

    private static BackupVerificationResult Verification(string outcome, params BackupVerificationCheck[] checks)
        => new("backup-20261231-020000.zip", new DateTime(2026, 12, 31, 2, 0, 0), outcome, checks, 10 * 1024 * 1024, 3, TimeSpan.FromSeconds(4));

    private static SystemReportAiCost AiCost() => new()
    {
        CostTwd = 12,
        PreviousCostTwd = 10,
        Calls = 5,
        UserCount = 1,
        ByOperation = [new("例外分析", 5, 12, 10)],
        ByModel = [new("gpt-a", 5, 12, 10)],
        TopUsers = [new("secret-user", "祕密姓名", 5, 12)],
    };

    private static SystemReportAccountReview Review() => new()
    {
        StaleDays = 90,
        EnabledAccounts = 3,
        Admins = [new("secret-admin", "祕密姓名", true, null, true, false, new DateTime(2026, 12, 30))],
        Changes = [new(new DateTime(2026, 11, 2, 1, 0, 0), AuditActions.Role.Update, "secret-admin", "角色 業務")],
    };

    private static SystemReportFacts Facts(
        SystemReportPeriod period,
        SystemReportMetrics? metrics = null,
        SystemReportMetrics? previous = null,
        IReadOnlyList<SystemReportVolumeFact>? volumes = null,
        IReadOnlyList<CapacitySnapshotRow>? history = null,
        CapacityDatabaseUsage? database = null,
        IReadOnlyList<BackupFileInfo>? backups = null,
        BackupVerificationResult? verification = null,
        SystemReportAiCost? aiCost = null,
        SystemReportAccountReview? review = null)
        => new()
        {
            Period = period,
            NowLocal = Now,
            Zone = TimeZoneInfo.Utc,
            Metrics = metrics ?? new SystemReportMetrics(),
            PreviousMetrics = previous ?? new SystemReportMetrics(),
            Capacity = new CapacityProbeResult { Volumes = volumes?.Select(x => x.Usage).ToList() ?? [], Folders = [], Database = database },
            Volumes = volumes ?? [],
            CapacityHistory = history ?? [],
            Backups = backups ?? [new BackupFileInfo("backup-20261231-020000.zip", 1024, new DateTime(2026, 12, 31, 2, 0, 0), null)],
            BackupInterval = TimeSpan.FromDays(1),
            BackupVerification = verification,
            AiCost = aiCost,
            AccountReview = review,
            EmailProvider = "Smtp",
        };
}
