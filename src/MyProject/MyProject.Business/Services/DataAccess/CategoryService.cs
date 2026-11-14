using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Mappers;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

public class CategoryService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public ILogger<CategoryService> Logger { get; }

    public CategoryService(
        IDbContextFactory<BackendDBContext> contextFactory,
        ILogger<CategoryService> logger,
        IRecordAccessScopeProvider accessScope,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService)
    {
        this.contextFactory = contextFactory;
        Logger = logger;
        this.accessScope = accessScope;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
    }

    /// <summary>
    /// 寫一筆稽核（LOG-14）。操作者取自 <see cref="CurrentUserService"/>：Web API 的 <c>ApiActorMiddleware</c> 依存取 Token 填入的使用者。AuditLogService 失敗不拋出。
    /// </summary>
    private Task WriteAuditAsync(string action, int targetId, string detail, string? content = null)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: "Category",
            targetId: targetId.ToString(),
            detail: detail,
            content: content);
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
        => RecordTeamScope.ApplyCategory(source, scope);

    /// <summary>
    /// 單筆分類的可見性判斷，規則與 <see cref="ApplyTeamVisibility"/> 一致。
    /// </summary>
    private static bool IsVisible(Category item, RecordAccessScope scope)
        => RecordTeamScope.CanAccessCategory(item.Teams, scope);

    /// <param name="isEnabled">只看啟用（true）或停用（false）的；null 不篩選（Web API 的 <c>IsEnabled</c> 條件，0.10.2 起）。</param>
    public async Task<DataRequestResult<CategoryAdapterModel>> GetAsync(DataRequest dataRequest, bool? isEnabled = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading categories. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            string.IsNullOrWhiteSpace(dataRequest.Search) == false,
            dataRequest.Search?.Length ?? 0,
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

        if (isEnabled is { } enabledOnly)
        {
            dataSource = dataSource.Where(x => x.IsEnabled == enabledOnly);
        }

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
            else if (dataRequest.SortField == nameof(CategoryAdapterModel.CreatedAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
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
        result.Result = CategoryMapper.ToModels(records);
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

        return CategoryMapper.ToModel(item);
    }

    public async Task<VerifyRecordResult> AddAsync(CategoryAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating category. Name={CategoryName}", paraObject.Name);

        try
        {
            // 非管理員只能指定自己範圍內的團隊（0.9.105 起，與專案相同）。
            if (RecordTeamScope.CheckAssignment(null, TagStringHelper.ToStored(paraObject.Teams), await accessScope.GetAsync()) is { } teamError)
            {
                Logger.LogInformation("Category create rejected by team assignment rule. Name={CategoryName}", paraObject.Name);
                return VerifyRecordResultFactory.Invalid(teamError);
            }

            Category itemParameter = CategoryMapper.ToEntity(paraObject);
            itemParameter.ConcurrencyStamp = ConcurrencyStampHelper.New();
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Category.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            // 回填新的 Id 與版本號（0.10.2 起）：Web API 新增後要回傳這一筆。
            paraObject.Id = itemParameter.Id;
            paraObject.ConcurrencyStamp = itemParameter.ConcurrencyStamp;
            Logger.LogInformation("Category created successfully. CategoryId={CategoryId}, Name={CategoryName}", itemParameter.Id, itemParameter.Name);
            await WriteAuditAsync(AuditActions.Category.Create, itemParameter.Id, $"name={itemParameter.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // 前置檢查與寫入不在同一個交易裡，唯一索引是最後一道防線；
            // 命中時要給明確訊息，不要被泛用的「新增分類失敗。」蓋掉。
            // 名稱重複是使用者錯誤（LOG-11）：記 Information、不帶例外物件，不進系統例外紀錄。
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                Logger.LogInformation("Category create rejected by unique constraint. Name={CategoryName}", paraObject.Name);
                return VerifyRecordResultFactory.Conflict(conflictMessage, ex);
            }

            Logger.LogError(ex, "Failed to create category. Name={CategoryName}", paraObject.Name);
            return VerifyRecordResultFactory.Error("新增分類失敗。", ex);
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
                return VerifyRecordResultFactory.NotFound("找不到要修改的分類資料。");
            }

            // 0.9.105 之前修改不檢查團隊範圍（刪除、還原早已檢查）。
            var scope = await accessScope.GetAsync();
            if (!IsVisible(item, scope))
            {
                Logger.LogWarning("Category update denied by team scope. CategoryId={CategoryId}", paraObject.Id);
                return VerifyRecordResultFactory.NotFound("這筆分類不在你的團隊範圍內，無法修改。");
            }

            if (RecordTeamScope.CheckAssignment(item.Teams, TagStringHelper.ToStored(paraObject.Teams), scope) is { } teamError)
            {
                Logger.LogInformation("Category update rejected by team assignment rule. CategoryId={CategoryId}", paraObject.Id);
                return VerifyRecordResultFactory.Invalid(teamError);
            }

            Category itemData = CategoryMapper.ToEntity(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            // 修改前後差異（0.9.131 起）寫進稽核的詳細內容檔；item 是 AsNoTracking 讀出的修改前資料。
            var diff = AuditDiff.Compare(item, itemData).Render($"分類 #{itemData.Id}（{item.Name}）修改");

            var entry = context.Entry(itemData);
            entry.State = EntityState.Modified;
            ConcurrencyStampHelper.Apply(entry, paraObject.ConcurrencyStamp);
            SoftDeleteHelper.ProtectFlags(entry);
            await context.SaveChangesAsync();

            Logger.LogInformation("Category updated successfully. CategoryId={CategoryId}, Name={CategoryName}", itemData.Id, itemData.Name);
            await WriteAuditAsync(AuditActions.Category.Update, itemData.Id, $"name={itemData.Name}", diff);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 別人在這段期間先存過或刪除了這筆：使用者情境而非系統錯誤（LOG-11），記 Information、不帶例外物件。
            Logger.LogInformation("Category update rejected by concurrency conflict. CategoryId={CategoryId}", paraObject.Id);
            return VerifyRecordResultFactory.Conflict(ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                Logger.LogInformation("Category update rejected by unique constraint. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
                return VerifyRecordResultFactory.Conflict(conflictMessage, ex);
            }

            Logger.LogError(ex, "Failed to update category. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Error("修改分類失敗。", ex);
        }
    }

    /// <summary>
    /// 清單上點狀態標籤切換啟用／停用（0.9.120 起）：只改 <c>IsEnabled</c>，不走整筆覆寫的 <see cref="UpdateAsync"/>。
    /// 參數是目標值（重複點不會反轉）；已經是目標值時回成功、不寫稽核。團隊範圍與修改相同。
    /// 這是使用者的編輯動作，所以換版本號：別人開著這筆的修改窗存檔時會得到並行衝突，而不是把狀態蓋回去。
    /// </summary>
    public async Task<VerifyRecordResult> SetEnabledAsync(int id, bool enabled)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        try
        {
            Category? item = await context.Category.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                Logger.LogInformation("Category status change rejected because record was not found. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("找不到這個分類，可能已被刪除。");
            }

            if (!IsVisible(item, await accessScope.GetAsync()))
            {
                Logger.LogWarning("Category status change denied by team scope. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("這筆分類不在你的團隊範圍內，無法修改。");
            }

            var rows = item.IsEnabled == enabled ? 0 : await context.Category
                .Where(x => x.Id == id && x.IsEnabled != enabled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsEnabled, enabled)
                    .SetProperty(x => x.UpdatedAt, DateTime.Now)
                    .SetProperty(x => x.ConcurrencyStamp, ConcurrencyStampHelper.New()));
            if (rows == 0)
            {
                return VerifyRecordResultFactory.Build(true);
            }

            Logger.LogInformation("Category status changed. CategoryId={CategoryId}, Enabled={Enabled}", id, enabled);
            await WriteAuditAsync(enabled ? AuditActions.Category.Enable : AuditActions.Category.Disable, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change category status. CategoryId={CategoryId}, Enabled={Enabled}", id, enabled);
            return VerifyRecordResultFactory.Error("切換分類狀態失敗。", ex);
        }
    }

    /// <summary>
    /// 刪除 = 軟刪除（0.9.94 起）：資料仍在，可在「顯示已刪除」中還原；永久刪除見 <see cref="PurgeAsync"/>。
    /// </summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting category. CategoryId={CategoryId}", id);

        try
        {
            Category? item = await context.Category.FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Category deletion rejected because record was not found. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("找不到要刪除的分類資料。");
            }

            // 0.9.93 之前刪除完全不檢查團隊範圍；畫面上看不到不代表伺服器端不該擋。
            if (!IsVisible(item, await accessScope.GetAsync()))
            {
                Logger.LogWarning("Category deletion denied by team scope. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("這筆分類不在你的團隊範圍內，無法刪除。");
            }

            SoftDeleteHelper.MarkDeleted(item, currentUserService.CurrentUser.Id > 0 ? currentUserService.CurrentUser.Account : null);
            await context.SaveChangesAsync();

            Logger.LogInformation("Category deleted successfully. CategoryId={CategoryId}, Name={CategoryName}", id, item.Name);
            await WriteAuditAsync(AuditActions.Category.Delete, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Category deletion rejected by concurrency conflict. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Conflict(ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete category. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Error("刪除分類失敗。", ex);
        }
    }

    /// <summary>已刪除的分類（「顯示已刪除」清單）。沿用搜尋與團隊可見性，固定依刪除時間由新到舊。</summary>
    public async Task<DataRequestResult<CategoryAdapterModel>> GetDeletedAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<Category> dataSource = context.Category
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Where(x => x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        dataSource = ApplyTeamVisibility(dataSource, await accessScope.GetAsync())
            .OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Id);

        var result = new DataRequestResult<CategoryAdapterModel> { Count = await dataSource.CountAsync() };
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        result.Result = CategoryMapper.ToModels(await dataSource.ToListAsync());
        return result;
    }

    public async Task<VerifyRecordResult> RestoreAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Restoring category. CategoryId={CategoryId}", id);

        try
        {
            Category? item = await context.Category
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Category restore rejected because deleted record was not found. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("找不到要還原的分類（可能已被還原或永久刪除）。");
            }

            if (!IsVisible(item, await accessScope.GetAsync()))
            {
                Logger.LogWarning("Category restore denied by team scope. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("這筆分類不在你的團隊範圍內，無法還原。");
            }

            // 刪除期間可能有人建立了同名的分類（部分唯一索引允許），規則與新增時相同：去空白後不分大小寫。
            var name = item.Name.ToLower();
            if (await context.Category.AnyAsync(x => x.Id != id && x.Name.ToLower() == name))
            {
                Logger.LogInformation("Category restore rejected because an active category has the same name. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.Conflict($"已有同名的分類「{item.Name}」，無法還原。請先將現有的同名分類改名後再還原。");
            }

            SoftDeleteHelper.Restore(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("Category restored successfully. CategoryId={CategoryId}, Name={CategoryName}", id, item.Name);
            await WriteAuditAsync(AuditActions.Category.Restore, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Category restore rejected by concurrency conflict. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Conflict(ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                Logger.LogInformation("Category restore rejected by unique constraint. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.Conflict(conflictMessage, ex);
            }

            Logger.LogError(ex, "Failed to restore category. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Error("還原分類失敗。", ex);
        }
    }

    /// <summary>永久刪除：只能對已刪除的分類執行，無法復原。</summary>
    public async Task<VerifyRecordResult> PurgeAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging category. CategoryId={CategoryId}", id);

        try
        {
            Category? item = await context.Category
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Category purge rejected because deleted record was not found. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("找不到要永久刪除的分類（只能永久刪除已刪除的資料）。");
            }

            if (!IsVisible(item, await accessScope.GetAsync()))
            {
                Logger.LogWarning("Category purge denied by team scope. CategoryId={CategoryId}", id);
                return VerifyRecordResultFactory.NotFound("這筆分類不在你的團隊範圍內，無法永久刪除。");
            }

            context.Category.Remove(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("Category purged successfully. CategoryId={CategoryId}, Name={CategoryName}", id, item.Name);
            await WriteAuditAsync(AuditActions.Category.Purge, id, $"name={item.Name}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Category purge rejected by concurrency conflict. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Conflict("這筆分類已被其他人還原或變更，沒有執行永久刪除。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge category. CategoryId={CategoryId}", id);
            return VerifyRecordResultFactory.Error("永久刪除分類失敗。", ex);
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
            return VerifyRecordResultFactory.Conflict("分類名稱已存在，無法新增。");
        }

        // 這次新選的適用團隊已不存在或已停用（0.10.15 起，見 SelectedOptionGuard）。
        if (SelectedOptionGuard.FindUnavailable(paraObject.Teams, null, await SelectedOptionGuard.EnabledTeamNamesAsync(context)) is { } team)
        {
            Logger.LogInformation("Pre-create validation failed because a selected team is no longer available. Name={CategoryName}", paraObject.Name);
            return VerifyRecordResultFactory.Invalid(SelectedOptionGuard.Message("團隊", team));
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
            return VerifyRecordResultFactory.NotFound("要修改的分類資料不存在。");
        }

        var name = NameNormalizer.Normalize(paraObject.Name);
        var sameName = await context.Category
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (sameName != null)
        {
            Logger.LogInformation("Pre-update validation failed because category name already exists. CategoryId={CategoryId}, Name={CategoryName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Conflict("分類名稱已存在，無法修改。");
        }

        // 這次新選的適用團隊已不存在或已停用（原本就有的不擋；0.10.15 起，見 SelectedOptionGuard）。排在重名之後：欄位的即時名稱檢查只認 409。
        if (SelectedOptionGuard.FindUnavailable(paraObject.Teams, TagStringHelper.ToList(searchItem.Teams), await SelectedOptionGuard.EnabledTeamNamesAsync(context)) is { } team)
        {
            Logger.LogInformation("Pre-update validation failed because a selected team is no longer available. CategoryId={CategoryId}", paraObject.Id);
            return VerifyRecordResultFactory.Invalid(SelectedOptionGuard.Message("團隊", team));
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
