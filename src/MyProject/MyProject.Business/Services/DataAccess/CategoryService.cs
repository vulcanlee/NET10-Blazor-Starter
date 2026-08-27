using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

public class CategoryService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IRecordAccessScopeProvider accessScope;

    public IMapper Mapper { get; }
    public ILogger<CategoryService> Logger { get; }

    public CategoryService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<CategoryService> logger,
        IRecordAccessScopeProvider accessScope)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
    }

    /// <summary>
    /// 套用分類的團隊可見性。
    ///
    /// 刻意與紀錄（Project）不同：紀錄的規則是「使用者沒有團隊 → 只看得到公開紀錄」，
    /// 分類則是「使用者沒有團隊 → 視為不受限，可見全部分類」。
    /// 分類可見性是操作便利性的過濾（避免下拉清單塞滿用不到的項目），不是安全邊界，
    /// 安全邊界由 RBAC（HasPermission / IPermissionChecker）負責。
    /// </summary>
    private static IQueryable<Category> ApplyTeamVisibility(IQueryable<Category> source, RecordAccessScope scope)
    {
        if (scope.IsAdmin || scope.Teams.Count == 0)
        {
            return source;
        }

        return source.Where(TagStringHelper.BuildTeamAccessPredicate<Category>(x => x.Teams, scope.Teams));
    }

    /// <summary>
    /// 單筆分類的可見性判斷，規則與 <see cref="ApplyTeamVisibility"/> 一致。
    /// </summary>
    private static bool IsVisible(Category item, RecordAccessScope scope)
    {
        return scope.Teams.Count == 0
            || TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin);
    }

    public async Task<DataRequestResult<CategoryAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading categories. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<CategoryAdapterModel> result = new();
        IQueryable<Category> dataSource = context.Category.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        dataSource = ApplyTeamVisibility(dataSource, await accessScope.GetAsync());

        IOrderedQueryable<Category>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(CategoryAdapterModel.Name))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(CategoryAdapterModel.IsEnabled))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(CategoryAdapterModel.UpdatedAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 一定要搭配 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<Category> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<CategoryAdapterModel>>(records);
        Logger.LogDebug("Loaded categories successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<CategoryAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading category by id. CategoryId={CategoryId}", id);

        Category? item = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("Category not found. CategoryId={CategoryId}", id);
            return new CategoryAdapterModel();
        }

        if (!IsVisible(item, await accessScope.GetAsync()))
        {
            Logger.LogWarning("Category access denied by team scope. CategoryId={CategoryId}", id);
            return new CategoryAdapterModel();
        }

        return Mapper.Map<CategoryAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(CategoryAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating category. Name={CategoryName}", paraObject.Name);

        try
        {
            Category itemParameter = Mapper.Map<Category>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Category.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            Logger.LogInformation("Category created successfully. CategoryId={CategoryId}, Name={CategoryName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create category. Name={CategoryName}", paraObject.Name);

            // 前置檢查與寫入不在同一個交易裡，唯一索引是最後一道防線；
            // 命中時要給明確訊息，不要被泛用的「新增分類失敗。」蓋掉。
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            return VerifyRecordResultFactory.Build(false, "新增分類失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(CategoryAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);

        try
        {
            Category? item = await context.Category
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Category update rejected because record was not found. CategoryId={CategoryId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的分類資料。");
            }

            Category itemData = Mapper.Map<Category>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();

            Logger.LogInformation("Category updated successfully. CategoryId={CategoryId}, Name={CategoryName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);

            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            return VerifyRecordResultFactory.Build(false, "修改分類失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting category. CategoryId={CategoryId}", id);

        try
        {
            Category? item = await context.Category
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Category deletion rejected because record was not found. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的分類資料。");
            }

            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();

            Logger.LogInformation("Category deleted successfully. CategoryId={CategoryId}, Name={CategoryName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete category. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除分類失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(CategoryAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-create validation for category. Name={CategoryName}", paraObject.Name);

        var name = NameNormalizer.Normalize(paraObject.Name);
        var searchItem = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());

        if (searchItem != null)
        {
            Logger.LogInformation("Pre-create validation failed because category name already exists. Name={CategoryName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "分類名稱已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(CategoryAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);

        var searchItem = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogInformation("Pre-update validation failed because category was not found. CategoryId={CategoryId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的分類資料不存在。");
        }

        var name = NameNormalizer.Normalize(paraObject.Name);
        searchItem = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogInformation("Pre-update validation failed because category name already exists. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "分類名稱已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(CategoryAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 取得目前使用者可使用、且啟用中的分類名稱（依名稱排序），供其他頁面下拉選取使用。
    /// 可見性規則見 <see cref="ApplyTeamVisibility"/>。
    /// </summary>
    public async Task<List<string>> GetAllEnabledNamesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<Category> dataSource = context.Category
            .AsNoTracking()
            .Where(x => x.IsEnabled);

        dataSource = ApplyTeamVisibility(dataSource, await accessScope.GetAsync());

        return await dataSource
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync();
    }
}
