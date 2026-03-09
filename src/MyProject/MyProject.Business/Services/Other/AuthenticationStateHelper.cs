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

    public AuthenticationStateHelper(ILogger<AuthenticationStateHelper> logger,
        IMapper mapper, MyUserService myUserService,
        CurrentUserService currentUserService,
        RolePermissionService rolePermissionService)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.currentUserService = currentUserService;
        this.rolePermissionService = rolePermissionService;
    }

    /// <summary>
    /// Verifies the authentication state of the current user and initializes user-specific settings if authenticated.
    /// </summary>
    /// <remarks>This method checks the authentication state of the current user and retrieves their
    /// associated settings and permissions. If the user is not authenticated or their role information is missing, the
    /// method redirects to the logout page.</remarks>
    /// <param name="authStateProvider">The <see cref="AuthenticationStateProvider"/> used to retrieve the current authentication state.</param>
    /// <param name="NavigationManager">The <see cref="NavigationManager"/> used to navigate to specific routes if authentication fails.</param>
    /// <returns><see langword="true"/> if the user is authenticated and their settings are successfully initialized;  otherwise,
    /// <see langword="false"/>.</returns>
    public async Task<bool> Check(AuthenticationStateProvider authStateProvider,
        NavigationManager NavigationManager)
    {
        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is not null && user.Identity.IsAuthenticated)
        {
            var id = user.Claims
                .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?.Value.ToInt();
            if (id is not null)
            {
                var myuser = await myUserService.GetAsync(id.Value);

                #region 檢查是否需要強制變更密碼
                bool needChangePassword = await myUserService.NeedChangePasswordAsync(myuser);
                if (needChangePassword)
                {
                    // 延遲導航，避免在初始化過程中立即導航
                    await Task.Delay(200);
                    NavigationManager.NavigateTo("/ChangePassword", true);
                    return false;
                }
                #endregion

                CurrentUser currentUser = mapper.Map<CurrentUser>(myuser);

                RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();
                if (myuser.RoleView == null)
                {
                    // 延遲導航，避免在初始化過程中立即導航
                    await Task.Delay(200);
                    NavigationManager.NavigateTo("/Auths/Logout", true, true);
                    return false;
                }
                List<string> permissions = JsonSerializer
                    .Deserialize<List<string>>(myuser.RoleView.TabViewJson);
                rolePermissionService
                    .SetPermissionInput(rolePermission, permissions);
                currentUser.RoleJson = myuser.RoleView.TabViewJson;


                currentUserService.CurrentUser.CopyFrom(currentUser);
                currentUser.IsAuthenticated = true;
                return true;
            }
            else
            {
                // 延遲導航，避免在初始化過程中立即導航
                await Task.Delay(200);
                NavigationManager.NavigateTo("/Auths/Logout", true, true);
                return false;
            }
        }
        else
        {
            // 延遲導航，避免在初始化過程中立即導航
            await Task.Delay(200);
            NavigationManager.NavigateTo("/Auths/Logout", true, true);
            return false;
        }
    }

    public async Task<MyUserAdapterModel> GetUserInformation(AuthenticationStateProvider authStateProvider)
    {
        MyUserAdapterModel result = new();
        var authState = await authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        if (user.Identity is not null && user.Identity.IsAuthenticated)
        {
            var id = user.Claims
                .FirstOrDefault(c => c.Type == ClaimTypes.Sid)?.Value.ToInt();
            if (id is not null)
            {
                result = await myUserService.GetAsync(id.Value);
            }
            else
                return null;
        }
        else
            return null;
        return result;
    }

    public bool CheckIsAdmin()
    {
        return currentUserService.CurrentUser.IsAdmin;
    }

    public bool CheckAccessPage(string name)
    {
        var result = currentUserService.CurrentUser.RoleList.Contains(name);
        return result;
    }
}
