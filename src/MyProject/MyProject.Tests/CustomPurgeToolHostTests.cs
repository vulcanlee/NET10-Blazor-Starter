using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理的 <c>custom_purge</c>（0.9.159 起），以真正的主機驗證：和「資料保留與清理」頁走同一個服務與條件檢查，
/// 刪到資料才寫稽核（同頁面的 detail，加 via）；條件不合法時不刪除。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class CustomPurgeToolHostTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public CustomPurgeToolHostTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task CustomPurge_RemovesOldRows_AndAuditsLikeDataRetentionPage()
    {
        var signature = "custom-purge-tool-" + Guid.NewGuid().ToString("N");
        await using (var db = await Factory().CreateDbContextAsync())
        {
            db.ExceptionLog.Add(new ExceptionLog
            {
                Signature = signature,
                ExceptionType = "System.Exception",
                Message = "old",
                Source = ExceptionSources.Unknown,
                OccurrenceCount = 1,
                FirstOccurredAt = new DateTime(2000, 6, 1),
                LastOccurredAt = new DateTime(2000, 6, 1),
            });
            await db.SaveChangesAsync();
        }

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUserService>().CurrentUser = new CurrentUser { Id = 1, Account = "support", IsAdmin = true };
        var tools = scope.ServiceProvider.GetRequiredService<OpsAssistantTools>();

        var text = await tools.CustomPurgeAsync("exception-log", keepDays: null, beforeDate: "2001-01-01");

        Assert.Contains("\"removed\":", text, StringComparison.Ordinal);
        await using var check = await Factory().CreateDbContextAsync();
        Assert.False(await check.ExceptionLog.AnyAsync(x => x.Signature == signature));
        var audit = await check.AuditLog.OrderByDescending(x => x.Id).FirstAsync(x => x.Action == AuditActions.ExceptionLog.Purge);
        Assert.StartsWith("source=data-retention; mode=beforeDate; value=2001-01-01; rows=", audit.Detail, StringComparison.Ordinal);
        Assert.EndsWith("; via=admin-assistant", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomPurge_InvalidCondition_NothingDeleted()
    {
        using var scope = factory.Services.CreateScope();
        var tools = scope.ServiceProvider.GetRequiredService<OpsAssistantTools>();

        var both = await tools.CustomPurgeAsync("exception-log", keepDays: 30, beforeDate: "2001-01-01");
        var unknown = await tools.CustomPurgeAsync("backup", keepDays: 30, beforeDate: null);

        Assert.DoesNotContain("\"removed\"", both, StringComparison.Ordinal);
        Assert.DoesNotContain("\"removed\"", unknown, StringComparison.Ordinal);
    }

    private IDbContextFactory<BackendDBContext> Factory() => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
}
