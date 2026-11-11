using System.ComponentModel.DataAnnotations;

namespace MyProject.Web.Auth;

/// <summary>
/// 網頁登入能維持多久（0.10.8 起，WASM 遷移 M9；取代 <c>CookieSettings</c>）。網頁改成以 Token 登入後，這些值管的是網頁的發出管道
/// （<c>RefreshClientKinds.Web</c>；0.10.8 起不發登入 Cookie）。API 登入（App、整合程式）仍看 <see cref="JwtSettings"/>。
///
/// <para>⚠️ 舊鍵相容：設定檔或環境變數裡若還有 <c>CookieSettings:ExpireMinutes</c>／<c>RememberMeDays</c>／<c>ValidationIntervalMinutes</c>／<c>SlidingExpiration</c>，
/// 會蓋過這裡的值並在啟動時記一筆警告（見 <see cref="ApplyLegacyCookieSettings"/>），升級不會悄悄改變登入效期。</para>
/// </summary>
public class SessionSettings
{
    public const string SectionName = "SessionSettings";

    /// <summary>舊的設定區段名稱（0.10.7 以前）。</summary>
    public const string LegacySectionName = "CookieSettings";

    /// <summary>
    /// 沒有勾「記住我」時，多久沒有使用就要重新登入（分鐘，滑動）。預設 480（8 小時）。
    /// 網頁的刷新 Token 存在 sessionStorage，關掉分頁也就登出了 —— 兩個條件取先到者。
    /// </summary>
    [Range(1, 43200)]
    public int IdleTimeoutMinutes { get; set; } = 480;

    /// <summary>
    /// 沒勾「記住我」時是否滑動（0.10.8 獨立審查：對應舊的 <c>CookieSettings:SlidingExpiration</c>）。預設 true：每次使用重新起算。
    /// false：從登入那一刻起最多 <see cref="IdleTimeoutMinutes"/> 分鐘（絕對上限），一直使用也會到期。
    /// </summary>
    public bool IdleTimeoutSliding { get; set; } = true;

    /// <summary>勾了「記住我」時整個登入的上限（天，從登入那一刻算，不滑動）。預設 30。</summary>
    [Range(1, 365)]
    public int RememberMeDays { get; set; } = 30;

    /// <summary>網頁的存取 Token 效期（分鐘）。預設 15；到期前由前端以刷新 Token 換新。</summary>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// 工作階段版本的檢查間隔（分鐘）。預設 5。改密碼、停用、強制登出之後，別的行程（多台主機）最晚在這段時間內看到；
    /// 同一個行程內是立即的。存取 Token 驗證時用這個值。
    /// </summary>
    [Range(1, 60)]
    public int ValidationIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// 舊鍵相容：<c>CookieSettings</c> 區段裡有設定的值蓋過新區段，回傳套用了哪些舊鍵（啟動時記警告）。
    /// 出貨的 appsettings.json 已不含舊區段，所以出現舊鍵一定是部署者自己設的（環境變數、appsettings.Production.json 等）。
    /// </summary>
    public static IReadOnlyList<string> ApplyLegacyCookieSettings(SessionSettings target, IConfiguration configuration)
    {
        var applied = new List<string>();
        var legacy = configuration.GetSection(LegacySectionName);
        Apply("ExpireMinutes", value => target.IdleTimeoutMinutes = value);
        Apply("RememberMeDays", value => target.RememberMeDays = value);
        Apply("ValidationIntervalMinutes", value => target.ValidationIntervalMinutes = value);
        if (legacy["SlidingExpiration"] is { } sliding && bool.TryParse(sliding, out var isSliding))
        {
            target.IdleTimeoutSliding = isSliding;
            applied.Add($"{LegacySectionName}:SlidingExpiration");
        }

        return applied;

        void Apply(string key, Action<int> set)
        {
            if (legacy[key] is { } raw && int.TryParse(raw, out var value))
            {
                set(value);
                applied.Add($"{LegacySectionName}:{key}");
            }
        }
    }

    /// <summary>以設定檔讀出（含舊鍵相容）；給註冊時就要用到值的地方。</summary>
    public static SessionSettings Read(IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName).Get<SessionSettings>() ?? new SessionSettings();
        ApplyLegacyCookieSettings(settings, configuration);
        return settings;
    }
}
