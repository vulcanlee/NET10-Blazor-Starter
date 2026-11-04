using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Configuration;
using Fixture = MyProject.Tests.ProjectServiceTeamAccessTests.ProjectServiceFixture;

namespace MyProject.Tests;

/// <summary>
/// 0.9.142 起的 Agent 骨架：範例專案工具（必須沿用專案的團隊範圍）與各功能的模型設定。
/// </summary>
public sealed class AgentToolsAndModelTests
{
    [Fact]
    public async Task SearchProjects_ShouldOnlyReturnProjectsInUsersTeamScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var tools = new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A"));

        var result = await tools.SearchProjectsAsync(string.Empty);

        Assert.Equal(["公開專案", "團隊A專案"], result.Select(x => x.Title).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task UpdateDescription_OutsideTeamScope_ShouldNotModify()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var tools = new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A"));

        var message = await tools.UpdateProjectDescriptionAsync(ids["團隊B專案"], "被改了");

        Assert.Contains("沒有修改", message, StringComparison.Ordinal);
        var stored = await fixture.CreateService(isAdmin: true).GetAsync(ids["團隊B專案"]);
        Assert.NotEqual("被改了", stored.Description);
    }

    [Fact]
    public async Task UpdateDescription_InScope_ShouldModifyThroughProjectService()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var tools = new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A"));

        var message = await tools.UpdateProjectDescriptionAsync(ids["團隊A專案"], "新的描述");

        Assert.Contains("已把專案", message, StringComparison.Ordinal);
        var stored = await fixture.CreateService(isAdmin: true).GetAsync(ids["團隊A專案"]);
        Assert.Equal("新的描述", stored.Description);
    }

    [Fact]
    public async Task ProjectDetail_ShouldApplyTeamScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var tools = new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A"));

        Assert.Null(await tools.GetProjectDetailAsync(ids["團隊B專案"]));
        var detail = await tools.GetProjectDetailAsync(ids["團隊A專案"]);
        Assert.Equal("團隊A專案", detail!.Title);
        Assert.Equal(["團隊A"], detail.Teams);
    }

    [Fact]
    public async Task AppendStatusSummary_ShouldKeepDescription_CollapseLines_AndTruncate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.Zero));
        var tools = new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A"), clock);
        var service = fixture.CreateService(isAdmin: true);
        var project = await service.GetAsync(ids["團隊A專案"]);
        project.Description = "原本的描述";
        await service.UpdateAsync(project);

        var message = await tools.AppendProjectStatusSummaryAsync(ids["團隊A專案"], "第一行\n第二行 " + new string('長', 300));

        Assert.Contains("已在專案", message, StringComparison.Ordinal);
        var stored = (await service.GetAsync(ids["團隊A專案"])).Description!;
        // 本地時間（UTC+8）已經是 11/04。
        Assert.StartsWith("原本的描述\n\n【狀態摘要 2026/11/04】第一行 第二行 ", stored, StringComparison.Ordinal);
        Assert.EndsWith("…", stored, StringComparison.Ordinal);
        Assert.Equal(ProjectAgentTools.MaxSummaryLength + 1, stored[(stored.IndexOf('】') + 1)..].Length);
    }

    [Fact]
    public async Task AppendStatusSummary_OutsideTeamScope_ShouldNotModify()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var tools = new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A"));

        var message = await tools.AppendProjectStatusSummaryAsync(ids["團隊B專案"], "摘要");

        Assert.Contains("沒有修改", message, StringComparison.Ordinal);
        Assert.Null((await fixture.CreateService(isAdmin: true).GetAsync(ids["團隊B專案"])).Description);
    }

    [Theory]
    [InlineData(null, "", "default-model")]
    [InlineData(nameof(AiModelSettings.LogAnalysis), "", "default-model")]
    [InlineData(nameof(AiModelSettings.LogAnalysis), "  cheap-model  ", "cheap-model")]
    public void ModelSettings_Resolve_ShouldFallBackToDefault(string? key, string configured, string expected)
    {
        var settings = new AiModelSettings { LogAnalysis = configured };

        Assert.Equal(expected, settings.Resolve(key, "default-model"));
    }

    [Fact]
    public void ModelSettings_UnknownKey_ShouldThrow()
        => Assert.Throws<InvalidOperationException>(() => new AiModelSettings().Resolve("NoSuchFeature", "m"));

    [Fact]
    public void ModelSettings_OverriddenModels_ShouldListOnlyConfiguredOnes()
        => Assert.Equal(
            ["reasoning-model"],
            new AiModelSettings { ExceptionAnalysis = "reasoning-model" }.OverriddenModels().ToList());

    /// <summary>系統參數設了 AI 日誌分析的模型：送出的 model 與記帳的模型都要用它。</summary>
    [Fact]
    public async Task LogAnalysis_ShouldUseConfiguredModel_ForRequestAndAccounting()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            { "choices": [ { "finish_reason": "stop", "message": { "content": "ok" } } ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 } }
            """);
        var usage = new RecordingTokenUsageRecorder();
        var options = new StaticOptionsMonitor<AiSettings>(new AiSettings { Provider = nameof(AiProvider.OpenAI), ApiKey = "key", Model = "default-model" });
        var factory = new AiAgentFactory(
            new StubHttpClientFactory(handler),
            options,
            NullLoggerFactory.Instance,
            usage,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService(),
            modelOptions: new StaticOptionsMonitor<AiModelSettings>(new AiModelSettings { LogAnalysis = "cheap-model" }));
        var service = new AiLogAnalysisService(
            options,
            new AiChatCompletionClient(NullLogger<AiChatCompletionClient>.Instance, options, factory),
            new StubSystemPromptProvider());

        var result = await service.AnalyzeAsync(
        [
            new MyProject.Web.Diagnostics.LogEntry { Sequence = 1, Timestamp = new DateTime(2026, 11, 3, 9, 0, 0), Level = "ERROR", Raw = "boom" },
        ]);

        Assert.True(result.Success);
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("cheap-model", body.RootElement.GetProperty("model").GetString());
        var entry = Assert.Single(usage.Entries);
        Assert.Equal("cheap-model", entry.Model);
        Assert.NotNull(entry.RunId);
        Assert.Equal(TokenUsageOperations.AiLogAnalysis, entry.Operation);
    }
}
