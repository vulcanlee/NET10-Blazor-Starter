using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Share.Helpers;

namespace MyProject.Web.Auth;

public class JwtTokenService : IJwtTokenService
{
    internal const string TokenTypeClaimType = "token_type";
    internal const string AccessTokenType = "access";
    private readonly JwtSettings settings;
    private readonly ILogger<JwtTokenService> logger;

    public JwtTokenService(IOptions<JwtSettings> options, ILogger<JwtTokenService> logger)
    {
        settings = options.Value;
        this.logger = logger;
    }

    public TokenResponseDto CreateTokenResponse(MyUser user, IssuedRefreshToken refreshToken, bool isPersistent)
    {
        var currentUser = ToCurrentUserDto(user);
        var accessExpiresAt = DateTime.UtcNow.AddMinutes(settings.AccessTokenMinutes);

        // ⚠️ 只記 UserId 與到期時間，token 本身絕不進日誌。
        logger.LogDebug(
            "Issued an access token. UserId={UserId}, AccessExpiresAt={AccessExpiresAt}, RefreshExpiresAt={RefreshExpiresAt}",
            currentUser.Id, accessExpiresAt, refreshToken.ExpiresAtUtc);

        return new TokenResponseDto
        {
            AccessToken = CreateToken(currentUser, user.SecurityStamp, AccessTokenType, accessExpiresAt),
            AccessTokenExpiresAt = accessExpiresAt,
            RefreshToken = refreshToken.Token,
            RefreshTokenExpiresAt = refreshToken.ExpiresAtUtc,
            User = currentUser,
            IsPersistent = isPersistent,
        };
    }

    public TokenValidationParameters CreateValidationParameters(bool validateLifetime)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = CreateSecurityKey(),
            ValidateLifetime = validateLifetime,
            ClockSkew = TimeSpan.FromMinutes(settings.ClockSkewMinutes)
        };
    }

    private string CreateToken(CurrentUserDto user, string securityStamp, string tokenType, DateTime expiresAt)
    {
        var credentials = new SigningCredentials(CreateSecurityKey(), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Account),
            new("display_name", user.Name),
            new("is_admin", user.IsAdmin.ToString()),
            new(TokenTypeClaimType, tokenType),
            new(MagicObjectHelper.SecurityStampClaimType, securityStamp)
        };

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private SymmetricSecurityKey CreateSecurityKey()
    {
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
    }

    private static CurrentUserDto ToCurrentUserDto(MyUser user)
    {
        return new CurrentUserDto
        {
            Id = user.Id,
            Account = user.Account,
            Name = user.Name,
            Email = user.Email,
            IsAdmin = user.IsAdmin
        };
    }
}
