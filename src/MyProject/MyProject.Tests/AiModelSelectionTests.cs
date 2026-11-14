using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// AI 呼叫用哪一個模型：各功能的模型設定（系統參數頁可改，空白＝<c>AiSettings:Model</c>）。
/// 0.10.22 起從 AgentToolsAndModelTests 搬來（Agent 測試台的專案工具已移除）。
/// </summary>
public sealed class AiModelSelectionTests
{
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

    /// <summary>
    /// 快速模型（0.10.22 起，<c>AiSettings:FastModel</c>）：呼叫端標明 <c>UseFastModel</c> 才用它，空白時用 <c>Model</c>；
    /// 系統參數替該功能指定的模型優先於快速模型。送出的 model 與記帳的模型都要一致。
    /// </summary>
    [Theory]
    [InlineData(false, "fast-model", null, "", "default-model")]
    [InlineData(true, "fast-model", null, "", "fast-model")]
    [InlineData(true, "  fast-model  ", null, "", "fast-model")]
    [InlineData(true, "", null, "", "default-model")]
    [InlineData(true, "   ", null, "", "default-model")]
    [InlineData(true, "fast-model", nameof(AiModelSettings.LogAnalysis), "", "fast-model")]
    [InlineData(true, "fast-model", nameof(AiModelSettings.LogAnalysis), "feature-model", "feature-model")]
    [InlineData(false, "fast-model", nameof(AiModelSettings.LogAnalysis), "feature-model", "feature-model")]
    public async Task FastModel_ShouldBeUsedOnlyWhenAsked_AndYieldToTheFeatureSetting(
        bool useFastModel, string fastModel, string? modelKey, string featureModel, string expected)
    {
        var (handler, usage, client) = CreateClient(
            new AiSettings { Provider = nameof(AiProvider.OpenAI), ApiKey = "key", Model = "default-model", FastModel = fastModel },
            new AiModelSettings { LogAnalysis = featureModel });

        var result = await client.CompleteAsync(new AiChatCompletionRequest
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            Messages = [new AiChatMessage(AiChatRoles.User, "把這段文字產生三個標籤")],
            ModelKey = modelKey,
            UseFastModel = useFastModel,
        });

        Assert.True(result.Success);
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(expected, body.RootElement.GetProperty("model").GetString());
        Assert.Equal(expected, Assert.Single(usage.Entries).Model);
    }

    [Theory]
    [InlineData(false, "", "default-model")]
    [InlineData(true, "", "default-model")]
    [InlineData(true, "fast-model", "fast-model")]
    [InlineData(false, "fast-model", "default-model")]
    public void DefaultModel_ShouldFallBackToModel_WhenFastModelIsBlank(bool fast, string fastModel, string expected)
        => Assert.Equal(expected, new AiSettings { Model = "default-model", FastModel = fastModel }.DefaultModel(fast));

    /// <summary>「系統健康監控」的 AI 計費表要檢查每一個可能被用到的模型：預設、快速（有填才算）與系統參數指定的，不分大小寫去重。</summary>
    [Theory]
    [InlineData("fast-model", new[] { "default-model", "fast-model", "feature-model" })]
    [InlineData("", new[] { "default-model", "feature-model" })]
    [InlineData("DEFAULT-model", new[] { "default-model", "feature-model" })]
    public void ModelsToPrice_ShouldIncludeTheFastModel(string fastModel, string[] expected)
        => Assert.Equal(
            expected,
            MyProject.Web.Health.SystemHealthService.ModelsToPrice(
                new AiSettings { Model = "default-model", FastModel = fastModel },
                new AiModelSettings { LogAnalysis = "feature-model" }));

    private static (StubHttpMessageHandler Handler, RecordingTokenUsageRecorder Usage, AiChatCompletionClient Client) CreateClient(
        AiSettings settings, AiModelSettings models)
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            { "choices": [ { "finish_reason": "stop", "message": { "content": "ok" } } ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 } }
            """);
        var usage = new RecordingTokenUsageRecorder();
        var options = new StaticOptionsMonitor<AiSettings>(settings);
        var factory = new AiAgentFactory(
            new StubHttpClientFactory(handler),
            options,
            NullLoggerFactory.Instance,
            usage,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService(),
            modelOptions: new StaticOptionsMonitor<AiModelSettings>(models));
        return (handler, usage, new AiChatCompletionClient(NullLogger<AiChatCompletionClient>.Instance, options, factory));
    }
}
