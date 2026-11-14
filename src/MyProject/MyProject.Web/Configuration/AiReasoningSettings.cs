using System.Reflection;

namespace MyProject.Web.Configuration;

/// <summary>
/// 各 Agent 功能送出的推論等級（<c>reasoning.effort</c>，0.9.168 起，可在「系統參數」頁修改）。
///
/// 屬性名稱和 <see cref="AiModelSettings"/> 的同名鍵一致，以 <c>AiCallContext.ModelKey</c> 對應；
/// <b>空字串代表沿用 <see cref="AiSettings.AgentReasoningEffort"/></b>（預設不送，由模型決定）。
/// 只有走 Responses API 的 Agent 功能會送；AI 日誌分析、例外分析與分派判斷一律不送。
///
/// <para>
/// 新增一個走 Responses API 的 Agent：在這裡加一個同名的 string 屬性（預設空字串）、在 <c>SystemParameterCatalog.All</c>
/// 加一行、在 appsettings.json 的 <c>AiReasoningSettings</c> 區段加同名鍵（<c>AiReasoningSettingsTests</c> 守門）。
/// 不放進 <see cref="AiModelSettings"/>：它的 <c>OverriddenModels()</c> 會把每個 string 屬性都當成模型名稱。
/// </para>
/// </summary>
public class AiReasoningSettings
{
    public const string SectionName = "AiReasoningSettings";

    /// <summary>系統管理助理的對話與定時巡檢。</summary>
    public string AdminAssistant { get; set; } = string.Empty;

    /// <summary>
    /// 這個功能自己設定的推論等級（小寫）；<paramref name="featureKey"/> 為 null、沒有對應的設定，或值空白時回 null，
    /// 由 <c>AiAgentFactory</c> 改用 <see cref="AiSettings.AgentReasoningEffort"/>（兩者分開，錯誤訊息才指得出用到哪一個設定）。
    /// </summary>
    public string? GetOverride(string? featureKey)
    {
        if (string.IsNullOrWhiteSpace(featureKey))
        {
            return null;
        }

        var property = typeof(AiReasoningSettings).GetProperty(featureKey, BindingFlags.Public | BindingFlags.Instance);
        var value = (property?.GetValue(this) as string)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();
    }
}
