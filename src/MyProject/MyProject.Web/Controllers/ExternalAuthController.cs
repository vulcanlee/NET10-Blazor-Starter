using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Configuration;
using System.Security.Claims;

namespace MyProject.Web.Controllers;

/// <summary>
/// Google OAuth2 第三方登入的瀏覽器導向端點（Challenge / Callback）。
///
/// 0.10.8 起（WASM 遷移 M9）不發登入 Cookie：發起時必須帶瀏覽器綁定值（<c>binding</c>，登入頁產生的驗證值的 SHA-256），
/// 回呼產生一次性代碼導回前端 <c>/Auths/Login#code=…</c>，由 <c>POST api/v1/Auth/google/exchange</c> 換 Token（<see cref="GoogleLoginCodes"/>）。
/// 沒帶綁定值（舊書籤、直接打網址）一律回登入頁重新開始。
/// 被拒絕的結果（帳號名稱已被使用、已刪除、待開通、鎖定）導回登入頁帶固定代碼或待開通頁。
/// 0.10.10 起導回的前端頁一律經 <see cref="ClientSettings.FrontendUrl"/>：雙站台時是 <c>ClientSettings:BaseUrl</c>，
/// 只用設定值，不採用請求的 Host 或任何參數（不可成為開放式重新導向）；回呼本身仍在這個 API 主機。
/// </summary>
[Route("Auths/Google")]
// Google OAuth 的導向入口，本來就必須匿名可達；明示標註以搭配
// Program.cs 的 MapControllers().RequireAuthorization()（未標註即拒絕）。
[AllowAnonymous]
public class ExternalAuthController : Controller
{
    private readonly ExternalLoginService externalLoginService;
    private readonly GoogleOAuthSettings googleOAuthSettings;
    private readonly ILogger<ExternalAuthController> logger;
    private readonly IAuditLogService auditLogService;
    private readonly ISecurityStampService securityStampService;
    private readonly GoogleLoginCodes googleLoginCodes;
    private readonly ClientSettings clientSettings;

    public ExternalAuthController(
        ExternalLoginService externalLoginService,
        IOptions<GoogleOAuthSettings> googleOAuthSettings,
        ILogger<ExternalAuthController> logger,
        IAuditLogService auditLogService,
        ISecurityStampService securityStampService,
        GoogleLoginCodes googleLoginCodes,
        IOptions<ClientSettings> clientSettings)
    {
        this.externalLoginService = externalLoginService;
        this.googleOAuthSettings = googleOAuthSettings.Value;
        this.logger = logger;
        this.auditLogService = auditLogService;
        this.securityStampService = securityStampService;
        this.googleLoginCodes = googleLoginCodes;
        this.clientSettings = clientSettings.Value;
    }

    /// <summary>
    /// 觸發 Google OAuth2 驗證。
    /// </summary>
    /// <param name="binding">
    /// 前端產生的隨機驗證值的 SHA-256（Base64Url，0.10.8 起必填）：兌換一次性代碼時要出示原值（綁定發起的瀏覽器）。
    /// 放進 OAuth 的 state（加密、防竄改），不寫進日誌。
    /// </param>
    [HttpGet("Login")]
    public IActionResult Login(string? returnUrl = null, string? binding = null)
    {
        if (!googleOAuthSettings.IsConfigured)
        {
            logger.LogWarning("Google login requested but Google OAuth is not configured.");
            return ToFrontend("/Auths/Login");
        }

        if (!GoogleLoginCodes.IsValidBindingHash(binding))
        {
            logger.LogInformation("Google login requested without a browser binding; returning to the login page.");
            return ToFrontend("/Auths/Login");
        }

        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        var callbackUrl = Url.Action(nameof(Callback), "ExternalAuth", new { returnUrl = safeReturnUrl })
            ?? "/Auths/Google/Callback";

        var properties = new AuthenticationProperties
        {
            RedirectUri = callbackUrl,
        };
        properties.Items[GoogleLoginCodes.BindingItemKey] = binding;

        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Google 驗證完成後的回呼：查找/建立帳號，依狀態導向待審核頁或以一次性代碼回前端。
    /// </summary>
    [HttpGet("Callback")]
    public async Task<IActionResult> Callback(string? returnUrl = null)
    {
        var result = await HttpContext.AuthenticateAsync(MagicObjectHelper.ExternalCookieScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            logger.LogWarning("Google callback failed because external authentication did not succeed.");
            await auditLogService.WriteAsync(AuditActions.Login.SsoFailed, success: false, detail: "provider=Google; reason=AuthFailed");
            return ToFrontend("/Auths/Login");
        }

        var subject = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = result.Principal.FindFirstValue(ClaimTypes.Email);
        var name = result.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
        {
            logger.LogWarning("Google callback rejected because subject or email claim is missing.");
            await auditLogService.WriteAsync(AuditActions.Login.SsoFailed, success: false, detail: "provider=Google; reason=MissingClaims");
            await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);
            return ToFrontend("/Auths/Login");
        }

        // 發起時帶的瀏覽器綁定值（在 OAuth 的 state 裡）；沒有就無法安全地交回前端，重新開始。
        var binding = result.Properties?.Items.TryGetValue(GoogleLoginCodes.BindingItemKey, out var value) == true && GoogleLoginCodes.IsValidBindingHash(value)
            ? value
            : null;
        if (binding is null)
        {
            logger.LogWarning("Google callback rejected because the browser binding is missing.");
            await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);
            return ToFrontend("/Auths/Login");
        }

        var lookup = await externalLoginService.FindOrCreateAsync(
            GoogleDefaults.AuthenticationScheme,
            subject,
            email,
            name,
            googleOAuthSettings.DefaultRoleName);
        var user = lookup.User;

        // 清除暫存的外部登入身分
        await HttpContext.SignOutAsync(MagicObjectHelper.ExternalCookieScheme);

        var outcome = lookup.Evaluate(DateTime.UtcNow);

        // 帳號名稱已被別人使用（0.9.171 起帳號不分大小寫唯一）：不建立、不合併，回登入頁請使用者洽管理員。
        if (outcome == ExternalLoginOutcome.AccountTaken)
        {
            logger.LogWarning("Google login refused because the account name is already used by another user.");
            await auditLogService.WriteAsync(
                AuditActions.Login.SsoFailed, success: false, actorAccount: user.Account, detail: "provider=Google; reason=AccountTaken");
            return ToFrontend("/Auths/Login?sso=account-taken");
        }

        // 已刪除的使用者（0.9.95 起）：不登入、不建新帳號，回登入頁顯示固定訊息；管理員還原後即可再登入。
        if (outcome == ExternalLoginOutcome.Deleted)
        {
            logger.LogInformation("Google login refused because the user is deleted. UserId={UserId}.", user.Id);
            await auditLogService.WriteAsync(
                AuditActions.Login.SsoFailed, success: false, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google; reason=Deleted");
            return ToFrontend("/Auths/Login?sso=deleted");
        }

        if (outcome == ExternalLoginOutcome.Pending)
        {
            logger.LogInformation(
                "Google login user is disabled and awaiting approval. UserId={UserId}.",
                user.Id);
            await auditLogService.WriteAsync(
                AuditActions.Login.Disabled, success: false, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google");
            return ToFrontend("/Auths/Pending");
        }

        // 帳號因密碼輸錯被鎖定時，Google 登入也不放行（0.9.101 起；之前可以用 Google 繞過鎖定）。
        // 對方已向 Google 證明身分，所以明確告知是鎖定，不必用模糊訊息。
        if (outcome == ExternalLoginOutcome.Locked)
        {
            logger.LogInformation("Google login refused because the account is locked. UserId={UserId}, LockoutEndUtc={LockoutEndUtc}.", user.Id, user.LockoutEndUtc);
            await auditLogService.WriteAsync(
                AuditActions.Login.SsoFailed, success: false, actorUserId: user.Id, actorAccount: user.Account, detail: "provider=Google; reason=Locked");
            return ToFrontend("/Auths/Login?sso=locked");
        }

        // 重疊回收時舊版程式建立的帳號可能還沒有工作階段版本（0.9.103 起）。
        user.SecurityStamp = await securityStampService.EnsureAsync(user.Id);

        // ⚠️ 已啟用兩步驟驗證的帳號由兌換端點回待驗票證（記住過的裝置除外）；成功稽核也在兌換、真的發出 Token 時才寫。
        var requiresSecondFactor = user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret);
        var code = googleLoginCodes.Issue(new GoogleLoginGrant(user.Id, user.SecurityStamp, GetSafeReturnUrl(returnUrl), requiresSecondFactor, binding));
        logger.LogInformation("Google login verified; returning a one-time code to the browser. UserId={UserId}, SecondFactor={SecondFactor}", user.Id, requiresSecondFactor);
        return ToFrontend($"{GoogleLoginCodes.FrontendPath}#code={Uri.EscapeDataString(code)}");
    }

    /// <summary>導回前端的頁面（同站台是本站路徑，雙站台接在 ClientSettings:BaseUrl 後面）。</summary>
    private RedirectResult ToFrontend(string path) => Redirect(clientSettings.FrontendUrl(path));

    private string GetSafeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return "/App";
    }
}
