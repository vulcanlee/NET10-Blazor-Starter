using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// Azure OpenAI 與 OpenAI 的位址、認證與設定驗證測試。
/// 這是兩家唯一真正不同的地方，抽成純函式就能完整覆蓋，不必打真的 API。
/// </summary>
public sealed class AiChatEndpointTests
{
    /// <summary>哨兵金鑰：用來斷言錯誤訊息絕不回吐金鑰。</summary>
    private const string SentinelApiKey = "SENTINEL-DO-NOT-LEAK-abcdef123456";

    [Fact]
    public void Create_Azure_ShouldComposeDeploymentPathWithApiVersion()
    {
        var descriptor = AiChatEndpoint.Create(CreateAzureSettings());

        Assert.Equal(
            "https://contoso.openai.azure.com/openai/deployments/gpt-4o-mini/chat/completions?api-version=2024-10-21",
            descriptor.RequestUri.AbsoluteUri);
    }

    [Fact]
    public void Create_Azure_ShouldUseApiKeyHeader()
    {
        var descriptor = AiChatEndpoint.Create(CreateAzureSettings());

        Assert.Equal("api-key", descriptor.AuthHeaderName);
        Assert.Equal(SentinelApiKey, descriptor.AuthHeaderValue);
    }

    [Fact]
    public void Create_Azure_ShouldFallBackToDefaultApiVersion_WhenBlank()
    {
        var settings = CreateAzureSettings();
        settings.ApiVersion = "   ";

        var descriptor = AiChatEndpoint.Create(settings);

        Assert.Contains($"api-version={AiChatEndpoint.DefaultAzureApiVersion}", descriptor.RequestUri.AbsoluteUri);
    }

    /// <summary>
    /// 部署名稱要做百分比編碼。實務上 Azure 的部署名稱只允許英數與 - _，這是防禦性處理。
    /// ⚠️ 斷言用 AbsoluteUri 而不是 ToString()：後者會把路徑中的 %20 顯示成空白（只是顯示
    /// 慣例），HttpClient 送出的是 AbsoluteUri 這個保留編碼的形式。
    /// </summary>
    [Fact]
    public void Create_Azure_ShouldEscapeDeploymentName()
    {
        var settings = CreateAzureSettings();
        settings.Deployment = "my deployment/v2";

        var descriptor = AiChatEndpoint.Create(settings);

        Assert.Contains("deployments/my%20deployment%2Fv2/chat/completions", descriptor.RequestUri.AbsoluteUri);
        Assert.Contains("deployments/my%20deployment%2Fv2/chat/completions", descriptor.RequestUri.PathAndQuery);
    }

    [Fact]
    public void Create_ShouldTolerateTrailingSlashInEndpoint()
    {
        var settings = CreateAzureSettings();
        settings.Endpoint = "https://contoso.openai.azure.com/  ";

        var descriptor = AiChatEndpoint.Create(settings);

        Assert.DoesNotContain("azure.com//", descriptor.RequestUri.AbsoluteUri);
    }

    [Fact]
    public void Create_OpenAi_ShouldComposeV1ChatCompletionsPath()
    {
        var descriptor = AiChatEndpoint.Create(CreateOpenAiSettings());

        Assert.Equal("https://api.openai.com/v1/chat/completions", descriptor.RequestUri.AbsoluteUri);
    }

    [Fact]
    public void Create_OpenAi_ShouldUseBearerAuthorizationHeader()
    {
        var descriptor = AiChatEndpoint.Create(CreateOpenAiSettings());

        Assert.Equal("Authorization", descriptor.AuthHeaderName);
        Assert.Equal($"Bearer {SentinelApiKey}", descriptor.AuthHeaderValue);
    }

    [Fact]
    public void Create_OpenAi_ShouldHonourCustomEndpoint()
    {
        var settings = CreateOpenAiSettings();
        settings.Endpoint = "https://gateway.internal/openai";

        var descriptor = AiChatEndpoint.Create(settings);

        Assert.Equal("https://gateway.internal/openai/v1/chat/completions", descriptor.RequestUri.AbsoluteUri);
    }

    [Fact]
    public void Validate_ShouldRejectDisabled()
    {
        var settings = CreateAzureSettings();
        settings.Enabled = false;

        Assert.Contains("AiSettings:Enabled", AiChatEndpoint.Validate(settings));
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

    [Fact]
    public void Validate_Azure_ShouldRejectBlankDeployment()
    {
        var settings = CreateAzureSettings();
        settings.Deployment = string.Empty;

        Assert.Contains("AiSettings:Deployment", AiChatEndpoint.Validate(settings));
    }

    [Fact]
    public void Validate_OpenAi_ShouldNotRequireDeployment()
    {
        var settings = CreateOpenAiSettings();
        settings.Deployment = string.Empty;

        Assert.Null(AiChatEndpoint.Validate(settings));
    }

    [Fact]
    public void Validate_OpenAi_ShouldRejectBlankModel()
    {
        var settings = CreateOpenAiSettings();
        settings.Model = string.Empty;

        Assert.Contains("AiSettings:Model", AiChatEndpoint.Validate(settings));
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

        var disabled = CreateAzureSettings();
        disabled.Enabled = false;
        cases.Add(disabled);

        var noEndpoint = CreateAzureSettings();
        noEndpoint.Endpoint = string.Empty;
        cases.Add(noEndpoint);

        var noDeployment = CreateAzureSettings();
        noDeployment.Deployment = string.Empty;
        cases.Add(noDeployment);

        var noModel = CreateOpenAiSettings();
        noModel.Model = string.Empty;
        cases.Add(noModel);

        foreach (var settings in cases)
        {
            var message = AiChatEndpoint.Validate(settings);
            Assert.NotNull(message);
            Assert.DoesNotContain(SentinelApiKey, message);
        }
    }

    [Fact]
    public void ResolveModelField_ShouldUseDeploymentForAzure()
    {
        Assert.Equal("gpt-4o-mini", AiChatEndpoint.ResolveModelField(CreateAzureSettings()));
    }

    [Fact]
    public void ResolveModelField_ShouldUseModelForOpenAi()
    {
        var settings = CreateOpenAiSettings();
        settings.Model = "gpt-4.1-mini";

        Assert.Equal("gpt-4.1-mini", AiChatEndpoint.ResolveModelField(settings));
    }

    private static AiSettings CreateAzureSettings() => new()
    {
        Enabled = true,
        Provider = nameof(AiProvider.AzureOpenAI),
        Endpoint = "https://contoso.openai.azure.com",
        ApiKey = SentinelApiKey,
        Deployment = "gpt-4o-mini",
        ApiVersion = "2024-10-21",
    };

    private static AiSettings CreateOpenAiSettings() => new()
    {
        Enabled = true,
        Provider = nameof(AiProvider.OpenAI),
        Endpoint = string.Empty,
        ApiKey = SentinelApiKey,
        Model = "gpt-4o-mini",
    };
}
