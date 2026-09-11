using MyProject.Web.Auth;

namespace MyProject.Web.Configuration;

public static class StartupSafetyValidator
{
    private const string DevelopmentSigningKey = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars";

    public static void Validate(IConfiguration configuration, string environmentName)
    {
        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errors = new List<string>();
        var signingKey = configuration[$"{JwtSettings.SectionName}:SigningKey"];
        if (string.Equals(signingKey, DevelopmentSigningKey, StringComparison.Ordinal))
        {
            errors.Add("JwtSettings:SigningKey 不可在 Production 使用開發預設值。");
        }

        var supportPassword = configuration["BootstrapSettings:SupportPassword"];
        if (string.Equals(supportPassword, "support", StringComparison.Ordinal))
        {
            errors.Add("BootstrapSettings:SupportPassword 不可在 Production 使用預設密碼。");
        }

        if (string.IsNullOrWhiteSpace(configuration[$"{SwaggerSettings.SectionName}:EnabledInProduction"]))
        {
            errors.Add("Swagger:EnabledInProduction 必須在 Production 明確設定 true 或 false。");
        }

        var cacheProvider = configuration[$"{CacheSettings.SectionName}:Provider"];
        if (string.Equals(cacheProvider, nameof(CacheProvider.Redis), StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(configuration[$"{CacheSettings.SectionName}:RedisConnection"]))
        {
            errors.Add("CacheSettings:RedisConnection 在 Production 使用 Redis provider 時不可留空。");
        }

        // AI 分析是 opt-in：Enabled 為 false 時刻意不檢查，腳手架不帶金鑰也要能在 Production 啟動。
        if (string.Equals(configuration[$"{AiSettings.SectionName}:Enabled"], bool.TrueString,
                StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuration[$"{AiSettings.SectionName}:ApiKey"]))
            {
                errors.Add("AiSettings:ApiKey 在 Production 啟用 AI 分析時不可留空，請改用 User Secrets 或環境變數。");
            }

            var aiProvider = configuration[$"{AiSettings.SectionName}:Provider"];
            var isAzure = string.IsNullOrWhiteSpace(aiProvider)
                || string.Equals(aiProvider, nameof(AiProvider.AzureOpenAI), StringComparison.OrdinalIgnoreCase);
            if (isAzure)
            {
                if (string.IsNullOrWhiteSpace(configuration[$"{AiSettings.SectionName}:Endpoint"]))
                {
                    errors.Add("AiSettings:Endpoint 在 Production 使用 Azure OpenAI 時不可留空。");
                }

                if (string.IsNullOrWhiteSpace(configuration[$"{AiSettings.SectionName}:Deployment"]))
                {
                    errors.Add("AiSettings:Deployment 在 Production 使用 Azure OpenAI 時不可留空。");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Production 啟動安全檢查失敗：" + string.Join(" ", errors));
        }
    }
}
