using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;

namespace MyProject.Web.Auth;

/// <summary>
/// 解析目前使用者的紀錄存取範圍：
/// - Blazor 互動情境：使用已填入的 <see cref="CurrentUserService"/>。
/// - Web API／檔案下載（JWT/Cookie）情境：由 HttpContext 的 Sid claim 載入使用者與其角色團隊。
/// 兩者皆無法解析時，回傳「非管理員、無團隊」，僅能看到無團隊（公開）紀錄。
/// </summary>
public sealed class RecordAccessScopeProvider : IRecordAccessScopeProvider
{
    private readonly CurrentUserService currentUserService;
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly BackendDBContext context;

    public RecordAccessScopeProvider(
        CurrentUserService currentUserService,
        IHttpContextAccessor httpContextAccessor,
        BackendDBContext context)
    {
        this.currentUserService = currentUserService;
        this.httpContextAccessor = httpContextAccessor;
        this.context = context;
    }

    public async Task<RecordAccessScope> GetAsync()
    {
        var currentUser = currentUserService.CurrentUser;
        if (currentUser.IsAuthenticated)
        {
            return new RecordAccessScope(currentUser.IsAdmin, currentUser.TeamList ?? []);
        }

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var sid = principal.FindFirst(ClaimTypes.Sid)?.Value;
            if (int.TryParse(sid, out var id) && id > 0)
            {
                var user = await context.MyUser
                    .AsNoTracking()
                    .Include(x => x.RoleView)
                    .FirstOrDefaultAsync(x => x.Id == id);

                if (user is not null)
                {
                    var teams = TeamJsonHelper.Deserialize(user.RoleView?.DefaultTeamsJson);
                    return new RecordAccessScope(user.IsAdmin, teams);
                }
            }
        }

        return new RecordAccessScope(false, []);
    }
}
