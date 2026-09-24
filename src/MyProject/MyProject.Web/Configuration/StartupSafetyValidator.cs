using MyProject.Web.Auth;

namespace MyProject.Web.Configuration;

public static class StartupSafetyValidator
{
    /// <summary>
    /// 範本出貨的預設管理者密碼（現行為 <c>support</c>）。**同樣是「值會變、比對要跟著變」的地方**：
    /// <c>StartupSafetyConventionTests</c> 會讀 <c>appsettings.json</c> 實際出貨的值來守門，
    /// 日後換預設值卻忘了加進這份清單，測試就會紅。
    /// </summary>
    private static readonly string[] TemplateSupportPasswords = ["support"];

    public static void Validate(IConfiguration configuration, string environmentName)
    {
        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errors = new List<string>();
        var signingKey = configuration[$"{JwtSettings.SectionName}:SigningKey"];
        if (JwtSettings.IsPlaceholderSigningKey(signingKey))
        {
            errors.Add("JwtSettings:SigningKey 不可在 Production 使用範本的佔位金鑰。");
        }

        var supportPassword = configuration["BootstrapSettings:SupportPassword"];
        if (string.IsNullOrWhiteSpace(supportPassword))
        {
            // 留空不是「不要設定」，而是「把空字串雜湊成管理員密碼」（Program.cs 的 seed），
            // 而且每次重啟都重新套用一次。必須比照 SigningKey 留空一樣擋下。
            errors.Add("BootstrapSettings:SupportPassword 不可在 Production 留空，否則預設管理者會被設成空密碼。");
        }
        else if (TemplateSupportPasswords.Contains(supportPassword, StringComparer.Ordinal))
        {
            errors.Add("BootstrapSettings:SupportPassword 不可在 Production 使用範本的預設密碼。");
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

        // 寄信：None（未啟用）不擋；Pickup 會把信（含日後的密碼重設連結）以明文寫進主機磁碟，
        // 能讀到那個資料夾的人就能重設任何帳號，Production 一律拒絕。
        // Smtp 的 Host／FromAddress 在所有環境都由 ValidateOnStart 檢查，這裡只補 Production 才需要的公開網址。
        var emailProvider = configuration[$"{EmailSettings.SectionName}:Provider"];
        if (string.Equals(emailProvider, nameof(EmailProvider.Pickup), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("EmailSettings:Provider 不可在 Production 使用 Pickup（信件會以明文寫入主機磁碟）。");
        }
        else if (string.Equals(emailProvider, nameof(EmailProvider.Smtp), StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuration[$"{EmailSettings.SectionName}:Host"]))
            {
                errors.Add("EmailSettings:Host 在 Production 使用 Smtp 時不可留空。");
            }

            if (string.IsNullOrWhiteSpace(configuration[$"{EmailSettings.SectionName}:FromAddress"]))
            {
                errors.Add("EmailSettings:FromAddress 在 Production 使用 Smtp 時不可留空。");
            }

            var publicBaseUrl = configuration[$"{EmailSettings.SectionName}:PublicBaseUrl"];
            if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            {
                errors.Add("EmailSettings:PublicBaseUrl 在 Production 使用 Smtp 時必須是完整的 http(s) 網址（信中連結以它為準，不採用請求的 Host header）。");
            }
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
