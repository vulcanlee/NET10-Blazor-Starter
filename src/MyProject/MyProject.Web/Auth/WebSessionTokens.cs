using System.Security.Claims;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Share.Helpers;

namespace MyProject.Web.Auth;

/// <summary>
/// 網頁與 API 的 Token 發出規則（0.10.8 起，WASM 遷移 M9）：效期依發出管道與「記住我」決定，換發與新分頁沿用同一套規則。
/// <list type="bullet">
/// <item>網頁（<see cref="RefreshClientKinds.Web"/>）勾「記住我」：刷新 Token 與整串上限都是 <see cref="SessionSettings.RememberMeDays"/> 天（從登入算，不滑動）。</item>
/// <item>網頁沒勾：刷新 Token <see cref="SessionSettings.IdleTimeoutMinutes"/> 分鐘，每次刷新重新起算（閒置逾時），沒有整串上限。</item>
/// <item>網頁的存取 Token <see cref="SessionSettings.AccessTokenMinutes"/> 分鐘（<see cref="JwtTokenService"/>）。</item>
/// <item>API 登入（App）：<see cref="JwtSettings.RefreshTokenDays"/>（0.10.7 以前 Cookie 換出的舊紀錄同樣，另以 Cookie 的到期時間為上限）。</item>
/// </list>
/// </summary>
public sealed class WebSessionTokens
{
    private const int DeviceLabelMaxLength = 200;

    private readonly IRefreshTokenStore store;
    private readonly IJwtTokenService jwtTokenService;
    private readonly IOptionsMonitor<SessionSettings> sessionOptions;
    private readonly JwtSettings jwtSettings;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<WebSessionTokens> logger;

    public WebSessionTokens(
        IRefreshTokenStore store,
        IJwtTokenService jwtTokenService,
        IOptionsMonitor<SessionSettings> sessionOptions,
        IOptions<JwtSettings> jwtOptions,
        TimeProvider timeProvider,
        ILogger<WebSessionTokens> logger)
    {
        this.store = store;
        this.jwtTokenService = jwtTokenService;
        this.sessionOptions = sessionOptions;
        jwtSettings = jwtOptions.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>刷新 Token 的效期規則（交給 <see cref="IRefreshTokenStore"/> 在輪替、新分頁、換一串時使用）。</summary>
    public RefreshLifetimePolicy Lifetime => LifetimeFor;

    /// <summary>網頁登入成功：開一串新的網頁 Token（新的登入識別）。</summary>
    public async Task<TokenResponseDto> IssueWebAsync(MyUser user, bool rememberMe, HttpRequest request)
    {
        var settings = sessionOptions.CurrentValue;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        // 記住我：從登入起 RememberMeDays 天的整串上限；沒勾：預設滑動（沒有整串上限），IdleTimeoutSliding=false 時從登入起 IdleTimeoutMinutes 分鐘。
        DateTime? cap = rememberMe
            ? now.AddDays(settings.RememberMeDays)
            : settings.IdleTimeoutSliding ? null : now.AddMinutes(settings.IdleTimeoutMinutes);
        var refresh = await store.IssueAsync(
            user, rememberMe, RefreshClientKinds.Web, Guid.NewGuid().ToString("N"), DeviceLabel(request), LifetimeFor(RefreshClientKinds.Web, rememberMe), cap);
        logger.LogInformation("Web tokens issued. UserId={UserId}, Persistent={Persistent}", user.Id, rememberMe);
        return jwtTokenService.CreateTokenResponse(user, refresh, rememberMe);
    }

    /// <summary>刷新、新分頁、換一串的結果轉成回應（只在 <see cref="RefreshRotationStatus.Rotated"/> 時呼叫）。</summary>
    public TokenResponseDto Respond(RefreshRotationResult result) => jwtTokenService.CreateTokenResponse(result.User!, result.Token!, result.IsPersistent);

    /// <summary>
    /// 自己換了工作階段版本之後，把呼叫這次請求的那一串換成新的（存取 Token 帶的 <see cref="JwtTokenService.FamilyClaimType"/>）。
    /// 存取 Token 沒有串的識別（0.10.8 以前發的）或那一串已不存在時回 null —— 呼叫端照舊回應，前端下一個請求就會被要求重新登入。
    /// </summary>
    public async Task<TokenResponseDto?> ReissueAsync(ClaimsPrincipal principal, MyUser user, string reason)
    {
        var familyId = principal.FindFirstValue(JwtTokenService.FamilyClaimType);
        if (string.IsNullOrEmpty(familyId))
        {
            return null;
        }

        var result = await store.ReissueFamilyAsync(familyId, user, Lifetime, reason);
        return result.Status == RefreshRotationStatus.Rotated ? Respond(result) : null;
    }

    /// <summary>刷新紀錄上的裝置說明（User-Agent，截斷到 200 字）。</summary>
    public static string? DeviceLabel(HttpRequest request)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        return userAgent.Length > DeviceLabelMaxLength ? userAgent[..DeviceLabelMaxLength] : userAgent;
    }

    private TimeSpan LifetimeFor(string clientKind, bool isPersistent)
    {
        if (clientKind != RefreshClientKinds.Web)
        {
            return TimeSpan.FromDays(jwtSettings.RefreshTokenDays);
        }

        var settings = sessionOptions.CurrentValue;
        return isPersistent ? TimeSpan.FromDays(settings.RememberMeDays) : TimeSpan.FromMinutes(settings.IdleTimeoutMinutes);
    }
}

/// <summary>
/// 自己的工作階段版本換掉之後讓這台裝置保持登入（0.10.8 起；取代 Server 版的 <c>SessionRefreshNavigator</c>）：
/// 把這次請求所屬的那一串換成新的一串，回這台的新 Token（其他裝置與這台的舊 Token 都失效）。
/// </summary>
public sealed class SelfSessionRenewal
{
    private readonly ISecurityStampService securityStampService;
    private readonly MyUserServiceLogin userServiceLogin;
    private readonly WebSessionTokens webSessionTokens;
    private readonly ILogger<SelfSessionRenewal> logger;

    public SelfSessionRenewal(
        ISecurityStampService securityStampService,
        MyUserServiceLogin userServiceLogin,
        WebSessionTokens webSessionTokens,
        ILogger<SelfSessionRenewal> logger)
    {
        this.securityStampService = securityStampService;
        this.userServiceLogin = userServiceLogin;
        this.webSessionTokens = webSessionTokens;
        this.logger = logger;
    }

    /// <summary>版本確實換了而且帳號仍可登入時回新的 Token；否則 <see cref="SessionRenewalDto.Tokens"/> 為 null。</summary>
    public async Task<T> RenewAsync<T>(ClaimsPrincipal principal, int userId, string reason)
        where T : SessionRenewalDto, new()
    {
        var oldStamp = principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType) ?? string.Empty;
        var state = await securityStampService.GetStateAsync(userId, TimeSpan.Zero);
        if (!state.IsActive || string.IsNullOrEmpty(oldStamp) || SecurityStamps.Matches(oldStamp, state.SecurityStamp)
            || await userServiceLogin.GetActiveUserAsync(userId) is not { } user)
        {
            return new T();
        }

        var tokens = await webSessionTokens.ReissueAsync(principal, user, reason);
        logger.LogInformation("Kept the current device signed in after the user changed their own session. UserId={UserId}, Reissued={Reissued}", userId, tokens is not null);
        return new T { Tokens = tokens };
    }
}
