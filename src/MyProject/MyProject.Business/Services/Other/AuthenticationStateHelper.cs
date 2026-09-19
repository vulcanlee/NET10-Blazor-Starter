using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Models.Others;
using MyProject.Share.Helpers;
using System.Security.Claims;
using System.Text.Json;

namespace MyProject.Business.Services.Other;

public class AuthenticationStateHelper
{
    private readonly ILogger<AuthenticationStateHelper> logger;
    private readonly IMapper mapper;
    private readonly MyUserService myUserService;
    private readonly CurrentUserService currentUserService;
    private readonly RolePermissionService rolePermissionService;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly IPermissionChecker permissionChecker;

    public AuthenticationStateHelper(
        ILogger<AuthenticationStateHelper> logger,
        IMapper mapper,
        MyUserService myUserService,
        CurrentUserService currentUserService,
        RolePermissionService rolePermissionService,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPermissionChecker permissionChecker)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.currentUserService = currentUserService;
        this.rolePermissionService = rolePermissionService;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.permissionChecker = permissionChecker;
    }

    public async Task<AuthenticationCheckResult> Check(AuthenticationStateProvider authStateProvider, NavigationManager navigationManager)
    {
        logger.LogDebug("Checking authentication state for current request.");

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Authentication check failed because the current principal is not authenticated.");
            await Task.Delay(200);

            // ⚠️ 這裡導向登入頁而**不是**登出頁（0.9.39 起），其餘分支則維持導向登出。
            //
            // 原因：/Auths/Logout 會無條件呼叫 SignOutAsync 刪掉 Cookie。而「未認證」這個
            // 分支同時涵蓋「Cookie 一時讀不出來」（伺服器重啟、Data Protection 金鑰換掉、
            // 換連接埠或網域）—— 那種情況下把 Cookie 毀掉，會讓使用者原本還有效的
            // 「記住我」永久消失，症狀還會自我延續（每次回來都重演一次）。
            // 真正失效的 Cookie 由 Cookie handler 自己清理，不需要我們動手。
            //
            // ⚠️ 不會產生重導迴圈：登入頁用的是 NoFooterLayout，而那個版面不呼叫本方法
            //（只有 MainLayout 與 NavMenu 會）。**若日後改動登入頁的版面，這個保證就沒了。**
            navigationManager.NavigateTo("/Auths/Login", true, true);
            return AuthenticationCheckResult.Unauthenticated;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Authentication check failed because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        var myUser = await myUserService.GetAsync(id);
        if (myUser.Id == 0)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} was not found.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        if (!myUser.Status)
        {
            logger.LogWarning("Authentication check failed because UserId={UserId} is disabled.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        logger.LogDebug("Resolved authenticated user information for UserId={UserId}.", id);

        bool needChangePassword = await myUserService.NeedChangePasswordAsync(myUser);
        if (needChangePassword && !IsChangePasswordPage(navigationManager))
        {
            logger.LogWarning("User {UserId} is required to change password before continuing.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/ChangePassword", true);
            return AuthenticationCheckResult.RequiresPasswordChange;
        }

        CurrentUser currentUser = mapper.Map<CurrentUser>(myUser);
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();

        if (myUser.RoleView == null)
        {
            logger.LogWarning("User {UserId} does not have a role view assigned. Redirecting to logout.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }

        try
        {
            List<string> permissions = JsonSerializer.Deserialize<List<string>>(myUser.RoleView.TabViewJson) ?? [];
            rolePermissionService.SetPermissionInput(rolePermission, permissions);
            currentUser.RoleJson = myUser.RoleView.TabViewJson;
            currentUser.TeamList = (await effectiveTeamResolver.GetEffectiveTeamNamesAsync(myUser.Id)).ToList();
            currentUser.IsAuthenticated = true;
            currentUserService.CurrentUser.CopyFrom(currentUser);

            // UI 權限判定改以 RBAC 表為單一權威來源（含多角色聯集），與 API 端 IPermissionChecker 對齊；
            // 覆寫 CopyFrom 由 TabViewJson 反序列化得到的 RoleList。TabViewJson 僅保留供角色編輯畫面回填。
            currentUserService.CurrentUser.RoleList =
                (await permissionChecker.GetEffectivePermissionKeysAsync(myUser.Id)).ToList();

            // 每次導覽都會跑，屬於流程細節而非使用者意圖，因此記在 Debug。
            logger.LogDebug(
                "Authentication state initialized for UserId={UserId}, Account={Account}, IsAdmin={IsAdmin}.",
                myUser.Id,
                myUser.Account,
                myUser.IsAdmin);

            return AuthenticationCheckResult.Succeeded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize permission data for UserId={UserId}.", id);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return AuthenticationCheckResult.InvalidUser;
        }
    }

    private static bool IsChangePasswordPage(NavigationManager navigationManager)
    {
        var currentPath = new Uri(navigationManager.Uri).AbsolutePath.Trim('/');
        return string.Equals(currentPath, "ChangePassword", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MyUserAdapterModel?> GetUserInformation(AuthenticationStateProvider authStateProvider)
    {
        logger.LogDebug("Loading current user information from authentication state.");

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Cannot load current user information because the principal is not authenticated.");
            return null;
        }

        var idClaimValue = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value;

        if (!int.TryParse(idClaimValue, out var id) || id <= 0)
        {
            logger.LogWarning("Cannot load current user information because claim {ClaimType} is missing or invalid.", ClaimTypes.Sid);
            return null;
        }

        return await myUserService.GetAsync(id);
    }

    public bool CheckIsAdmin()
    {
        var isAdmin = currentUserService.CurrentUser.IsAdmin;
        logger.LogDebug("Checked admin permission for UserId={UserId}. IsAdmin={IsAdmin}", currentUserService.CurrentUser.Id, isAdmin);
        return isAdmin;
    }

    /// <summary>
    /// 檢查目前使用者能否進入某個頁面（也用於側邊選單過濾）。
    ///
    /// 通過條件：管理員、擁有裸頁面鍵、或擁有該頁的 <c>view</c> 動作鍵。
    /// ⚠️ 第三項不可省略 —— 角色矩陣只勾「檢視」時產生的是「頁面:view」，**不含裸鍵**，
    /// 少了這一項，唯讀角色會連頁面都打不開、選單也不顯示，「可看不可改」就只剩 API 端生效。
    /// 這是 <see cref="CheckAccessAction"/>「動作鍵不中則退回裸鍵」的鏡像。
    /// </summary>
    public bool CheckAccessPage(string name)
    {
        // 管理員一律通過：GetEffectivePermissionKeysAsync 不含 admin 隱含全通過（回空集合），
        // 故此處需比照 CheckAccessAction 短路，否則管理員選單/頁面會全被隱藏。
        if (currentUserService.CurrentUser.IsAdmin)
        {
            return true;
        }

        var keys = currentUserService.CurrentUser.RoleList;
        var result = keys.Contains(name)
            || keys.Contains(PermissionKey.For(name, PermissionActions.View));
        logger.LogDebug(
            "Checked page access for UserId={UserId}, Page={PageName}, Allowed={Allowed}.",
            currentUserService.CurrentUser.Id,
            name,
            result);
        return result;
    }

    /// <summary>
    /// 檢查目前使用者是否具備某頁面的特定動作權限（view/create/edit/delete/export）。
    /// 管理員一律通過；擁有動作鍵「頁面:動作」或裸頁面鍵（舊制＝全動作）即通過。
    /// 供 Razor 檢視依動作顯示/停用按鈕使用。
    /// </summary>
    public bool CheckAccessAction(string page, string action)
    {
        var currentUser = currentUserService.CurrentUser;
        if (currentUser.IsAdmin)
        {
            return true;
        }

        var keys = currentUser.RoleList;
        return keys.Contains(PermissionKey.For(page, action)) || keys.Contains(page);
    }
}
