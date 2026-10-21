using Microsoft.Extensions.DependencyInjection;
using MyProject.Web.Components.Views.Admins;
using MyProject.Web.DataRetention;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Tests;

/// <summary>
/// 「資料保留與清理」頁（0.9.133 起），以真正的主機驗證：每一列都讀得到（沒有「讀取失敗」）、順序與負責的作業正確、
/// 系統備份沒有「立即清理」、沒有作業的列也沒有；以及確認窗的文字。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class DataRetentionOverviewServiceTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public DataRetentionOverviewServiceTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Overview_ShouldListEveryKindOfData_WithItsJob()
    {
        using var scope = factory.Services.CreateScope();
        var overview = await scope.ServiceProvider.GetRequiredService<DataRetentionOverviewService>().GetOverviewAsync();

        Assert.Equal(
            ["deleted-project", "deleted-category", "deleted-team", "deleted-user", "deleted-role",
             "exception-log", "token-usage", "ai-call-log", "audit-log", "notification", "backup", "job-run", "nlog", "announcement"],
            overview.Rows.Select(x => x.Key).ToArray());
        Assert.All(overview.Rows, row => Assert.Null(row.Error));

        string? JobOf(string key) => overview.Rows.Single(x => x.Key == key).JobName;
        Assert.All(overview.Rows.Where(x => x.Key.StartsWith("deleted-", StringComparison.Ordinal)), row => Assert.Equal(SoftDeletePurgeJob.JobName, row.JobName));
        Assert.Equal(ExceptionLogRetentionJob.JobName, JobOf("exception-log"));
        Assert.Equal(TokenUsageLogRetentionJob.JobName, JobOf("token-usage"));
        Assert.Equal(AiCallLogRetentionJob.JobName, JobOf("ai-call-log"));
        Assert.Equal(AuditLogRetentionJob.JobName, JobOf("audit-log"));
        Assert.Equal(NotificationRetentionJob.JobName, JobOf("notification"));
        Assert.Equal(SystemBackupJob.JobName, JobOf("backup"));
        Assert.All(overview.Rows.Where(x => x.JobName is not null), row => Assert.NotNull(row.Job));

        // 「立即清理」：備份與沒有作業的列沒有這個按鈕（既不能按、也沒有不能按的原因）。
        foreach (var key in new[] { "backup", "job-run", "nlog", "announcement" })
        {
            var row = overview.Rows.Single(x => x.Key == key);
            Assert.False(row.CanPurgeNow);
            Assert.Null(row.PurgeDisabledReason);
        }

        Assert.All(overview.Rows.Where(x => x.Key != "notification"), row => Assert.NotNull(row.ManageUrl));
        Assert.Equal("/projects?deleted=1", overview.Rows.Single(x => x.Key == "deleted-project").ManageUrl);
    }

    [Theory]
    [InlineData("/projects")]
    [InlineData("/categories")]
    [InlineData("/teams")]
    [InlineData("/myusers")]
    [InlineData("/roleviews")]
    public void ManagePages_ForSoftDeletedData_ShouldHonorTheDeletedQuery(string route)
    {
        // 「前往」帶 ?deleted=1：頁面沒有接這個參數的話，只會落在一般清單，使用者還得自己按「顯示已刪除」。
        var pages = Directory.EnumerateFiles(FindPagesRoot(), "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Where(text => text.Contains($"@page \"{route}\"", StringComparison.Ordinal))
            .ToList();

        var page = Assert.Single(pages);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"deleted\")]", page, StringComparison.Ordinal);
        Assert.Contains("InitialShowDeleted=", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmContent_ForSoftDeletedData_ShouldListAllFiveKinds()
    {
        var rows = new[] { "已刪除的專案", "已刪除的分類", "已刪除的團隊", "已刪除的使用者", "已刪除的角色" }
            .Select((label, i) => new DataRetentionRow { Key = $"k{i}", Label = label, Expected = i, JobName = SoftDeletePurgeJob.JobName, RuleText = "刪除超過 90 天後永久刪除" })
            .ToList();

        var content = DataRetentionView.BuildConfirmContent(rows[0], rows, expected: 10);

        Assert.All(rows, row => Assert.Contains($"・{row.Label}：", content, StringComparison.Ordinal));
        Assert.Contains("共最多 10 筆", content, StringComparison.Ordinal);
        Assert.EndsWith("此動作無法復原。", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmContent_ForOtherData_ShouldGiveTheRuleAndCount()
    {
        var row = new DataRetentionRow { Key = "audit-log", Label = "稽核紀錄", Note = "連同詳細內容檔", Expected = 87, JobName = AuditLogRetentionJob.JobName, RuleText = "保留 365 天" };

        var content = DataRetentionView.BuildConfirmContent(row, [row], expected: 87);

        Assert.StartsWith("將依目前的保留規則（保留 365 天）刪除「稽核紀錄」中約 87 筆，連同詳細內容檔。", content, StringComparison.Ordinal);
    }

    private static string FindPagesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "MyProject", "MyProject.Web", "Components", "Pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, "MyProject.Web", "Components", "Pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MyProject.Web/Components/Pages。");
    }
}
