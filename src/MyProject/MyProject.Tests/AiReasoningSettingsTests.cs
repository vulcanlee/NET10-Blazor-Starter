using System.Reflection;
using MyProject.Web.Ai.Agents.AdminAssistant;
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
        var settings = new AiReasoningSettings { AdminAssistant = " LOW " };

        Assert.Equal("low", settings.GetOverride(nameof(AiReasoningSettings.AdminAssistant)));
    }

    /// <summary>沒有自己的值時回 null，由 <c>AiAgentFactory</c> 改用 <c>AiSettings:AgentReasoningEffort</c>。</summary>
    [Theory]
    [InlineData("AdminAssistant", "")]
    [InlineData("AdminAssistant", "   ")]
    [InlineData(null, "low")]
    [InlineData("LogAnalysis", "low")]
    [InlineData("AdminAssistantRouter", "low")]
    public void GetOverride_ShouldBeNull_WhenTheFeatureHasNoValueOfItsOwn(string? key, string adminAssistant)
    {
        var settings = new AiReasoningSettings { AdminAssistant = adminAssistant };

        Assert.Null(settings.GetOverride(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("High")]
    [InlineData("xhigh")]
    public void Validator_ShouldAcceptBlankAndKnownValues(string value)
        => Assert.True(new AiReasoningSettingsValidator().Validate(null, new AiReasoningSettings { AdminAssistant = value }).Succeeded);

    [Fact]
    public void Validator_ShouldRejectUnknownValues_AndNameTheKey()
    {
        var result = new AiReasoningSettingsValidator().Validate(null, new AiReasoningSettings { AdminAssistant = "ultra" });

        Assert.True(result.Failed);
        Assert.Contains("AiReasoningSettings:AdminAssistant", result.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// 每個走 Responses API（會送推論等級）的 Agent，它的 <c>ModelKey</c> 都要有自己的推論等級設定；
    /// 否則新增的 Agent 會靜默沿用全域值，系統參數頁卻看不到它。
    /// </summary>
    [Fact]
    public void EveryResponsesAgent_ShouldHaveItsOwnReasoningSetting()
    {
        var keys = new[] { AdminAssistantAgent.Home.ModelKey, AdminAssistantAgent.ScheduledPatrol.ModelKey }
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
