using MyProject.Web.Configuration;

namespace MyProject.Web.Ai;

/// <summary>
/// Azure OpenAI 與 OpenAI 的位址、認證與模型欄位差異全部收斂在此，且刻意做成純函式
/// —— 這是兩家唯一真正不同的地方，抽出來才測得到（不必真的打 API）。
///
/// <para>Azure：<c>POST {Endpoint}/openai/deployments/{Deployment}/chat/completions?api-version={ApiVersion}</c>
/// 搭配標頭 <c>api-key: {ApiKey}</c>。</para>
/// <para>OpenAI：<c>POST {Endpoint}/v1/chat/completions</c>
/// 搭配標頭 <c>Authorization: Bearer {ApiKey}</c>。</para>
/// </summary>
public static class AiChatEndpoint
{
    /// <summary>OpenAI 的預設位址（<c>AiSettings:Endpoint</c> 留空時使用）。</summary>
    public const string DefaultOpenAiEndpoint = "https://api.openai.com";

    /// <summary>Azure 的 <c>api-version</c> 後備值（設定留空時使用）。</summary>
    public const string DefaultAzureApiVersion = "2024-10-21";

    public static AiChatEndpointDescriptor Create(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var provider = settings.GetProvider();
        var baseUrl = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? (provider == AiProvider.OpenAI ? DefaultOpenAiEndpoint : string.Empty)
            : settings.Endpoint.Trim().TrimEnd('/');

        if (provider == AiProvider.OpenAI)
        {
            return new AiChatEndpointDescriptor(
                new Uri($"{baseUrl}/v1/chat/completions"),
                "Authorization",
                $"Bearer {settings.ApiKey}");
        }

        var apiVersion = string.IsNullOrWhiteSpace(settings.ApiVersion)
            ? DefaultAzureApiVersion
            : settings.ApiVersion.Trim();

        var uri = $"{baseUrl}/openai/deployments/{Uri.EscapeDataString(settings.Deployment)}"
            + $"/chat/completions?api-version={Uri.EscapeDataString(apiVersion)}";

        return new AiChatEndpointDescriptor(new Uri(uri), "api-key", settings.ApiKey);
    }

    /// <summary>
    /// 設定完整性檢查。回傳 <c>null</c> 代表可用，否則回傳給使用者看的中文原因。
    /// ⚠️ 訊息只能指出「要設哪個鍵」，絕不可回吐任何金鑰片段（有測試守門）。
    /// </summary>
    public static string? Validate(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Enabled == false)
        {
            return "AI 分析尚未啟用，請將 AiSettings:Enabled 設為 true。";
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return "AI 分析尚未設定 API 金鑰，請於 User Secrets 或環境變數設定 AiSettings:ApiKey。";
        }

        if (settings.GetProvider() == AiProvider.AzureOpenAI)
        {
            if (string.IsNullOrWhiteSpace(settings.Endpoint))
            {
                return "AI 分析尚未設定 AiSettings:Endpoint（Azure OpenAI 資源網址）。";
            }

            if (string.IsNullOrWhiteSpace(settings.Deployment))
            {
                return "AI 分析尚未設定 AiSettings:Deployment（Azure OpenAI 部署名稱）。";
            }
        }
        else if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return "AI 分析尚未設定 AiSettings:Model（OpenAI 模型名稱）。";
        }

        return null;
    }

    /// <summary>
    /// 送進請求 body 的 <c>model</c> 欄位值：Azure 用部署名稱、OpenAI 用模型 id。
    /// Azure 實際上會忽略 body 的 model（以 URL 的 deployment 為準），照樣帶上是為了讓
    /// 請求內容可斷言，也讓稽核明細有東西可寫。
    /// </summary>
    public static string ResolveModelField(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetProvider() == AiProvider.AzureOpenAI ? settings.Deployment : settings.Model;
    }
}
