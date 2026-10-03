using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Dtos.Commons;
using Microsoft.Extensions.Logging;

namespace MyProject.Business.Repositories;

public class TeamRepository
{
    private readonly BackendDBContext context;
    private readonly ILogger<TeamRepository> logger;

    public TeamRepository(BackendDBContext context, ILogger<TeamRepository> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    #region 查詢方法

    public async Task<Team?> GetByIdAsync(int id)
    {
        return await context.Team.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Team>> GetPagedAsync(TeamSearchRequestDto request)
    {
        var query = context.Team.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Keyword) ||
                (x.Code != null && x.Code.Contains(request.Keyword)) ||
                (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

        query = request.SortBy?.ToLower() switch
        {
            "name" => request.SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
            "code" => request.SortDescending ? query.OrderByDescending(x => x.Code) : query.OrderBy(x => x.Code),
            "isenabled" => request.SortDescending ? query.OrderByDescending(x => x.IsEnabled) : query.OrderBy(x => x.IsEnabled),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();

        // 記在 Debug：這是排查「為什麼查不到資料」的第一手線索。
        // 只記筆數與分頁參數，不記關鍵字內容 —— 使用者的搜尋字串可能包含個資。
        logger.LogDebug(
            "Paged team query executed. PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, HasKeyword={HasKeyword}, TotalCount={TotalCount}",
            request.PageIndex, request.PageSize, request.SortBy, request.SortDescending,
            string.IsNullOrWhiteSpace(request.Keyword) == false, totalCount);
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<Team>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    /// <summary>
    /// 名稱是否已被使用。比對語意必須與 Blazor 路徑的 BeforeAddCheckAsync 一致
    /// （先正規化、再不分大小寫），否則同一份資料會出現「UI 擋得下、API 擋不下」。
    /// </summary>
    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var normalized = NameNormalizer.Normalize(name);
        var query = context.Team.Where(x => x.Name.ToLower() == normalized.ToLower());
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    /// <summary>
    /// 代號是否已被使用。與 ExistsByNameAsync 同樣的理由要與服務層語意一致。
    /// 空白代號視為「未填」，一律回 false（未填不算重複）。
    /// </summary>
    public async Task<bool> ExistsByCodeAsync(string code, int? excludeId = null)
    {
        if (NameNormalizer.NormalizeOptional(code) is not { } normalized)
        {
            return false;
        }

        var query = context.Team.Where(x => x.Code != null && x.Code.ToLower() == normalized.ToLower());
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Team> AddAsync(Team team)
    {
        team.CreatedAt = DateTime.Now;
        team.UpdatedAt = DateTime.Now;

        // 不信任客戶端傳來的版本號（POST 會忽略它）；AutoMapper 從 DTO 映射時可能是 null。
        // 一律由資料庫配號：0.9.93 之前會直接採用客戶端傳來的 Id，與既有資料撞號時回 500（軟刪除的列也一直佔著 Id）。
        team.Id = 0;
        team.ConcurrencyStamp = ConcurrencyStampHelper.New();
        await context.Team.AddAsync(team);
        await context.SaveChangesAsync();

        return team;
    }

    public async Task<bool> UpdateAsync(Team team)
    {
        var existing = await context.Team.FindAsync(team.Id);
        if (existing == null)
        {
            return false;
        }

        team.UpdatedAt = DateTime.Now;
        team.CreatedAt = existing.CreatedAt;

        context.Entry(existing).CurrentValues.SetValues(team);
        // FindAsync 載入的 OriginalValue 是資料庫目前的版本號，必須改成客戶端帶來的值才比對得出衝突；
        // 衝突時 SaveChanges 丟 DbUpdateConcurrencyException，由 controller 轉成 409。
        ConcurrencyStampHelper.Apply(context.Entry(existing), team.ConcurrencyStamp);
        SoftDeleteHelper.ProtectFlags(context.Entry(existing));
        await context.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// 軟刪除（0.9.94 起）：可在畫面的「顯示已刪除」中還原或永久刪除。
    /// FindAsync 會套用軟刪除過濾，已刪除的資料回 false（controller 回 404）。
    /// </summary>
    public async Task<bool> DeleteAsync(int id, string? actorAccount)
    {
        var team = await context.Team.FindAsync(id);
        if (team == null)
        {
            return false;
        }

        SoftDeleteHelper.MarkDeleted(team, actorAccount);
        await context.SaveChangesAsync();

        return true;
    }

    #endregion
}
