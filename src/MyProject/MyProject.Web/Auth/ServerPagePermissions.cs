using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Web.Client.Auth;
using MyProject.Web.Configuration;

namespace MyProject.Web.Auth;

/// <summary>
/// Server 頁面的 <see cref="PagePermissions"/>（0.10.2 起，WASM 遷移 M3）：目前使用者是電路的 <see cref="CurrentUserService"/>。
/// 錯誤通知的完整例外另外依 <see cref="ExceptionDetailPolicy"/>（開發環境或 <c>Security:ReturnExceptionDetails=true</c>）放行。
/// </summary>
public sealed class ServerPagePermissions : PagePermissions
{
    private readonly CurrentUserService currentUserService;
    private readonly IOptions<SecuritySettings> securitySettings;
    private readonly IWebHostEnvironment environment;

    public ServerPagePermissions(CurrentUserService currentUserService, IOptions<SecuritySettings> securitySettings, IWebHostEnvironment environment)
    {
        this.currentUserService = currentUserService;
        this.securitySettings = securitySettings;
        this.environment = environment;
    }

    protected override CurrentUser User => currentUserService.CurrentUser;

    public override bool MayCopyErrorDetail
        => base.MayCopyErrorDetail || ExceptionDetailPolicy.ShouldReturnDetails(securitySettings.Value, environment);
}
