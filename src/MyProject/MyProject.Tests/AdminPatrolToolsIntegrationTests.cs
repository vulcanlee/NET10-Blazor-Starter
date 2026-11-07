using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using MyProject.Business.Services.Other;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>
/// 定時巡檢（0.9.161 起），以真正的主機驗證：在<b>沒有登入使用者</b>的 scope 裡（同排程作業），經權限窄門呼叫巡檢指示列出的每一個工具，
/// 都不會回「沒有權限」或「執行失敗」—— 確認唯讀工具本身不依賴目前使用者。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AdminPatrolToolsIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public AdminPatrolToolsIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>巡檢指示（<see cref="AdminAssistantAgent.DefaultCommonInstructions"/>）列出的工具與參數。</summary>
    public static TheoryData<string, string> PatrolCalls => new()
    {
        { "get_system_health", "{}" },
        { "query_logs", """{"minimumLevel":"warn"}""" },
        { "search_exceptions", """{"hours":24}""" },
        { "get_scheduled_jobs_overview", "{}" },
        { "list_backups", "{}" },
        { "get_database_usage", "{}" },
        { "get_token_usage", """{"days":2}""" },
        { "get_ai_quota_status", "{}" },
        { "get_account_security_overview", """{"hours":24,"inactiveDays":90}""" },
    };

    [Theory]
    [MemberData(nameof(PatrolCalls))]
    public async Task PatrolTool_WithoutUser_RunsThroughTheGate(string name, string argumentsJson)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.Equal(0, services.GetRequiredService<CurrentUserService>().CurrentUser.Id);
        Assert.Contains(name, AdminAssistantAgent.DefaultCommonInstructions, StringComparison.Ordinal);

        Assert.Contains(name, AdminAssistantAgent.PatrolToolNames);
        var gate = new ScheduledPatrolToolGuard(services.GetRequiredService<IAiToolPermissionGuard>(), AdminAssistantAgent.PatrolToolNames);
        var function = new AiAgentToolFactory(services, gate).Create(AdminAssistantAgent.ScheduledPatrol)
            .OfType<AIFunction>()
            .Single(tool => tool.Name == name);
        var arguments = new AIFunctionArguments(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)!)
        {
            Services = services,
        };

        var result = (await function.InvokeAsync(arguments))?.ToString() ?? string.Empty;

        Assert.DoesNotContain("沒有執行這個操作的權限", result, StringComparison.Ordinal);
        Assert.DoesNotContain("執行失敗", result, StringComparison.Ordinal);
    }
}
