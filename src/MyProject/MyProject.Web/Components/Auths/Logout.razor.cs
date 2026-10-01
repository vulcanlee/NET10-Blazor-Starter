using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;

namespace MyProject.Web.Components.Auths
{
    public partial class Logout
    {
        private readonly ILogger<Logout> logger;
        private readonly IAuditLogService auditLogService;
        string errorMessage = string.Empty;

        public Logout(ILogger<Logout> logger, IAuditLogService auditLogService)
        {
            this.logger = logger;
            this.auditLogService = auditLogService;
        }

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            try
            {
                // 登出後 User 就清空了，身分必須在 SignOut 之前取（LOG-15）。
                var (account, userId) = RequestActorResolver.Resolve(HttpContext.User);

                await HttpContext.SignOutAsync(MagicObjectHelper.CookieScheme);
                await Task.Delay(200);
                logger.LogInformation("User logout completed successfully. Account={Account}, UserId={UserId}", account, userId);

                // 未登入狀態打開登出頁（重複點擊、過期 Cookie）不算一次登出，不寫稽核。
                if (userId is not null)
                {
                    await auditLogService.WriteAsync(AuditActions.Logout, success: true, actorUserId: userId, actorAccount: account);
                }
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
