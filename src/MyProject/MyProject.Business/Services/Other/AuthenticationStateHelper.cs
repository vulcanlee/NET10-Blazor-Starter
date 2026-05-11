using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Models.Others;
using MyProject.Share.Extensions;
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

    public AuthenticationStateHelper(
        ILogger<AuthenticationStateHelper> logger,
        IMapper mapper,
        MyUserService myUserService,
        CurrentUserService currentUserService,
        RolePermissionService rolePermissionService)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.currentUserService = currentUserService;
        this.rolePermissionService = rolePermissionService;
    }

    public async Task<bool> Check(AuthenticationStateProvider authStateProvider, NavigationManager navigationManager)
    {
        logger.LogDebug("Checking authentication state for current request.");

        if (currentUserService.CurrentUser.IsAuthenticated)
        {
            logger.LogDebug("Authentication state already initialized for UserId={UserId}.", currentUserService.CurrentUser.Id);
            return true;
        }

        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is null || !user.Identity.IsAuthenticated)
        {
            logger.LogWarning("Authentication check failed because the current principal is not authenticated.");
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return false;
        }

        var id = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value
            .ToInt();

        if (id is null)
        {
            logger.LogWarning("Authentication check failed because claim {ClaimType} is missing.", ClaimTypes.Sid);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return false;
        }

        var myUser = await myUserService.GetAsync(id.Value);
        logger.LogDebug("Resolved authenticated user information for UserId={UserId}.", id.Value);

        bool needChangePassword = await myUserService.NeedChangePasswordAsync(myUser);
        if (needChangePassword && !IsChangePasswordPage(navigationManager))
        {
            logger.LogWarning("User {UserId} is required to change password before continuing.", id.Value);
            await Task.Delay(200);
            navigationManager.NavigateTo("/ChangePassword", true);
            return false;
        }

        CurrentUser currentUser = mapper.Map<CurrentUser>(myUser);
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();

        if (myUser.RoleView == null)
        {
            logger.LogWarning("User {UserId} does not have a role view assigned. Redirecting to logout.", id.Value);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return false;
        }

        try
        {
            List<string> permissions = JsonSerializer.Deserialize<List<string>>(myUser.RoleView.TabViewJson) ?? [];
            rolePermissionService.SetPermissionInput(rolePermission, permissions);
            currentUser.RoleJson = myUser.RoleView.TabViewJson;
            currentUser.IsAuthenticated = true;
            currentUserService.CurrentUser.CopyFrom(currentUser);

            logger.LogInformation(
                "Authentication state initialized for UserId={UserId}, Account={Account}, IsAdmin={IsAdmin}.",
                myUser.Id,
                myUser.Account,
                myUser.IsAdmin);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize permission data for UserId={UserId}.", id.Value);
            await Task.Delay(200);
            navigationManager.NavigateTo("/Auths/Logout", true, true);
            return false;
        }
    }

    private static bool IsChangePasswordPage(NavigationManager navigationManager)
    {
        var currentPath = navigationManager.ToBaseRelativePath(navigationManager.Uri).Trim('/');
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

        var id = user.Claims
            .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?
            .Value
            .ToInt();

        if (id is null)
        {
            logger.LogWarning("Cannot load current user information because claim {ClaimType} is missing.", ClaimTypes.Sid);
            return null;
        }

        return await myUserService.GetAsync(id.Value);
    }

    public bool CheckIsAdmin()
    {
        var isAdmin = currentUserService.CurrentUser.IsAdmin;
        logger.LogDebug("Checked admin permission for UserId={UserId}. IsAdmin={IsAdmin}", currentUserService.CurrentUser.Id, isAdmin);
        return isAdmin;
    }

    public bool CheckAccessPage(string name)
    {
        var result = currentUserService.CurrentUser.RoleList.Contains(name);
        logger.LogDebug(
            "Checked page access for UserId={UserId}, Page={PageName}, Allowed={Allowed}.",
            currentUserService.CurrentUser.Id,
            name,
            result);
        return result;
    }
}
