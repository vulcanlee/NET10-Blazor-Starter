using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Web.Configuration.Parameters;

/// <summary>
/// 可在「系統參數」頁修改的設定鍵（0.9.98 起）—— <b>唯一的定義來源</b>。不在這裡的鍵，資料庫裡就算有也不會套用。
///
/// 只放符合下列條件的值（速查表「系統參數」）：
/// <list type="bullet">
/// <item>非機密（密碼、金鑰、連線字串一律留在 appsettings 或環境變數）。</item>
/// <item>執行期讀得到新值：讀取端用 <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c>（或 <c>ISystemIdentity</c>），不是啟動時讀一次。</item>
/// <item>單一純量（int／bool／string）；陣列與字典（例如告警收件人、各作業的 cron）不放。</item>
/// <item>屬於啟動時驗證的設定類別，預設值仍寫在 appsettings。</item>
/// </list>
///
/// 新增一個鍵：在 <see cref="All"/> 加一行 <see cref="Define{TOptions, TValue}"/>，並寫進
/// <c>docs/operations/日誌與設定檔說明.md</c> 的系統參數表（<c>SystemParameterCatalogTests</c> 會檢查綁定、範圍、白名單與文件）。
/// </summary>
public static class SystemParameterCatalog
{
    public const string GroupIdentity = "系統識別";
    public const string GroupRetention = "資料保留";
    public const string GroupMonitoring = "監控與告警";
    public const string GroupSecurity = "安全與流量";

    private const string NextRetentionRun = "下一次排程清理時（預設每天 03:00）";

    public static IReadOnlyList<SystemParameterDefinition> All { get; } =
    [
        Define<SystemSettings, string>(nameof(SystemSettings), x => x.SystemInformation.SystemName, GroupIdentity,
            "系統名稱", "登入頁、側邊欄、瀏覽器分頁、「關於」、PDF 報告與系統寄出的信件都會顯示。",
            "信件與 PDF 立即；已開啟的畫面在換頁或重新整理後", required: true, maxLength: 50),
        Define<SystemSettings, string>(nameof(SystemSettings), x => x.SystemInformation.SystemDescription, GroupIdentity,
            "系統簡介", "登入頁、首頁與「關於」顯示的一句話說明；可以留空。",
            "已開啟的畫面在換頁或重新整理後", maxLength: 200),

        Define<LogRetentionSettings, int>(LogRetentionSettings.SectionName, x => x.ExceptionLogDays, GroupRetention,
            "系統例外紀錄保留天數", "最後發生早於這麼多天前的例外紀錄（連同堆疊檔）會被自動刪除。",
            NextRetentionRun + "；「系統例外紀錄」頁的清除按鈕立即使用", unit: "天", zeroMeaning: "不自動清除", isRetention: true),
        Define<LogRetentionSettings, int>(LogRetentionSettings.SectionName, x => x.AuditLogDays, GroupRetention,
            "稽核紀錄保留天數", "早於這麼多天前的稽核紀錄會被自動刪除。稽核是責任證據，有保存規定時請依規定設定。",
            NextRetentionRun + "；「稽核紀錄」頁的清除按鈕立即使用", unit: "天", zeroMeaning: "不自動清除", isRetention: true),
        Define<LogRetentionSettings, int>(LogRetentionSettings.SectionName, x => x.TokenUsageLogDays, GroupRetention,
            "Token 用量紀錄保留天數", "早於這麼多天前的 Token 用量紀錄（連同原始檔）會被自動刪除。",
            NextRetentionRun, unit: "天", zeroMeaning: "不自動清除", isRetention: true),
        Define<AiCallLogSettings, int>(AiCallLogSettings.SectionName, x => x.RetentionDays, GroupRetention,
            "AI 對話紀錄保留天數", "早於這麼多天前的 AI 對話紀錄（連同內容檔）會被自動刪除。內容含日誌與例外堆疊，不宜留太久。",
            NextRetentionRun, unit: "天", isRetention: true),
        Define<AiCallLogSettings, bool>(AiCallLogSettings.SectionName, x => x.Enabled, GroupRetention,
            "記錄 AI 對話內容", "是否把每次 AI 呼叫的完整請求與回應記進「AI 對話紀錄」。關閉後不再記錄，舊紀錄照樣依天數過期。",
            "下一次 AI 呼叫起"),
        Define<SoftDeleteSettings, int>(SoftDeleteSettings.SectionName, x => x.PurgeAfterDays, GroupRetention,
            "已刪除資料保留天數", "專案、分類、團隊、使用者、角色被刪除超過這麼多天，就永久刪除（專案連同附件檔）。",
            NextRetentionRun, unit: "天", zeroMeaning: "不自動永久刪除", isRetention: true),
        Define<ScheduledJobSettings, int>(ScheduledJobSettings.SectionName, x => x.JobRunRetentionDays, GroupRetention,
            "排程執行紀錄保留天數", "「排程作業」頁的執行紀錄保留多久。",
            "下一次任何排程作業執行完時", unit: "天", zeroMeaning: "不清除", min: 0, max: 36500, isRetention: true),

        Define<SlowOperationSettings, int>(SlowOperationSettings.SectionName, x => x.HttpRequestMs, GroupMonitoring,
            "慢請求門檻", "一個 HTTP 請求超過這麼久，就在日誌記一筆警告。",
            "立即", unit: "毫秒", zeroMeaning: "不記錄"),
        Define<SlowOperationSettings, int>(SlowOperationSettings.SectionName, x => x.DbCommandMs, GroupMonitoring,
            "慢查詢門檻", "一個資料庫指令超過這麼久，就在日誌記一筆警告。",
            "立即", unit: "毫秒", zeroMeaning: "不記錄"),
        Define<SlowOperationSettings, int>(SlowOperationSettings.SectionName, x => x.ExternalCallMs, GroupMonitoring,
            "慢寄信門檻", "寄出一封信超過這麼久，就在日誌記一筆警告。",
            "立即", unit: "毫秒", zeroMeaning: "不記錄"),
        Define<SlowOperationSettings, int>(SlowOperationSettings.SectionName, x => x.AiCallMs, GroupMonitoring,
            "慢 AI 呼叫門檻", "一次 AI 呼叫超過這麼久，就在日誌記一筆警告。",
            "立即", unit: "毫秒", zeroMeaning: "不記錄"),
        Define<ExceptionAlertSettings, int>(ExceptionAlertSettings.SectionName, x => x.BurstThreshold, GroupMonitoring,
            "例外告警：暴增門檻", "同一段時間內例外發生這麼多次，就寄告警信。收件人在設定檔，沒有收件人時不寄。",
            "立即（下一個例外起）", unit: "次"),
        Define<ExceptionAlertSettings, int>(ExceptionAlertSettings.SectionName, x => x.BurstWindowMinutes, GroupMonitoring,
            "例外告警：暴增視窗", "計算「暴增」的時間長度。",
            "立即（下一個例外起）", unit: "分鐘"),
        Define<ExceptionAlertSettings, int>(ExceptionAlertSettings.SectionName, x => x.PerSignatureCooldownMinutes, GroupMonitoring,
            "例外告警：同類冷卻時間", "同一種例外寄過告警後，這段時間內不再為它寄信。",
            "立即（下一個例外起）", unit: "分鐘", zeroMeaning: "不冷卻"),
        Define<ExceptionAlertSettings, int>(ExceptionAlertSettings.SectionName, x => x.MaxEmailsPerHour, GroupMonitoring,
            "例外告警：每小時上限", "全系統每小時最多寄出幾封告警信，超過的併入下一封。",
            "立即（下一個例外起）", unit: "封"),
        Define<ClientErrorReportingSettings, bool>(ClientErrorReportingSettings.SectionName, x => x.Enabled, GroupMonitoring,
            "回報瀏覽器端錯誤", "使用者瀏覽器裡的 JavaScript 錯誤是否記進「系統例外紀錄」。",
            "關閉立即生效；開啟只對之後才載入的頁面"),
        Define<ClientErrorReportingSettings, int>(ClientErrorReportingSettings.SectionName, x => x.MaxPerCircuitPerMinute, GroupMonitoring,
            "瀏覽器錯誤每分鐘上限", "每個開著的頁面每分鐘最多回報幾筆，避免一個壞掉的頁面灌爆例外紀錄。",
            "立即", unit: "筆"),

        Define<RateLimitSettings, int>(RateLimitSettings.SectionName, x => x.ApiRequestsPerMinute, GroupSecurity,
            "API 每分鐘請求上限", "每個使用者（未登入則每個 IP）每分鐘可以呼叫 Web API 幾次，超過回 429。",
            "立即（計數重新開始）", unit: "次", min: 1, max: 100_000),
        Define<RateLimitSettings, int>(RateLimitSettings.SectionName, x => x.LoginRequestsPerMinute, GroupSecurity,
            "登入每分鐘請求上限", "每個 IP 每分鐘可以呼叫登入 API 幾次。調高會削弱對暴力猜密碼的防護。",
            "立即（計數重新開始）", unit: "次", min: 1, max: 100_000),
        Define<PasswordResetSettings, int>(PasswordResetSettings.SectionName, x => x.TokenLifetimeMinutes, GroupSecurity,
            "重設密碼連結有效時間", "「忘記密碼」寄出的連結多久後失效。",
            "之後寄出的連結（已寄出的維持原本的期限）", unit: "分鐘"),
        Define<PasswordResetSettings, int>(PasswordResetSettings.SectionName, x => x.RequestCooldownSeconds, GroupSecurity,
            "重設密碼申請冷卻時間", "同一個帳號兩次申請「忘記密碼」至少要隔多久。",
            "立即", unit: "秒", zeroMeaning: "不限制"),
    ];

    private static readonly Dictionary<string, SystemParameterDefinition> ByKey =
        All.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static SystemParameterDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IEnumerable<string> Sections => All.Select(x => x.SectionName).Distinct(StringComparer.OrdinalIgnoreCase);

    private static SystemParameterDefinition Define<TOptions, TValue>(
        string sectionName,
        Expression<Func<TOptions, TValue>> property,
        string group,
        string label,
        string description,
        string effectNote,
        string? unit = null,
        string? zeroMeaning = null,
        int? min = null,
        int? max = null,
        int? maxLength = null,
        bool required = false,
        bool isRetention = false)
        where TOptions : class
    {
        var members = new Stack<PropertyInfo>();
        for (var expression = property.Body; expression is MemberExpression member; expression = member.Expression!)
        {
            members.Push((PropertyInfo)member.Member);
        }

        var leaf = members.Last();
        var propertyPath = string.Join(':', members.Select(x => x.Name));
        var range = leaf.GetCustomAttribute<RangeAttribute>();
        var kind = typeof(TValue) == typeof(int) ? SystemParameterKind.Int
            : typeof(TValue) == typeof(bool) ? SystemParameterKind.Bool
            : typeof(TValue) == typeof(string) ? SystemParameterKind.String
            : throw new InvalidOperationException($"系統參數只支援 int、bool、string：{sectionName}:{propertyPath}");

        return new SystemParameterDefinition
        {
            Key = $"{sectionName}:{propertyPath}",
            SectionName = sectionName,
            PropertyPath = propertyPath,
            OptionsType = typeof(TOptions),
            Group = group,
            Label = label,
            Description = description,
            EffectNote = effectNote,
            Kind = kind,
            Unit = unit,
            ZeroMeaning = zeroMeaning,
            Min = min ?? (range is null ? null : Convert.ToInt32(range.Minimum, System.Globalization.CultureInfo.InvariantCulture)),
            Max = max ?? (range is null ? null : Convert.ToInt32(range.Maximum, System.Globalization.CultureInfo.InvariantCulture)),
            MaxLength = maxLength,
            Required = required,
            IsRetention = isRetention,
            ValidateSection = ValidateWithOptionsPipeline<TOptions>,
        };
    }

    /// <summary>
    /// 用真的 options 管線綁定並驗證：DI 裡這個類型的所有 <see cref="IValidateOptions{TOptions}"/>
    /// （DataAnnotations、<c>.Validate(...)</c> 規則、自訂驗證器）都會執行，與啟動時、執行期 reload 時的判斷一致。
    /// </summary>
    private static IReadOnlyList<string> ValidateWithOptionsPipeline<TOptions>(IServiceProvider services, IConfiguration section)
        where TOptions : class
    {
        var factory = new OptionsFactory<TOptions>(
            [new NamedConfigureFromConfigurationOptions<TOptions>(Options.DefaultName, section)],
            services.GetServices<IPostConfigureOptions<TOptions>>(),
            services.GetServices<IValidateOptions<TOptions>>());
        try
        {
            factory.Create(Options.DefaultName);
            return [];
        }
        catch (OptionsValidationException ex)
        {
            return ex.Failures.ToList();
        }
        catch (InvalidOperationException ex)
        {
            // 綁定時型別轉換失敗（例如數字欄位拿到文字）。
            return [ex.Message];
        }
    }
}
