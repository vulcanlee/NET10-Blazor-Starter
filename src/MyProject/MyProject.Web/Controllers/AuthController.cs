using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Web.Auth;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
public class AuthController : ControllerBase
{
    private readonly MyUserServiceLogin userServiceLogin;
    private readonly IJwtTokenService jwtTokenService;
    private readonly ILogger<AuthController> logger;

    public AuthController(
        MyUserServiceLogin userServiceLogin,
        IJwtTokenService jwtTokenService,
        ILogger<AuthController> logger)
    {
        this.userServiceLogin = userServiceLogin;
        this.jwtTokenService = jwtTokenService;
        this.logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    // 註：登入的較嚴格配額由 "api" policy 依路徑判斷（見 AddConfiguredRateLimiting）。
    // 這裡刻意**不用** [EnableRateLimiting("login")]：端點慣例
    // MapControllers().RequireRateLimiting("api") 套用時機晚於屬性，會把它蓋掉而靜默失效。
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Login([FromBody] LoginRequestDto request)
    {
        var (message, user) = await userServiceLogin.LoginAsync(request.Account, request.Password);
        if (user is null)
        {
            logger.LogWarning("API login failed. Account={Account}", request.Account);
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult(message));
        }

        var tokenResponse = jwtTokenService.CreateTokenResponse(user);
        logger.LogInformation("API login succeeded. Account={Account}, UserId={UserId}", user.Account, user.Id);
        return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "登入成功"));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<ApiResult<TokenResponseDto>>> Refresh([FromBody] RefreshTokenRequestDto request)
    {
        try
        {
            var currentUser = jwtTokenService.ValidateRefreshToken(request.RefreshToken);

            // ⚠️ 不可直接拿 token claim 裡的資料重簽。
            // Refresh token 是 stateless、不落庫、無法撤銷（見「認證授權與權限機制」的既有限制），
            // 若不回查資料庫，帳號被停用或降權之後，舊 refresh token 在有效期內
            // 仍可持續換發帶著舊 IsAdmin 的 access token。
            var user = await userServiceLogin.GetActiveUserAsync(currentUser.Id);
            if (user is null)
            {
                logger.LogWarning(
                    "Refresh rejected because the user no longer exists or is disabled. UserId={UserId}",
                    currentUser.Id);
                return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("Refresh Token 無效或已過期。"));
            }

            var tokenResponse = jwtTokenService.CreateTokenResponse(user);
            return Ok(ApiResult<TokenResponseDto>.SuccessResult(tokenResponse, "Token 更新成功"));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refresh token validation failed.");
            return Unauthorized(ApiResult<TokenResponseDto>.UnauthorizedResult("Refresh Token 無效或已過期。"));
        }
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public ActionResult<ApiResult<CurrentUserDto>> Me()
    {
        var user = new CurrentUserDto
        {
            Id = int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0,
            Account = User.Identity?.Name ?? string.Empty,
            Name = User.FindFirst("display_name")?.Value ?? string.Empty,
            Email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
            IsAdmin = bool.TryParse(User.FindFirst("is_admin")?.Value, out var isAdmin) && isAdmin
        };

        return Ok(ApiResult<CurrentUserDto>.SuccessResult(user, "取得目前使用者成功"));
    }
}
