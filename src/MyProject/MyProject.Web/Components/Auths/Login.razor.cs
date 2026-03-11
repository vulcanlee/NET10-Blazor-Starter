using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using System.Security.Claims;

namespace MyProject.Web.Components.Auths
{
    public partial class Login
    {
        string errorMessage = string.Empty;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = new();

        [SupplyParameterFromQuery]
        private string? ReturnUrl { get; set; }

        [Inject]
        public ILogger<Login> Logger { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        string message = string.Empty;

        protected override Task OnInitializedAsync()
        {
            Input ??= new();
            Logger.LogDebug("Login component initialized. ReturnUrl={ReturnUrl}", ReturnUrl);
            return Task.CompletedTask;
        }

        public async Task LoginUser()
        {
            message = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(Input.Account))
            {
                message = "請輸入帳號";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because account is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.Password))
            {
                message = "請輸入密碼";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because password is empty. Account={Account}", Input.Account);
                return;
            }

            (string result, MyUser myUser) = await MyUserServiceLogin.LoginAsync(Input.Account, Input.Password);
            if (!string.IsNullOrEmpty(result))
            {
                Logger.LogWarning("Login failed for Account={Account}. Reason={Reason}", Input.Account, result);
                message = result;
            }
            else
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Role, "User"),
                    new(ClaimTypes.Name, myUser.Name),
                    new(ClaimTypes.NameIdentifier, myUser.Account),
                    new(ClaimTypes.Sid, myUser.Id.ToString()),
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                string returnUrl = string.IsNullOrEmpty(ReturnUrl) ? "/" : ReturnUrl;
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = true,
                    RedirectUri = returnUrl,
                };

                try
                {
                    await HttpContext.SignInAsync(
                        "CookieAuthenticationScheme",
                        new ClaimsPrincipal(claimsIdentity),
                        authProperties);

                    Logger.LogInformation(
                        "Login succeeded for Account={Account}, UserId={UserId}, RedirectUri={RedirectUri}.",
                        Input.Account,
                        myUser.Id,
                        returnUrl);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    Logger.LogError(ex, "Sign-in failed for Account={Account}.", Input.Account);
                }
            }

            errorMessage = string.IsNullOrEmpty(message) ? string.Empty : "alert-danger";
        }

        private sealed class InputModel
        {
            public string Account { get; set; } = string.Empty;

            public string Password { get; set; } = string.Empty;
        }
    }
}
