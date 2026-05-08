using MyProject.AccessDatas.Models;
using MyProject.Dtos.Auths;

namespace MyProject.Web.Auth;

public interface IJwtTokenService
{
    TokenResponseDto CreateTokenResponse(MyUser user);

    CurrentUserDto ValidateRefreshToken(string refreshToken);
}
