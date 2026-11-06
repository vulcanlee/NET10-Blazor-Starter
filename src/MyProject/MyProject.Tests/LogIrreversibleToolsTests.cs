using AutoMapper;
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
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理的紀錄類不可復原工具（0.9.159 起）：逐筆刪除例外與 AI 對話紀錄、清空例外／AI 對話紀錄／Token 用量。
/// 這三頁的稽核都由頁面寫，所以工具在成功後寫同一個代碼（加 via）；失敗不寫。
/// </summary>
public sealed class LogIrreversibleToolsTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string root = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
    private readonly RecordingAuditLogService audit = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 7, Account = "admin", IsAdmin = true } };
    private readonly MonitoringAssistantTools monitoring;
    private readonly AiAssistantTools ai;

    public LogIrreversibleToolsTests()
    {
        connection.Open();
        using (var context = Factory().CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        Directory.CreateDirectory(root);
        var mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
        var settings = new SystemSettings();
        settings.ExternalFileSystem.ExceptionPath = Path.Combine(root, "ex");
        settings.ExternalFileSystem.AiCallLogPath = Path.Combine(root, "ai");
        settings.ExternalFileSystem.TokenUsagePath = Path.Combine(root, "tk");
        var exceptions = new ExceptionLogService(Factory(), mapper, loggerFactory.CreateLogger<ExceptionLogService>(),
            new ExceptionStackFileStore(Options.Create(settings)));
        var callLogs = new AiCallLogService(Factory(), mapper, loggerFactory.CreateLogger<AiCallLogService>(),
            new AiCallLogFileStore(Options.Create(settings), loggerFactory.CreateLogger<AiCallLogFileStore>()),
            new StaticOptionsMonitor<AiCallLogSettings>(new AiCallLogSettings { Enabled = true, RetentionDays = 90 }));
        var tokenUsage = new TokenUsageLogService(Factory(), mapper, loggerFactory.CreateLogger<TokenUsageLogService>(),
            new TokenUsageRawStore(Options.Create(settings), loggerFactory.CreateLogger<TokenUsageRawStore>()), null!, new StubAiQuotaService());

        monitoring = new MonitoringAssistantTools(null!, null!, null!, exceptions, null!, audit, currentUser, null!, null!);
        ai = new AiAssistantTools(null!, tokenUsage, null!, callLogs, audit, currentUser);
    }

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync();
        loggerFactory.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task DeleteExceptions_PerItem_AuditsTypeLikeThePage()
    {
        var id = await AddExceptionAsync("System.InvalidOperationException");

        var json = await monitoring.DeleteExceptionsAsync([id, 9999]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"failed\":1", json, StringComparison.Ordinal);
        Assert.False(await Context().ExceptionLog.AnyAsync());
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("ExceptionLog.Delete", entry.Action);
        Assert.Equal("type=InvalidOperationException; via=admin-assistant", entry.Detail);
    }

    [Fact]
    public async Task ClearExceptions_RemovesAll_AndAudits()
    {
        await AddExceptionAsync("System.Exception");
        await AddExceptionAsync("System.TimeoutException");

        await monitoring.ClearExceptionsAsync();

        Assert.False(await Context().ExceptionLog.AnyAsync());
        Assert.Equal("ExceptionLog.ClearAll", Assert.Single(audit.Entries).Action);
        Assert.EndsWith("; via=admin-assistant", audit.Entries[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAiCallLogs_PerItem_AuditsOperation()
    {
        var id = await AddCallLogAsync("系統管理助理");

        var json = await ai.DeleteAiCallLogsAsync([id]);

        Assert.Contains("\"succeeded\":1", json, StringComparison.Ordinal);
        Assert.False(await Context().AiCallLog.AnyAsync());
        Assert.Equal("刪除 AI 對話紀錄：系統管理助理; via=admin-assistant", Assert.Single(audit.Entries).Detail);
    }

    [Fact]
    public async Task ClearAiCallLogs_KeepsTokenUsage()
    {
        await AddCallLogAsync("系統管理助理");
        await AddTokenUsageAsync();

        var text = await ai.ClearAiCallLogsAsync();

        Assert.Contains("已清空", text, StringComparison.Ordinal);
        Assert.False(await Context().AiCallLog.AnyAsync());
        Assert.True(await Context().TokenUsageLog.AnyAsync());
        Assert.Equal("AiCallLog.ClearAll", Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task ClearTokenUsage_RemovesAll_AndAuditsWithPageTargetType()
    {
        await AddTokenUsageAsync();

        await ai.ClearTokenUsageAsync();

        Assert.False(await Context().TokenUsageLog.AnyAsync());
        Assert.Equal("TokenUsage.ClearAll", Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public void IrreversibleTools_FlaggedWithNotes()
    {
        Assert.Equal(["delete_exceptions", "clear_exceptions"], MonitoringAssistantTools.IrreversibleTools.Select(tool => tool.Name));
        Assert.Equal(["delete_ai_call_logs", "clear_ai_call_logs", "clear_token_usage"], AiAssistantTools.IrreversibleTools.Select(tool => tool.Name));
        Assert.All(MonitoringAssistantTools.IrreversibleTools.Concat(AiAssistantTools.IrreversibleTools), tool =>
        {
            Assert.True(tool.IsMutating && tool.IsIrreversible, tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.ApprovalNote), tool.Name);
        });
    }

    private TestDbContextFactory Factory() => new(connection);

    private BackendDBContext Context() => Factory().CreateDbContext();

    private async Task<int> AddExceptionAsync(string type)
    {
        await using var context = Context();
        var row = new ExceptionLog
        {
            Signature = Guid.NewGuid().ToString("N"),
            ExceptionType = type,
            Message = "boom",
            Source = ExceptionSources.Unknown,
            OccurrenceCount = 1,
            FirstOccurredAt = DateTime.Now,
            LastOccurredAt = DateTime.Now,
        };
        context.ExceptionLog.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> AddCallLogAsync(string operation)
    {
        await using var context = Context();
        var row = new AiCallLog { CallId = Guid.NewGuid(), OccurredAt = DateTime.Now, Operation = operation, Model = "m", Provider = "p" };
        context.AiCallLog.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task AddTokenUsageAsync()
    {
        await using var context = Context();
        context.TokenUsageLog.Add(new TokenUsageLog
        {
            OccurredAt = DateTime.Now,
            Operation = TokenUsageOperations.AiLogAnalysis,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "p",
            Model = "m",
            Success = true,
        });
        await context.SaveChangesAsync();
    }
}
