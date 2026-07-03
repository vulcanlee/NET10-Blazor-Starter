using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;

namespace MyProject.Business.Services.Other;

public sealed class PermissionChecker : IPermissionChecker
{
    private readonly BackendDBContext context;

    public PermissionChecker(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task<bool> HasPermissionAsync(int userId, string permissionKey)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView)
            .FirstOrDefaultAsync(x => x.Id == userId);

        if (user is null)
        {
            return false;
        }

        if (user.IsAdmin)
        {
            return true;
        }

        var keys = ParsePermissionKeys(user.RoleView?.TabViewJson);
        return keys.Contains(permissionKey);
    }

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId)
    {
        var user = await context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView)
            .FirstOrDefaultAsync(x => x.Id == userId);

        return user is null
            ? []
            : ParsePermissionKeys(user.RoleView?.TabViewJson);
    }

    private static HashSet<string> ParsePermissionKeys(string? tabViewJson)
    {
        if (string.IsNullOrWhiteSpace(tabViewJson))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var names = JsonSerializer.Deserialize<List<string>>(tabViewJson) ?? [];
            return names.ToHashSet(StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
