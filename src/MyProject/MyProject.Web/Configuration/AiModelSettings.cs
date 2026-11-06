using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace MyProject.Web.Configuration;

/// <summary>
/// 各 AI 功能／Agent 使用的模型（0.9.142 起，可在「系統參數」頁修改）。
///
/// 每個屬性對應一個功能，值是模型名稱（Azure OpenAI 為部署名稱）；<b>空字串代表用 <see cref="AiSettings.Model"/></b>。
/// 端點與金鑰共用 <see cref="AiSettings"/>，所以只能換同一個服務裡的模型。
///
/// <para>
/// 新增一個 Agent 要用自己的模型：在這裡加一個 string 屬性（預設空字串）、在 <c>SystemParameterCatalog.All</c>
/// 加一行、在 appsettings.json 的 <c>AiModelSettings</c> 區段加同名鍵，呼叫端在 <c>AiCallContext.ModelKey</c>
/// 帶 <c>nameof(AiModelSettings.屬性)</c>。記帳與費用一律依「實際送出的模型」計算，新模型記得補費率。
/// </para>
/// </summary>
public class AiModelSettings
{
    public const string SectionName = "AiModelSettings";

    /// <summary>模型名稱長度上限（Azure 部署名稱最長 64，OpenAI 模型 id 更短）。</summary>
    public const int MaxModelLength = 100;

    /// <summary>AI 日誌分析。</summary>
    [MaxLength(MaxModelLength)]
    public string LogAnalysis { get; set; } = string.Empty;

    /// <summary>AI 例外分析（含追問）。</summary>
    [MaxLength(MaxModelLength)]
    public string ExceptionAnalysis { get; set; } = string.Empty;

    /// <summary>Agent 測試台。</summary>
    [MaxLength(MaxModelLength)]
    public string AgentPlayground { get; set; } = string.Empty;

    /// <summary>Agent 工作流程「專案狀態報告」（0.9.144 起；流程裡每個 agent 都用它）。</summary>
    [MaxLength(MaxModelLength)]
    public string ProjectStatusReport { get; set; } = string.Empty;

    /// <summary>Agent 工作流程「多觀點審查」（0.9.144 起；流程裡每個 agent 都用它）。</summary>
    [MaxLength(MaxModelLength)]
    public string MultiPerspectiveReview { get; set; } = string.Empty;

    /// <summary>系統管理助理（0.9.155 起）的對話。</summary>
    [MaxLength(MaxModelLength)]
    public string AdminAssistant { get; set; } = string.Empty;

    /// <summary>系統管理助理每則訊息前的分派判斷（0.9.155 起）；空白時用 <see cref="AdminAssistant"/>。</summary>
    [MaxLength(MaxModelLength)]
    public string AdminAssistantRouter { get; set; } = string.Empty;

    /// <summary>
    /// 解析實際要用的模型。<paramref name="modelKey"/> 為 null 或該功能沒設定時回傳 <paramref name="defaultModel"/>。
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="modelKey"/> 不是這個類別的屬性（程式寫錯，不是設定錯）。</exception>
    public string Resolve(string? modelKey, string defaultModel)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return defaultModel;
        }

        var property = typeof(AiModelSettings).GetProperty(modelKey, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"AiModelSettings 沒有 {modelKey} 這個模型設定。");

        var value = (property.GetValue(this) as string)?.Trim();
        return string.IsNullOrEmpty(value) ? defaultModel : value;
    }

    /// <summary>所有有覆寫的模型（不含預設模型），給健康監控檢查費率用。</summary>
    public IEnumerable<string> OverriddenModels()
        => typeof(AiModelSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => (property.GetValue(this) as string)?.Trim())
            .Where(value => string.IsNullOrEmpty(value) == false)
            .Select(value => value!);
}
