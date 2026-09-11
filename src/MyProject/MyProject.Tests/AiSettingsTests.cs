using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// AiSettings 的 provider 解析與預設值測試。
/// 預設值有測試釘住，是為了避免有人悄悄改動而文件沒跟著更新。
/// </summary>
public sealed class AiSettingsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GetProvider_ShouldDefaultToAzureOpenAI_WhenBlank(string? provider)
    {
        var settings = new AiSettings { Provider = provider! };

        Assert.Equal(AiProvider.AzureOpenAI, settings.GetProvider());
    }

    [Theory]
    [InlineData("AzureOpenAI", AiProvider.AzureOpenAI)]
    [InlineData("azureopenai", AiProvider.AzureOpenAI)]
    [InlineData("AZUREOPENAI", AiProvider.AzureOpenAI)]
    [InlineData("OpenAI", AiProvider.OpenAI)]
    [InlineData("openai", AiProvider.OpenAI)]
    public void GetProvider_ShouldParseCaseInsensitively(string provider, AiProvider expected)
    {
        var settings = new AiSettings { Provider = provider };

        Assert.Equal(expected, settings.GetProvider());
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    [InlineData("azure-openai")]
    public void GetProvider_ShouldThrow_WhenUnsupported(string provider)
    {
        var settings = new AiSettings { Provider = provider };

        var exception = Assert.Throws<InvalidOperationException>(() => settings.GetProvider());
        Assert.Contains(provider, exception.Message);
    }

    /// <summary>
    /// 預設值必須與 appsettings.json 範本及 docs 的說明一致。
    /// Enabled 預設 false 是刻意的：腳手架不帶金鑰也要能直接跑起來。
    /// </summary>
    [Fact]
    public void Defaults_ShouldMatchDocumentedValues()
    {
        var settings = new AiSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(nameof(AiProvider.AzureOpenAI), settings.Provider);
        Assert.Equal(string.Empty, settings.Endpoint);
        Assert.Equal(string.Empty, settings.ApiKey);
        Assert.Equal(string.Empty, settings.Deployment);
        Assert.Equal("gpt-4o-mini", settings.Model);
        Assert.Equal("2024-10-21", settings.ApiVersion);
        Assert.Equal(string.Empty, settings.SystemPrompt);
        Assert.Equal(100, settings.MaxEntries);
        Assert.Equal(2000, settings.MaxCharactersPerEntry);
        Assert.Equal(120000, settings.MaxTotalCharacters);
        Assert.Equal(120, settings.TimeoutSeconds);
        Assert.Equal(2000, settings.MaxOutputTokens);
        Assert.Equal(0.2, settings.Temperature);
    }

    [Fact]
    public void SectionName_ShouldMatchAppSettingsKey()
    {
        Assert.Equal("AiSettings", AiSettings.SectionName);
    }
}
