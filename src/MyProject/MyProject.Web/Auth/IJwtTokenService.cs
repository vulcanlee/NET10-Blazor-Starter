using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;

namespace MyProject.Web.Auth;

public interface IJwtTokenService
{
    /// <summary>
    /// 簽發存取 Token，並與 <see cref="IRefreshTokenStore"/> 發出的刷新 Token 組成回應（0.10.0 起）。
    /// 刷新 Token 不再是 JWT，而是存在伺服器端、可輪替與撤銷的不透明字串。
    /// </summary>
    TokenResponseDto CreateTokenResponse(MyUser user, IssuedRefreshToken refreshToken, bool isPersistent);
}
