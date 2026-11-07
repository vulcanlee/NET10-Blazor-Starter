using System.Reflection;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Ai.Workflows;
using MyProject.Web.Configuration;
using MyProject.Web.Configuration.Parameters;
using MyProject.Web.Configuration.Validation;

namespace MyProject.Tests;

/// <summary>
/// 各 Agent 功能的推論等級（0.9.168 起，系統參數可改）：空白時沿用 <c>AiSettings:AgentReasoningEffort</c>。
/// </summary>
public sealed class AiReasoningSettingsTests
{
    [Fact]
    public void GetOverride_ShouldReturnTheFeatureValueInLowerCase()
    {
        var settings = new AiReasoningSettings { AgentPlayground = " LOW " };

        Assert.Equal("low", settings.GetOverride(nameof(AiReasoningSettings.AgentPlayground)));
    }

    /// <summary>沒有自己的值時回 null，由 <c>AiAgentFactory</c> 改用 <c>AiSettings:AgentReasoningEffort</c>。</summary>
    [Theory]
    [InlineData("AgentPlayground", "")]
    [InlineData("AgentPlayground", "   ")]
    [InlineData(null, "low")]
    [InlineData("LogAnalysis", "low")]
    [InlineData("AdminAssistantRouter", "low")]
    public void GetOverride_ShouldBeNull_WhenTheFeatureHasNoValueOfItsOwn(string? key, string playground)
    {
        var settings = new AiReasoningSettings { AgentPlayground = playground, AdminAssistant = "low" };

        Assert.Null(settings.GetOverride(key));
    }

    /// <summary>
    /// 審查發現（0.9.168）：Agent 測試台在開對話或接續時才建立 agent（沒有規劃器，不會每則訊息重建），
    /// 生效時機要和「使用模型」一樣寫「下一段新對話」，不能寫「下一則訊息起」。
    /// </summary>
    [Fact]
    public void AgentPlaygroundEffectNote_ShouldSayNewConversation()
    {
        var note = SystemParameterCatalog.Find("AiReasoningSettings:AgentPlayground")!.EffectNote;

        Assert.Contains("下一段新對話", note, StringComparison.Ordinal);
        Assert.DoesNotContain("下一則訊息", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("High")]
    [InlineData("xhigh")]
    public void Validator_ShouldAcceptBlankAndKnownValues(string value)
        => Assert.True(new AiReasoningSettingsValidator().Validate(null, new AiReasoningSettings { ProjectStatusReport = value }).Succeeded);

    [Fact]
    public void Validator_ShouldRejectUnknownValues_AndNameTheKey()
    {
        var result = new AiReasoningSettingsValidator().Validate(null, new AiReasoningSettings { MultiPerspectiveReview = "ultra" });

        Assert.True(result.Failed);
        Assert.Contains("AiReasoningSettings:MultiPerspectiveReview", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// 每個走 Responses API（會送推論等級）的 Agent，它的 <c>ModelKey</c> 都要有自己的推論等級設定；
    /// 否則新增的 Agent 會靜默沿用全域值，系統參數頁卻看不到它。
    /// </summary>
    [Fact]
    public void EveryResponsesAgent_ShouldHaveItsOwnReasoningSetting()
    {
        var keys = new[] { AgentPlaygroundAgent.Definition.ModelKey, AdminAssistantAgent.Home.ModelKey, AdminAssistantAgent.ScheduledPatrol.ModelKey }
            .Concat(AiWorkflowCatalog.All.Select(x => x.ModelKey))
            .Distinct()
            .ToArray();

        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.NotNull(typeof(AiReasoningSettings).GetProperty(key!, BindingFlags.Public | BindingFlags.Instance));
        }

        // 反過來：每個推論等級設定都有人用，而且都開放在系統參數頁。
        foreach (var property in typeof(AiReasoningSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Contains(property.Name, keys);
            var definition = SystemParameterCatalog.Find($"{AiReasoningSettings.SectionName}:{property.Name}");
            Assert.NotNull(definition);
            Assert.Equal(AiSettings.AgentReasoningEffortValues, definition!.AllowedValues);
        }
    }
}
