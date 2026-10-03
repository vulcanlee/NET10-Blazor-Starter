using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;

namespace MyProject.Business.Startup;

/// <summary>
/// 「預設角色」（新建帳號與 Google 自動建立的帳號預設取得的角色）。
///
/// <list type="bullet">
/// <item>不存在 → 以全部頁面建立。</item>
/// <item>已存在 → 只加入<b>新頁面</b>：程式碼有、但權限目錄（<see cref="Permission"/> 表）裡從未出現過的頁面鍵。
/// 管理員移除過的頁面仍在目錄裡，因此不會被加回去。</item>
/// </list>
///
/// 沿革：0.9.90 之前每次啟動都把 <c>TabViewJson</c> 蓋回全部頁面，RBAC 回填再依它把權限列補回 ——
/// 管理員對預設角色的限制，重啟就失效（而且是實際權限，不只畫面）。
///
/// ⚠️ 新頁面的 Permission 列在<b>同一次 SaveChanges</b> 寫入：偵測依據就是目錄裡有沒有這個鍵，
/// 若交給之後的 RBAC 回填補目錄，兩者的先後順序一旦改變，新頁面就永遠不會被偵測到。
/// </summary>
public sealed class DefaultRoleViewSeeder : IDatabaseSeeder
{
    private readonly BackendDBContext dbContext;
    private readonly RolePermissionService rolePermissionService;
    private readonly ILogger<DefaultRoleViewSeeder> logger;

    public DefaultRoleViewSeeder(
        BackendDBContext dbContext,
        RolePermissionService rolePermissionService,
        ILogger<DefaultRoleViewSeeder> logger)
    {
        this.dbContext = dbContext;
        this.rolePermissionService = rolePermissionService;
        this.logger = logger;
    }

    public int Order => 10;

    public string Name => "預設角色";

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // 舊資料庫可能還有尾端帶空白的鍵（正規化要到 RBAC 回填才做），比對時一律 Trim，
        // 否則 "登出 " 會讓 "登出" 被誤判成新頁面。
        var existingKeys = (await dbContext.Permission.Select(x => x.Key).ToListAsync(cancellationToken))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var newPermissions = rolePermissionService.CreatePermissionCatalog()
            .Where(x => !existingKeys.Contains(x.Key))
            .ToList();

        var role = await dbContext.RoleView
            .FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色, cancellationToken);

        if (role is null)
        {
            dbContext.RoleView.Add(new RoleView
            {
                Name = MagicObjectHelper.預設角色,
                TabViewJson = rolePermissionService.GetRolePermissionAllNameToJson(),
            });
            logger.LogInformation("Seeded default role view.");
        }
        else if (newPermissions.Count > 0)
        {
            AddNewPages(role, newPermissions.Select(x => x.Key).ToList());
        }

        dbContext.Permission.AddRange(newPermissions);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddNewPages(RoleView role, IReadOnlyList<string> newKeys)
    {
        List<string> names;
        try
        {
            names = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(role.TabViewJson ?? "[]") ?? [];
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            // 壞掉的 JSON 只可能來自手動改資料庫；這時改寫會蓋掉原本的內容，寧可不動並留下紀錄。
            logger.LogWarning(ex, "Default role permission JSON is malformed; new pages were not added. NewKeys={NewKeys}", string.Join(",", newKeys));
            return;
        }

        var present = names.Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal);
        var added = newKeys.Where(present.Add).ToList();
        if (added.Count == 0)
        {
            return;
        }

        names.AddRange(added);
        role.TabViewJson = Newtonsoft.Json.JsonConvert.SerializeObject(names);
        logger.LogInformation("Added new pages to the default role. Pages={Pages}", string.Join(",", added));
    }
}
