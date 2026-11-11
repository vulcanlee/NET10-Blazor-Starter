using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// 0.10.0 起 <see cref="JwtTokenService"/> 只簽發存取 Token；刷新 Token 由 <c>RefreshTokenStore</c> 發出、存在伺服器端，
/// 這裡只負責原樣帶進回應（見 <c>RefreshTokenStoreTests</c>、<c>TokenApiIntegrationTests</c>）。
/// </summary>
public class JwtTokenServiceTests
{
    [Fact]
    public void CreateTokenResponse_ShouldSignAnAccessToken_AndPassTheStoredRefreshTokenThrough()
    {
        var service = CreateService();
        var user = CreateUser();
        var refresh = new IssuedRefreshToken("opaque-refresh", DateTime.UtcNow.AddDays(7));

        var result = service.CreateTokenResponse(user, refresh, isPersistent: true);

        Assert.Equal("Bearer", result.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.Equal("opaque-refresh", result.RefreshToken);
        Assert.Equal(refresh.ExpiresAtUtc, result.RefreshTokenExpiresAt);
        Assert.True(result.IsPersistent);
        Assert.Equal(user.Account, result.User.Account);
        Assert.True(result.AccessTokenExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public void AccessToken_ShouldCarryTheUserTheStampAndTheAccessType()
    {
        var service = CreateService();
        var result = service.CreateTokenResponse(CreateUser(), new IssuedRefreshToken("r", DateTime.UtcNow.AddDays(7)), isPersistent: false);

        var principal = new JwtSecurityTokenHandler().ValidateToken(result.AccessToken, service.CreateValidationParameters(validateLifetime: true), out _);

        Assert.Equal("7", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("api-user", principal.FindFirstValue(ClaimTypes.Name));
        Assert.Equal("stamp-1", principal.FindFirstValue(MagicObjectHelper.SecurityStampClaimType));
        Assert.Equal(JwtTokenService.AccessTokenType, principal.FindFirstValue(JwtTokenService.TokenTypeClaimType));
        Assert.False(result.IsPersistent);
    }

    private static JwtTokenService CreateService()
    {
        var settings = new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "TestSigningKey-AtLeast32Characters-12345",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
            ClockSkewMinutes = 0
        };

        return new JwtTokenService(Options.Create(settings), new StaticOptionsMonitor<SessionSettings>(new SessionSettings()), NullLogger<JwtTokenService>.Instance);
    }

    private static MyUser CreateUser()
    {
        return new MyUser
        {
            Id = 7,
            Account = "api-user",
            Name = "API User",
            Email = "api-user@example.com",
            IsAdmin = true,
            SecurityStamp = "stamp-1",
        };
    }
}
