using System.Security.Claims;

namespace MyProject.Web.Auth;

/// <summary>
/// 從已驗證的身分取出可以寫進紀錄的 Account 與 UserId。
///
/// 0.10.9 起（WASM 遷移 M10）只有 JWT 身分：NameIdentifier=UserId、Name=帳號。
/// 以前的登入 Cookie（NameIdentifier=帳號、Sid=UserId、Name=<b>姓名（個資）</b>）0.10.8 起已不發，它的分支一併移除；
/// NameIdentifier 不是正整數的身分（例如 Cookie 形狀的 claims）一律當成解析不到，絕不會把 <see cref="ClaimTypes.Name"/> 當帳號記下。
/// </summary>
public static class RequestActorResolver
{
    public static (string? Account, int? UserId) Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return (null, null);
        }

        if (int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var jwtUserId) && jwtUserId > 0)
        {
            return (principal.FindFirst(ClaimTypes.Name)?.Value, jwtUserId);
        }

        return (null, null);
    }
}
