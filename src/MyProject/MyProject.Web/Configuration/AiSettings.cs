namespace MyProject.Web.Configuration;

/// <summary>
/// AI 分析設定（日誌檢視頁的「AI 分析」功能）。
///
/// ⚠️ <see cref="ApiKey"/> 在 appsettings.json 一律留空字串當範本，實際值請放
/// User Secrets、環境變數（鍵名 <c>AiSettings__ApiKey</c>）或 appsettings.Development.json，
/// 與 GoogleOAuthSettings 的既有作法一致。
/// </summary>
public class AiSettings
{
    public const string SectionName = "AiSettings";

    /// <summary>
    /// 功能開關。預設關閉，讓腳手架不帶金鑰也能直接跑起來；
    /// 關閉時日誌檢視頁的 AI 分析按鈕會停用並以 Tooltip 說明原因。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 服務供應商：<c>AzureOpenAI</c> 或 <c>OpenAI</c>。
    /// 刻意存字串再由 <see cref="GetProvider"/> 解析（照 CacheSettings 的既有慣例），
    /// 這樣值打錯時丟的是可控的中文訊息，而不是組態繫結的框架英文例外。
    /// </summary>
    public string Provider { get; set; } = nameof(AiProvider.AzureOpenAI);

    /// <summary>
    /// Azure OpenAI：資源網址，例如 <c>https://your-resource.openai.azure.com</c>（必填）。
    /// OpenAI：留空即使用 <see cref="Ai.AiChatEndpoint.DefaultOpenAiEndpoint"/>。
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API 金鑰。⚠️ 見類別註解，不要寫進 appsettings.json。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure OpenAI 專用：部署名稱。OpenAI 不使用此欄位。</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>OpenAI 專用：模型 id。Azure OpenAI 以 <see cref="Deployment"/> 為準。</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Azure OpenAI 專用：<c>api-version</c> 查詢參數。缺這個參數 Azure 會回 404。</summary>
    public string ApiVersion { get; set; } = "2024-10-21";

    /// <summary>
    /// 系統提示詞。留空代表沿用 <see cref="Ai.AiPromptDefaults.SystemPrompt"/> 的內建值。
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>送出給 AI 的日誌筆數上限。超過時取最新的 N 筆。</summary>
    public int MaxEntries { get; set; } = 100;

    /// <summary>單筆日誌的字元上限，避免一筆很長的例外堆疊就把預算吃光。</summary>
    public int MaxCharactersPerEntry { get; set; } = 2000;

    /// <summary>所有日誌合計的字元上限，第二道保護。</summary>
    public int MaxTotalCharacters { get; set; } = 120000;

    /// <summary>HTTP 逾時秒數。AI 回應慢，預設遠高於一般 API。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>回應長度上限（送出的 <c>max_completion_tokens</c>）。</summary>
    public int MaxOutputTokens { get; set; } = 2000;

    /// <summary>
    /// 取樣溫度。<c>null</c> 代表整個欄位都不送 —— 推論模型（o 系列）不接受此參數。
    /// ⚠️ 要停用請設 <c>null</c>，**不要**設空字串：空字串會讓組態繫結在第一次讀取
    /// 設定時（也就是使用者按下按鈕時）才丟例外，而不是啟動時。
    /// </summary>
    public double? Temperature { get; set; } = 0.2;

    /// <summary>把 <see cref="Provider"/> 字串解析成列舉。空字串視為 Azure OpenAI。</summary>
    /// <exception cref="InvalidOperationException">值不是支援的供應商名稱。</exception>
    public AiProvider GetProvider()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            return AiProvider.AzureOpenAI;
        }

        if (Enum.TryParse<AiProvider>(Provider, ignoreCase: true, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"不支援的 AI provider：{Provider}");
    }
}

public enum AiProvider
{
    AzureOpenAI,
    OpenAI
}
