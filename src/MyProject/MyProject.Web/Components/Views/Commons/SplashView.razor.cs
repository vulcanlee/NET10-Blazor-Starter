using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MyProject.Business.Services.Other;

namespace MyProject.Web.Components.Views.Commons;

public partial class SplashView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; }
    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; }
    [Inject]
    public NavigationManager NavigationManager { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var checkResult = await AuthenticationStateHelper
            .Check(authStateProvider, NavigationManager);
            if (checkResult == true)
            {
                await Task.Delay(500); // Simulate a delay for splash screen
                NavigationManager.NavigateTo("/projects", true, true);
            }
            else
            {
                NavigationManager.NavigateTo("/auths/logout", true, true);

            }
        }
    }
}
