using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「AI 管理」唯讀工具的實際查詢（0.10.34 起補測）：提示詞清單與版本歷史、AI 對話紀錄查詢與明細、Token 用量逐日。
/// 稽核規則同各頁：提示詞頁讀取不寫稽核；AI 對話紀錄與 Token 用量查詢、開啟單筆要寫（加 via），找不到不寫。
/// </summary>
public sealed class AiAssistantReadToolsTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly string root = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
    private readonly RecordingAuditLogService audit = new();
    private readonly PromptTemplateService prompts;
    private readonly AiCallLogService callLogs;
    private readonly AiAssistantTools tools;

    public AiAssistantReadToolsTests()
    {
        connection.Open();
        var factory = new TestDbContextFactory(connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        Directory.CreateDirectory(root);
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiCallLogPath = Path.Combine(root, "ai");
        settings.ExternalFileSystem.TokenUsagePath = Path.Combine(root, "tk");
        prompts = new PromptTemplateService(factory, TimeProvider.System, NullLogger<PromptTemplateService>.Instance);
        callLogs = new AiCallLogService(factory, loggerFactory.CreateLogger<AiCallLogService>(),
            new AiCallLogFileStore(Options.Create(settings), loggerFactory.CreateLogger<AiCallLogFileStore>()),
            new StaticOptionsMonitor<AiCallLogSettings>(new AiCallLogSettings { Enabled = true, RetentionDays = 90 }));
        var tokenUsage = new TokenUsageLogService(factory, loggerFactory.CreateLogger<TokenUsageLogService>(),
            new TokenUsageRawStore(Options.Create(settings), loggerFactory.CreateLogger<TokenUsageRawStore>()), null!, new StubAiQuotaService());
        var currentUser = new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "admin", IsAdmin = true } };
        tools = new AiAssistantTools(prompts, tokenUsage, null!, callLogs, audit, currentUser);
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
    public async Task ListPromptTemplates_ShouldListEveryKind_WithItsStatus_AndNotAudit()
    {
        await prompts.SaveNewVersionAsync(PromptTemplateKeys.LogAnalysis, "第一版", "初版", null, "admin");

        using var json = JsonDocument.Parse(await tools.ListPromptTemplatesAsync());

        var templates = json.RootElement.GetProperty("templates").EnumerateArray().ToList();
        Assert.Equal(PromptTemplateKeys.All.Count, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(PromptTemplateKeys.All, templates.Select(item => item.GetProperty("key").GetString()));
        var changed = templates.Single(item => item.GetProperty("key").GetString() == PromptTemplateKeys.LogAnalysis);
        Assert.Equal("版本 1", changed.GetProperty("status").GetString());
        Assert.Equal(1, changed.GetProperty("versionCount").GetInt32());
        Assert.Equal("admin", changed.GetProperty("lastChangedBy").GetString());
        Assert.Equal("初版", changed.GetProperty("activeNote").GetString());
        var untouched = templates.First(item => item.GetProperty("key").GetString() != PromptTemplateKeys.LogAnalysis);
        Assert.Equal("內建預設", untouched.GetProperty("status").GetString());
        Assert.False(untouched.TryGetProperty("lastChangedBy", out _));
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task GetPromptHistory_ShouldReturnNewestFirst_ClampTake_AndTruncateContent()
    {
        var longContent = new string('長', AiAssistantTools.PromptContentMaxLength + 10);
        await prompts.SaveNewVersionAsync(PromptTemplateKeys.LogAnalysis, "第一版", null, null, "admin");
        await prompts.SaveNewVersionAsync(PromptTemplateKeys.LogAnalysis, longContent, "第二版", 1, "admin");

        using var json = JsonDocument.Parse(await tools.GetPromptHistoryAsync("loganalysis", take: 99));

        Assert.Equal(PromptTemplateKeys.LogAnalysis, json.RootElement.GetProperty("key").GetString());
        Assert.Equal("版本 2", json.RootElement.GetProperty("status").GetString());
        Assert.Contains("版數", json.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
        var versions = json.RootElement.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(item => item.GetProperty("version").GetInt32()));
        Assert.True(versions[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(AiAssistantTools.PromptContentMaxLength + 1, versions[0].GetProperty("content").GetString()!.Length);
        Assert.EndsWith("…", versions[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("第一版", versions[1].GetProperty("content").GetString());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task GetPromptHistory_TakeOne_ShouldReturnOnlyTheNewest()
    {
        await prompts.SaveNewVersionAsync(PromptTemplateKeys.LogAnalysis, "第一版", null, null, "admin");
        await prompts.SaveNewVersionAsync(PromptTemplateKeys.LogAnalysis, "第二版", null, 1, "admin");

        using var json = JsonDocument.Parse(await tools.GetPromptHistoryAsync(PromptTemplateKeys.LogAnalysis, take: 1));

        Assert.Equal(2, Assert.Single(json.RootElement.GetProperty("versions").EnumerateArray()).GetProperty("version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task GetPromptHistory_UnknownKey_ShouldListTheAvailableKinds()
    {
        var text = await tools.GetPromptHistoryAsync("NoSuchKey");

        Assert.StartsWith("找不到提示詞種類「NoSuchKey」", text, StringComparison.Ordinal);
        Assert.All(PromptTemplateKeys.All, key => Assert.Contains(key, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAiCallLogs_ShouldApplyTheFilters_AndWriteASearchAudit()
    {
        await callLogs.RecordAsync(Entry("系統管理助理", account: "alice", success: true, relatedInfo: "巡檢報告"));
        await callLogs.RecordAsync(Entry("系統管理助理", account: "alice", success: false, relatedInfo: "巡檢報告"));
        await callLogs.RecordAsync(Entry(TokenUsageOperations.AiLogAnalysis, account: "bob", success: true));

        using var json = JsonDocument.Parse(await tools.SearchAiCallLogsAsync(
            operation: " 系統管理助理 ", account: "ali", success: true, keyword: "巡檢", take: 99));

        Assert.Equal(1, json.RootElement.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("alice", item.GetProperty("account").GetString());
        Assert.True(item.GetProperty("success").GetBoolean());
        Assert.Contains("筆數", json.RootElement.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(DateTime.Today.AddDays(-1).ToString("yyyy/MM/dd"), json.RootElement.GetProperty("appliedStart").GetString());

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AiCallLog.Search, entry.Action);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Contains("operation=系統管理助理", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("success=true", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("hasAccount=True", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("hasKeyword=True", entry.Detail, StringComparison.Ordinal);
        Assert.EndsWith("via=admin-assistant", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ali", entry.Detail, StringComparison.Ordinal); // 自由輸入的原文只進內容檔
        Assert.Contains("keyword: 巡檢", Assert.Single(audit.Contents).Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAiCallLogDetail_Missing_ShouldSaySo_AndNotAudit()
    {
        var text = await tools.GetAiCallLogDetailAsync(9999);

        Assert.Equal("找不到 AI 對話紀錄 #9999。", text);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task GetAiCallLogDetail_ShouldTruncateMessagesAndResponse_AndWriteAViewAudit()
    {
        var entry = Entry("系統管理助理", account: "alice", success: true,
            requestBody: JsonSerializer.Serialize(new
            {
                model = "gpt-a",
                messages = new[]
                {
                    new { role = "system", content = "系統" },
                    new { role = "user", content = new string('問', AiAssistantTools.MessageMaxLength + 5) },
                },
            }),
            responseText: new string('答', AiAssistantTools.ResponseMaxLength + 5));
        await callLogs.RecordAsync(entry);
        var id = (await callLogs.FindIdByCallIdAsync(entry.CallId))!.Value;

        using var json = JsonDocument.Parse(await tools.GetAiCallLogDetailAsync(id));

        Assert.Equal(id, json.RootElement.GetProperty("id").GetInt32());
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal(AiAssistantTools.MessageMaxLength + 1, messages[1].GetProperty("content").GetString()!.Length);
        Assert.True(json.RootElement.GetProperty("messagesTruncated").GetBoolean());
        Assert.Equal(AiAssistantTools.ResponseMaxLength + 1, json.RootElement.GetProperty("response").GetString()!.Length);
        Assert.False(json.RootElement.TryGetProperty("usage", out _));
        Assert.Equal(0, json.RootElement.GetProperty("sameConversationCount").GetInt32()); // 沒有對話編號就不連結

        var audited = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AiCallLog.View, audited.Action);
        Assert.Equal("via=admin-assistant", audited.Detail);
        Assert.Equal(id.ToString(), Assert.Single(audit.Contents).TargetId);
    }

    [Fact]
    public async Task GetTokenUsage_GroupedByDay_ShouldReturnOneRowPerDay()
    {
        await AddTokenUsageAsync(DateTime.Today.AddHours(9), 100);
        await AddTokenUsageAsync(DateTime.Today.AddHours(10), 50);
        await AddTokenUsageAsync(DateTime.Today.AddDays(-2).AddHours(9), 30);

        using var json = JsonDocument.Parse(await tools.GetTokenUsageAsync(groupBy: "day", days: 3));

        Assert.Equal("Day", json.RootElement.GetProperty("groupBy").GetString());
        var rows = json.RootElement.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("callCount").GetInt32() > 0)
            .ToDictionary(row => row.GetProperty("date").GetString()!, row => row.GetProperty("totalTokens").GetInt64());
        Assert.Equal(150, rows[DateTime.Today.ToString("yyyy/MM/dd")]);
        Assert.Equal(30, rows[DateTime.Today.AddDays(-2).ToString("yyyy/MM/dd")]);
        Assert.Equal(3, json.RootElement.GetProperty("summary").GetProperty("callCount").GetInt32());
        Assert.Equal(AuditActions.TokenUsage.Search, Assert.Single(audit.Entries).Action);
    }

    private static AiCallLogEntry Entry(
        string operation, string? account, bool success, string? relatedInfo = null, string? requestBody = null, string responseText = "ok")
        => new()
        {
            CallId = Guid.NewGuid(),
            OccurredAt = DateTime.Now,
            Operation = operation,
            Provider = "AzureOpenAI",
            RequestedModel = "gpt-a",
            Model = "gpt-a",
            Account = account,
            UserId = account is null ? null : 7,
            Endpoint = "https://example.invalid/openai/v1/chat/completions",
            RelatedInfo = relatedInfo,
            Success = success,
            FailureReason = success ? null : "逾時",
            RequestBody = requestBody ?? "{}",
            ResponseText = responseText,
            ResponseBody = "{}",
        };

    private async Task AddTokenUsageAsync(DateTime occurredAt, int totalTokens)
    {
        await using var context = new TestDbContextFactory(connection).CreateDbContext();
        context.TokenUsageLog.Add(new TokenUsageLog
        {
            OccurredAt = occurredAt,
            Operation = TokenUsageOperations.AiLogAnalysis,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "p",
            Model = "m",
            Success = true,
            InputCount = totalTokens,
            TotalCount = totalTokens,
        });
        await context.SaveChangesAsync();
    }
}
