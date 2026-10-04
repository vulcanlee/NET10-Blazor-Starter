using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.Business.Helpers;
using Microsoft.Extensions.Logging;

namespace MyProject.Business.Services.Other;

public sealed class EffectiveTeamResolver : IEffectiveTeamResolver
{
    private readonly BackendDBContext context;
    private readonly ILogger<EffectiveTeamResolver> logger;

    public EffectiveTeamResolver(BackendDBContext context, ILogger<EffectiveTeamResolver> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetEffectiveTeamNamesAsync(int userId)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            logger.LogWarning(
                "Could not resolve effective teams because the user does not exist. UserId={UserId}", userId);
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        // 1) 直接綁在使用者的團隊（UserTeam）
        var userTeamNames = await context.UserTeam
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Join(context.Team, ut => ut.TeamId, t => t.Id, (ut, t) => t.Name)
            .ToListAsync();

        foreach (var name in userTeamNames)
        {
            AddDistinct(name, seen, result);
        }

        // 2) 使用者角色（UserRole ∪ legacy RoleViewId）的預設團隊
        var roleIds = await context.UserRole
            .AsNoTracking()
            .Where(x => x.MyUserId == userId)
            .Select(x => x.RoleViewId)
            .ToListAsync();

        if (user.RoleViewId.HasValue && !roleIds.Contains(user.RoleViewId.Value))
        {
            roleIds.Add(user.RoleViewId.Value);
        }

        if (roleIds.Count > 0)
        {
            var roleTeamJsons = await context.RoleView
                .AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .Select(r => r.DefaultTeamsJson)
                .ToListAsync();

            foreach (var json in roleTeamJsons)
            {
                foreach (var name in TeamJsonHelper.Deserialize(json, logger))
                {
                    AddDistinct(name, seen, result);
                }
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<int>> GetUserIdsInTeamAsync(string teamName)
    {
        var name = (teamName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return [];
        }

        var result = new HashSet<int>();

        // 1) 直接綁在使用者的團隊（UserTeam；經 context.Team 排除已刪除的團隊）
        var direct = await context.UserTeam
            .AsNoTracking()
            .Join(context.Team, ut => ut.TeamId, t => t.Id, (ut, t) => new { ut.MyUserId, t.Name })
            .ToListAsync();
        result.UnionWith(direct.Where(x => string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).Select(x => x.MyUserId));

        // 2) 預設團隊含這個名稱的角色（經 context.RoleView 排除已刪除的角色）→ 以它為額外角色或主要角色的使用者
        var roles = await context.RoleView
            .AsNoTracking()
            .Select(r => new { r.Id, r.DefaultTeamsJson })
            .ToListAsync();
        var roleIds = roles
            .Where(r => TeamJsonHelper.Deserialize(r.DefaultTeamsJson, logger).Any(x => string.Equals((x ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Id)
            .ToList();
        if (roleIds.Count > 0)
        {
            result.UnionWith(await context.UserRole.AsNoTracking().Where(x => roleIds.Contains(x.RoleViewId)).Select(x => x.MyUserId).ToListAsync());
            result.UnionWith(await context.MyUser.AsNoTracking().Where(x => x.RoleViewId != null && roleIds.Contains(x.RoleViewId.Value)).Select(x => x.Id).ToListAsync());
        }

        return result.Order().ToList();
    }

    private static void AddDistinct(string? name, HashSet<string> seen, List<string> result)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > 0 && seen.Add(trimmed))
        {
            result.Add(trimmed);
        }
    }
}
