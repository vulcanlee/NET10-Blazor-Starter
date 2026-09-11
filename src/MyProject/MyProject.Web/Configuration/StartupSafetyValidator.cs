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

        // AI 分析是 opt-in，而「有沒有填金鑰」就是那個開關：沒填等於功能關閉，
        // 腳手架不帶金鑰也能在 Production 正常啟動，所以不必擋。
        // 反過來，有填金鑰代表操作者打算啟用，此時其餘設定必須完整，否則啟動就擋下 ——
        // 那種錯誤等到使用者按下按鈕才發現，遠比啟動時就攔住昂貴。
        if (string.IsNullOrWhiteSpace(configuration[$"{AiSettings.SectionName}:ApiKey"]) == false)
        {
            var aiProvider = configuration[$"{AiSettings.SectionName}:Provider"];
            var isAzure = string.IsNullOrWhiteSpace(aiProvider)
                || string.Equals(aiProvider, nameof(AiProvider.AzureOpenAI), StringComparison.OrdinalIgnoreCase);
            if (isAzure && string.IsNullOrWhiteSpace(configuration[$"{AiSettings.SectionName}:Endpoint"]))
            {
                errors.Add("AiSettings:Endpoint 在 Production 使用 Azure OpenAI 時不可留空。");
            }

            if (string.IsNullOrWhiteSpace(configuration[$"{AiSettings.SectionName}:Model"]))
            {
                errors.Add("AiSettings:Model 在 Production 設定 AI 金鑰時不可留空（Azure 請填部署名稱）。");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Production 啟動安全檢查失敗：" + string.Join(" ", errors));
        }
    }
}
