using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// Azure OpenAI 與 OpenAI 的位址與設定驗證測試。
/// 這是兩家唯一真正不同的地方，抽成純函式就能完整覆蓋，不必打真的 API。
///
/// 0.9.140 起 HTTP 由 OpenAI SDK 送出，這裡驗的是交給 SDK 的 base URI；SDK 實際接出的完整網址與
/// 認證標頭（Azure 送 api-key、OpenAI 送 Bearer）由 <see cref="AiLogAnalysisServiceTests"/> 在線路層驗證。
/// </summary>
public sealed class AiChatEndpointTests
{
    /// <summary>SDK 在 base URI 後面接上 <c>/chat/completions</c>；這裡照同樣方式組出完整網址再斷言。</summary>
    private static Uri RequestUri(AiSettings settings)
        => new($"{AiChatEndpoint.GetBaseUri(settings).AbsoluteUri.TrimEnd('/')}/chat/completions");

    /// <summary>哨兵金鑰：用來斷言錯誤訊息絕不回吐金鑰。</summary>
    private const string SentinelApiKey = "SENTINEL-DO-NOT-LEAK-abcdef123456";

    /// <summary>
    /// Azure 走 v1 路徑。入口網站「Azure OpenAI 端點」給的值已經以 /openai/v1 結尾，
    /// 直接接上 chat/completions 即可，不需要 api-version 查詢參數。
    /// </summary>
    [Fact]
    public void GetBaseUri_Azure_ShouldComposeV1Path()
    {
        var requestUri = RequestUri(CreateAzureSettings());

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/v1/chat/completions",
            requestUri.AbsoluteUri);
    }

    [Fact]
    public void GetBaseUri_Azure_ShouldNotSendApiVersion()
    {
        var requestUri = RequestUri(CreateAzureSettings());

        Assert.DoesNotContain("api-version", requestUri.AbsoluteUri);
        Assert.Equal(string.Empty, requestUri.Query);
    }

    /// <summary>
    /// 舊習慣常常只貼資源根網址，補齊 /openai/v1 讓兩種貼法都能用
    /// （少了它會 404，而 404 的訊息看不出是路徑不完整）。
    /// </summary>
    [Theory]
    [InlineData("https://contoso.openai.azure.com")]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com")]
    public void GetBaseUri_Azure_ShouldAppendV1Suffix_WhenMissing(string endpoint)
    {
        var settings = CreateAzureSettings();
        settings.Endpoint = endpoint;

        var requestUri = RequestUri(settings);

        Assert.EndsWith("/openai/v1/chat/completions", requestUri.AbsoluteUri);
        Assert.DoesNotContain("/openai/v1/openai/v1", requestUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://contoso.openai.azure.com/openai/v1")]
    [InlineData("https://contoso.openai.azure.com/openai/v1/")]
    [InlineData("https://contoso.openai.azure.com/OPENAI/V1")]
    public void GetBaseUri_Azure_ShouldNotDuplicateV1Suffix(string endpoint)
    {
        var settings = CreateAzureSettings();
        settings.Endpoint = endpoint;

        var requestUri = RequestUri(settings);

        Assert.EndsWith("/chat/completions", requestUri.AbsoluteUri);
        Assert.DoesNotContain("v1/openai", requestUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Foundry 的「專案端點」是給 Agent SDK 用的，填錯時至少不要組出更奇怪的網址。</summary>
    [Fact]
    public void GetBaseUri_Azure_ShouldStillComposeUrl_WhenProjectEndpointPasted()
    {
        var settings = CreateAzureSettings();
        settings.Endpoint = "https://contoso.services.ai.azure.com/api/projects/proj-default";

        var requestUri = RequestUri(settings);

        Assert.EndsWith("/openai/v1/chat/completions", requestUri.AbsoluteUri);
    }

    [Fact]
    public void GetBaseUri_ShouldTolerateTrailingSlashInEndpoint()
    {
        var settings = CreateAzureSettings();
        settings.Endpoint = "https://contoso.openai.azure.com/  ";

        var requestUri = RequestUri(settings);

        Assert.DoesNotContain("azure.com//", requestUri.AbsoluteUri);
    }

    [Fact]
    public void GetBaseUri_OpenAi_ShouldComposeV1ChatCompletionsPath()
    {
        var requestUri = RequestUri(CreateOpenAiSettings());

        Assert.Equal("https://api.openai.com/v1/chat/completions", requestUri.AbsoluteUri);
    }

    [Fact]
    public void GetBaseUri_OpenAi_ShouldHonourCustomEndpoint()
    {
        var settings = CreateOpenAiSettings();
        settings.Endpoint = "https://gateway.internal/openai";

        var requestUri = RequestUri(settings);

        Assert.Equal("https://gateway.internal/openai/v1/chat/completions", requestUri.AbsoluteUri);
    }

    /// <summary>
    /// OpenAI 官方文件的 base_url 就是 https://api.openai.com/v1，很多人會照著填。
    /// 少了正規化會組出 /v1/v1/chat/completions。
    /// </summary>
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/")]
    [InlineData("https://api.openai.com/V1")]
    public void GetBaseUri_OpenAi_ShouldNotDuplicateV1Suffix(string endpoint)
    {
        var settings = CreateOpenAiSettings();
        settings.Endpoint = endpoint;

        var requestUri = RequestUri(settings);

        Assert.DoesNotContain("/v1/v1", requestUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/chat/completions", requestUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("https://api.openai.com/")]
    [InlineData("  https://api.openai.com  ")]
    public void GetBaseUri_OpenAi_ShouldAppendV1Suffix_WhenMissing(string endpoint)
    {
        var settings = CreateOpenAiSettings();
        settings.Endpoint = endpoint;

        var requestUri = RequestUri(settings);

        Assert.Equal("https://api.openai.com/v1/chat/completions", requestUri.AbsoluteUri);
    }

    [Fact]
    public void Validate_ShouldRejectBlankApiKey()
    {
        var settings = CreateAzureSettings();
        settings.ApiKey = "  ";

        Assert.Contains("AiSettings:ApiKey", AiChatEndpoint.Validate(settings));
    }

    [Fact]
    public void Validate_Azure_ShouldRejectBlankEndpoint()
    {
        var settings = CreateAzureSettings();
        settings.Endpoint = string.Empty;

        Assert.Contains("AiSettings:Endpoint", AiChatEndpoint.Validate(settings));
    }

    /// <summary>Azure 的 Model 就是部署名稱，錯誤訊息要講清楚，不然使用者會去填模型名稱。</summary>
    [Fact]
    public void Validate_Azure_ShouldRejectBlankModel_AndSayDeploymentName()
    {
        var settings = CreateAzureSettings();
        settings.Model = string.Empty;

        var message = AiChatEndpoint.Validate(settings);

        Assert.Contains("AiSettings:Model", message);
        Assert.Contains("部署名稱", message);
    }

    [Fact]
    public void Validate_OpenAi_ShouldNotRequireEndpoint()
    {
        var settings = CreateOpenAiSettings();
        settings.Endpoint = string.Empty;

        Assert.Null(AiChatEndpoint.Validate(settings));
    }

    [Fact]
    public void Validate_OpenAi_ShouldRejectBlankModel()
    {
        var settings = CreateOpenAiSettings();
        settings.Model = string.Empty;

        Assert.Contains("AiSettings:Model", AiChatEndpoint.Validate(settings));
    }

    /// <summary>
    /// ⚠️ Provider 打錯字時 Validate 必須回訊息而不是丟例外。
    /// 呼叫端 AiLogAnalysisService.IsAvailable 是在 LogsController.Options（日誌檢視頁開頁時呼叫）裡讀的，
    /// 外面沒有 try-catch —— 丟出去會讓那支 API 500，而不只是停用 AI 按鈕。
    /// </summary>
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("azure-openai")]
    [InlineData("Gemini")]
    public void Validate_ShouldReportUnsupportedProvider_WithoutThrowing(string provider)
    {
        var settings = CreateAzureSettings();
        settings.Provider = provider;

        var message = AiChatEndpoint.Validate(settings);

        Assert.NotNull(message);
        Assert.Contains(provider, message);
    }

    [Fact]
    public void Validate_ShouldPass_WhenFullyConfigured()
    {
        Assert.Null(AiChatEndpoint.Validate(CreateAzureSettings()));
        Assert.Null(AiChatEndpoint.Validate(CreateOpenAiSettings()));
    }

    /// <summary>
    /// 設定驗證的訊息會直接顯示給使用者（Tooltip 與 toast），絕不可夾帶金鑰。
    /// </summary>
    [Fact]
    public void Validate_ShouldNeverEchoApiKey()
    {
        var cases = new List<AiSettings>();

        var badProvider = CreateAzureSettings();
        badProvider.Provider = "Anthropic";
        cases.Add(badProvider);

        var noEndpoint = CreateAzureSettings();
        noEndpoint.Endpoint = string.Empty;
        cases.Add(noEndpoint);

        var azureNoModel = CreateAzureSettings();
        azureNoModel.Model = string.Empty;
        cases.Add(azureNoModel);

        var openAiNoModel = CreateOpenAiSettings();
        openAiNoModel.Model = string.Empty;
        cases.Add(openAiNoModel);

        foreach (var settings in cases)
        {
            var message = AiChatEndpoint.Validate(settings);
            Assert.NotNull(message);
            Assert.DoesNotContain(SentinelApiKey, message);
        }
    }

    private static AiSettings CreateAzureSettings() => new()
    {
        Provider = nameof(AiProvider.AzureOpenAI),
        // 入口網站「Azure OpenAI 端點」欄位給的就是這個形狀。
        Endpoint = "https://contoso.openai.azure.com/openai/v1",
        ApiKey = SentinelApiKey,
        Model = "gpt-4o-mini",
    };

    private static AiSettings CreateOpenAiSettings() => new()
    {
        Provider = nameof(AiProvider.OpenAI),
        Endpoint = string.Empty,
        ApiKey = SentinelApiKey,
        Model = "gpt-4o-mini",
    };
}
