using MyProject.Business.Helpers;
using MyProject.Models.Others;

namespace MyProject.Web.Client.Auth;

/// <summary>
/// 畫面上的權限判斷（0.10.2 起，WASM 遷移 M3）：<c>RequirePermission</c>、<c>AuditHistoryButton</c> 與 WASM 頁面用它決定顯示什麼。
/// 規則一律在 <see cref="PermissionRules"/>；目前使用者是 <c>/me</c> 的回應（<see cref="ClientPagePermissions"/>）。
/// ⚠️ 這只是畫面上的顯示：伺服器端仍以 <c>[HasPermission]</c>／<c>IPermissionChecker</c> 為準。
/// </summary>
public abstract class PagePermissions
{
    protected abstract CurrentUser User { get; }

    public bool IsAdmin => User.IsAdmin;

    public bool CanAccessPage(string page) => PermissionRules.CanAccessPage(User, page);

    public bool CanAccessAction(string page, string action) => PermissionRules.CanAccessAction(User, page, action);

    /// <summary>
    /// 錯誤通知按「複製」時能否附上完整例外（0.9.130 起的規則）：管理員可以。
    /// </summary>
    public virtual bool MayCopyErrorDetail => User.IsAdmin;
}

/// <summary>WASM 頁面的目前使用者：<see cref="ClientSession.Info"/>（<c>/me</c>，權限鍵已含角色展開後的結果）。</summary>
public sealed class ClientPagePermissions : PagePermissions
{
    private readonly ClientSession session;

    public ClientPagePermissions(ClientSession session) => this.session = session;

    protected override CurrentUser User => From(session.Info);

    internal static CurrentUser From(MyProject.Dtos.Auths.SessionInfoDto? info) => info is null
        ? new CurrentUser()
        : new CurrentUser
        {
            Id = info.Id,
            Account = info.Account,
            Name = info.Name,
            Email = info.Email,
            IsAdmin = info.IsAdmin,
            IsAuthenticated = true,
            RoleList = info.Permissions,
            TeamList = info.Teams,
        };
}
