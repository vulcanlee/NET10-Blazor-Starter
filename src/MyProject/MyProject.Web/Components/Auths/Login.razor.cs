using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using MyProject.AccessDatas.Models;
using MyProject.Share.Helpers;
using System.Security.Claims;

namespace MyProject.Web.Components.Auths
{
    public partial class Login
    {
        string errorMessage = string.Empty;
        string errorMessageClass = "";

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = new();

        [SupplyParameterFromQuery]
        private string? ReturnUrl { get; set; }

        [Inject]
        public ILogger<Login> Logger { get; set; }

        string message = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            try
            {
                if (HttpContext != null)
                {
                    if (HttpMethods.IsGet(HttpContext.Request.Method))
                    {
                        // Logger.LogInformation("Cookie : Login: OnInitializedAsync Need SignOut");
                        // Clear the existing external cookie to ensure a clean login process
                        // await HttpContext.SignOutAsync("CookieAuthenticationScheme");
                    }
                    else
                    {
                        // Logger.LogInformation("Cookie : Login: OnInitializedAsync No SignOut");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "登入初始化發生例外異常-1");
            }
        }

        public async Task LoginUser()
        {
        }
        //public async Task LoginUser()
        //{
        //    message = "";
        //    errorMessage = "";
        //    if (string.IsNullOrEmpty(Input.Account))
        //    {
        //        message = "請輸入帳號";
        //        errorMessage = "alert-danger";
        //        return;
        //    }
        //    else if (string.IsNullOrEmpty(Input.Password))
        //    {
        //        message = "請輸入密碼";
        //        errorMessage = "alert-danger";
        //        return;
        //    }
        //    (string result, MyUser myUser) = await MyUserServiceLogin.LoginAsync(Input.Account, Input.Password);
        //    if (result != string.Empty)
        //    {
        //        Logger.LogWarning($"""使用者 {Input.Account} 登入失敗，原因：{result} """);
        //        message = result;
        //    }
        //    else
        //    {
        //        #region 加入這個使用者需要用到的 宣告類型 Claim Type
        //        var claims = new List<Claim>
        //        {
        //            new Claim(ClaimTypes.Role, "User"),
        //            new Claim(ClaimTypes.Name, myUser.Name),
        //            new Claim(ClaimTypes.NameIdentifier, myUser.Account),
        //            new Claim(ClaimTypes.Sid, myUser.Id.ToString()),
        //        };
        //        #endregion

        //        #region 建立 宣告式身分識別
        //        // ClaimsIdentity類別是宣告式身分識別的具體執行, 也就是宣告集合所描述的身分識別
        //        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        //        #endregion

        //        #region 建立關於認證階段需要儲存的狀態
        //        string returnUrl = string.IsNullOrEmpty(ReturnUrl) ? "/" : ReturnUrl;
        //        var authProperties = new AuthenticationProperties
        //        {
        //            IsPersistent = true,
        //            RedirectUri = returnUrl,
        //        };
        //        #endregion

        //        #region 進行使用登入
        //        try
        //        {
        //            await HttpContext.SignInAsync(
        //                "CookieAuthenticationScheme",
        //            new ClaimsPrincipal(claimsIdentity),
        //            authProperties);

        //            Logger.LogInformation($"""使用者 {Input.Account} 登入成功""");

        //        }
        //        catch (Exception ex)
        //        {
        //            message = ex.Message;
        //            Logger.LogError(ex, $"""使用者 {Input.Account} 登入發生例外異常，原因：{ex.Message} """);
        //        }
        //        #endregion
        //    }
        //    if (string.IsNullOrEmpty(message))
        //    {
        //        errorMessage = "";
        //    }
        //    else
        //    {
        //        errorMessage = "alert-danger";
        //    }
        //}

        private sealed class InputModel
        {
            // [Required]
            // [EmailAddress]
            public string Account { get; set; } = "";

            // [Required]
            // [DataType(DataType.Password)]
            public string Password { get; set; } = "";
        }
    }
}
