namespace MyProject.Dtos.Auths;

public class TokenResponseDto
{
    public string TokenType { get; set; } = "Bearer";

    public string AccessToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpiresAt { get; set; }

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime RefreshTokenExpiresAt { get; set; }

    public CurrentUserDto User { get; set; } = new();

    /// <summary>
    /// 這組 Token 是否屬於「記住我」的登入（0.10.0 起）。網頁前端據此決定存放位置：
    /// true 存 localStorage（關掉瀏覽器仍維持登入），false 存 sessionStorage（關掉分頁即登出）。
    /// </summary>
    public bool IsPersistent { get; set; }
}
