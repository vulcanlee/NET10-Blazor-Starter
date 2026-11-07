using System.Reflection;
using Microsoft.Extensions.Options;

namespace MyProject.Web.Configuration.Validation;

/// <summary><c>AiReasoningSettings</c>（0.9.168 起）：每個值必須是空白，或 <see cref="AiSettings.AgentReasoningEffortValues"/> 其中之一（不分大小寫）。</summary>
public sealed class AiReasoningSettingsValidator : IValidateOptions<AiReasoningSettings>
{
    public ValidateOptionsResult Validate(string? name, AiReasoningSettings options)
    {
        var errors = new OptionsErrors();

        foreach (var property in typeof(AiReasoningSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = (property.GetValue(options) as string)?.Trim();
            if (string.IsNullOrEmpty(value) == false
                && !AiSettings.AgentReasoningEffortValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(
                    $"{AiReasoningSettings.SectionName}:{property.Name}",
                    $"只接受空白（沿用 AiSettings:AgentReasoningEffort）或 {string.Join("、", AiSettings.AgentReasoningEffortValues)}，目前是「{value}」。");
            }
        }

        return errors.ToResult();
    }
}
