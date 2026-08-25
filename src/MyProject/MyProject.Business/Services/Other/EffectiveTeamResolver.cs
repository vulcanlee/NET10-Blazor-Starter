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
                foreach (var name in TeamJsonHelper.Deserialize(json))
                {
                    AddDistinct(name, seen, result);
                }
            }
        }

        return result;
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
