using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Dtos.Commons;
using Microsoft.Extensions.Logging;

namespace MyProject.Business.Repositories;

public class CategoryRepository
{
    private readonly BackendDBContext context;
    private readonly ILogger<CategoryRepository> logger;

    public CategoryRepository(BackendDBContext context, ILogger<CategoryRepository> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    #region 查詢方法

    public async Task<Category?> GetByIdAsync(int id)
    {
        return await context.Category.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<Category>> GetPagedAsync(CategorySearchRequestDto request)
    {
        var query = context.Category.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(request.Keyword))
        {
            query = query.Where(x =>
                x.Name.Contains(request.Keyword) ||
                (x.Description != null && x.Description.Contains(request.Keyword)));
        }

        if (request.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == request.IsEnabled.Value);
        }

        query = request.SortBy?.ToLower() switch
        {
            "name" => request.SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
            "isenabled" => request.SortDescending ? query.OrderByDescending(x => x.IsEnabled) : query.OrderBy(x => x.IsEnabled),
            "createdat" => request.SortDescending ? query.OrderByDescending(x => x.CreatedAt) : query.OrderBy(x => x.CreatedAt),
            "updatedat" => request.SortDescending ? query.OrderByDescending(x => x.UpdatedAt) : query.OrderBy(x => x.UpdatedAt),
            _ => query.OrderByDescending(x => x.UpdatedAt),
        };

        var totalCount = await query.CountAsync();

        // 記在 Debug：這是排查「為什麼查不到資料」的第一手線索。
        // 只記筆數與分頁參數，不記關鍵字內容 —— 使用者的搜尋字串可能包含個資。
        logger.LogDebug(
            "Paged category query executed. PageIndex={PageIndex}, PageSize={PageSize}, SortBy={SortBy}, SortDescending={SortDescending}, HasKeyword={HasKeyword}, TotalCount={TotalCount}",
            request.PageIndex, request.PageSize, request.SortBy, request.SortDescending,
            string.IsNullOrWhiteSpace(request.Keyword) == false, totalCount);
        var items = await query
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<Category>
        {
            Items = items,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    public async Task<bool> ExistsByNameAsync(string name, int? excludeId = null)
    {
        var query = context.Category.Where(x => x.Name == name);
        if (excludeId.HasValue)
        {
            query = query.Where(x => x.Id != excludeId.Value);
        }
        return await query.AnyAsync();
    }

    #endregion

    #region 新增 / 更新 / 刪除

    public async Task<Category> AddAsync(Category category)
    {
        category.CreatedAt = DateTime.Now;
        category.UpdatedAt = DateTime.Now;

        await context.Category.AddAsync(category);
        await context.SaveChangesAsync();

        return category;
    }

    public async Task<bool> UpdateAsync(Category category)
    {
        var existing = await context.Category.FindAsync(category.Id);
        if (existing == null)
        {
            return false;
        }

        category.UpdatedAt = DateTime.Now;
        category.CreatedAt = existing.CreatedAt;

        context.Entry(existing).CurrentValues.SetValues(category);
        await context.SaveChangesAsync();

        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var category = await context.Category.FindAsync(id);
        if (category == null)
        {
            return false;
        }

        context.Category.Remove(category);
        await context.SaveChangesAsync();

        return true;
    }

    #endregion
}
