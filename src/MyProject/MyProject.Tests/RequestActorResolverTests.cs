using System.Security.Claims;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// 例外情境的帳號解析（LOG-01）。0.10.9 起（WASM 遷移 M10）只認 JWT（NameIdentifier=UserId、Name=帳號）；
/// 以前登入 Cookie 的 claim 對應相反，它的 <see cref="ClaimTypes.Name"/> 是<b>使用者姓名（個資）</b>，絕不可被當成帳號記下。
/// </summary>
public sealed class RequestActorResolverTests
{
    /// <summary>登入 Cookie 0.10.8 起不再發出，它的分支 0.10.9 起移除：Cookie 形狀的身分（Sid=UserId）不再被認得。</summary>
    [Fact]
    public void Resolve_CookieShapedPrincipal_ShouldNoLongerBeRecognized()
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.Name, "王小明"),
            new Claim(ClaimTypes.NameIdentifier, "support"),
            new Claim(ClaimTypes.Sid, "7"));

        var (account, userId) = RequestActorResolver.Resolve(principal);

        Assert.Null(account);
        Assert.Null(userId);
    }

    [Fact]
    public void Resolve_CookieShapedPrincipal_ShouldNeverReturnPersonName()
    {
        // 0.9.77 之前依路徑判斷成 JWT，會把 Cookie 身分的姓名當帳號記下。
        var principal = Authenticated(
            new Claim(ClaimTypes.Name, "王小明"),
            new Claim(ClaimTypes.NameIdentifier, "support"),
            new Claim(ClaimTypes.Sid, "7"));

        var (account, _) = RequestActorResolver.Resolve(principal);

        Assert.NotEqual("王小明", account);
    }

    [Fact]
    public void Resolve_JwtPrincipal_ShouldUseNameAndNameIdentifier()
    {
        var principal = Authenticated(
            new Claim(ClaimTypes.Name, "support"),
            new Claim(ClaimTypes.NameIdentifier, "7"));

        var (account, userId) = RequestActorResolver.Resolve(principal);

        Assert.Equal("support", account);
        Assert.Equal(7, userId);
    }

    [Fact]
    public void Resolve_Anonymous_ShouldReturnNulls()
    {
        var (account, userId) = RequestActorResolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Null(account);
        Assert.Null(userId);
    }

    [Fact]
    public void Resolve_AuthenticatedWithoutUsableIds_ShouldReturnNulls()
    {
        var principal = Authenticated(new Claim(ClaimTypes.Name, "王小明"));

        var (account, userId) = RequestActorResolver.Resolve(principal);

        Assert.Null(account);
        Assert.Null(userId);
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType: "Test"));
}
