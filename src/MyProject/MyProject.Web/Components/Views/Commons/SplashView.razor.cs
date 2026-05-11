using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MyProject.Business.Services.Other;

namespace MyProject.Web.Components.Views.Commons;

public partial class SplashView
{
    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;
    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var checkResult = await AuthenticationStateHelper
            .Check(authStateProvider, NavigationManager);
            if (checkResult == AuthenticationCheckResult.Succeeded)
            {
                NavigationManager.NavigateTo("/app", true, true);
            }
        }
    }
}
