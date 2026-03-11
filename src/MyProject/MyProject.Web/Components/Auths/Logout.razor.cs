using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Auths
{
    public partial class Logout
    {
        private readonly ILogger<Logout> logger;
        string errorMessage = string.Empty;

        public Logout(ILogger<Logout> logger)
        {
            this.logger = logger;
        }

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            try
            {
                await HttpContext.SignOutAsync(MagicObjectHelper.CookieScheme);
                await Task.Delay(200);
                logger.LogInformation("User logout completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Logout flow failed unexpectedly.");
                errorMessage = ex.Message;
            }

            NavigationManager.NavigateTo("/Auths/Login", forceLoad: true);
        }
    }
}
