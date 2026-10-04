using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using System.Text.Json;

namespace MyProject.Business.Services.DataAccess;

public class RoleViewService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly RolePermissionService rolePermissionService;
    private readonly IRbacWriteService rbacWriteService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public IMapper Mapper { get; }
    public ILogger<RoleViewService> Logger { get; }

    public RoleViewService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<RoleViewService> logger,
        RolePermissionService rolePermissionService,
        IRbacWriteService rbacWriteService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
        this.rbacWriteService = rbacWriteService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
    }

    /// <summary>取得目前操作者作為稽核 actor；未登入（Id==0）時回 null。</summary>
    private (int? ActorUserId, string? ActorAccount) ResolveActor()
    {
        var user = currentUserService.CurrentUser;
        return user.Id > 0 ? (user.Id, user.Account) : (null, null);
    }

    private List<string> ParsePermissionKeys(string? tabViewJson)
    {
        if (string.IsNullOrWhiteSpace(tabViewJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tabViewJson) ?? [];
        }
        catch (Exception ex)
        {
            // 解析失敗等於「這個角色沒有任何權限」，必須留下紀錄（LOG-08）。
            Logger.LogWarning(ex, "Failed to parse role permission JSON; treating it as no permissions. JsonLength={JsonLength}", tabViewJson.Length);
            return [];
        }
    }

    public async Task<DataRequestResult<RoleViewAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading role views. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            string.IsNullOrWhiteSpace(dataRequest.Search) == false,
            dataRequest.Search?.Length ?? 0,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<RoleViewAdapterModel> result = new();
        IQueryable<RoleView> dataSource = context.RoleView.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x => x.Name.Contains(dataRequest.Search));
        }

        IOrderedQueryable<RoleView>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(RoleViewAdapterModel.Name))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(RoleViewAdapterModel.CreateAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreateAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(RoleViewAdapterModel.UpdateAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdateAt).ThenBy(x => x.Id)
                        : null;
            }
        }

        // Skip/Take 一定要搭配 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.UpdateAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<RoleView> records = await dataSource.ToListAsync();
        List<RoleViewAdapterModel> adapterModelObjects = Mapper.Map<List<RoleViewAdapterModel>>(records);
        foreach (var adapterModelItem in adapterModelObjects)
        {
            await OtherDependencyData(adapterModelItem);
        }

        result.Result = adapterModelObjects;
        Logger.LogDebug("Loaded role views successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<RoleViewAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading role view by id. RoleViewId={RoleViewId}", id);

        RoleView? item = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("Role view not found. RoleViewId={RoleViewId}", id);
            return new RoleViewAdapterModel();
        }

        RoleViewAdapterModel result = Mapper.Map<RoleViewAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }

    public async Task<VerifyRecordResult> AddAsync(RoleViewAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating role view. Name={RoleName}", paraObject.Name);

        try
        {
            RoleView itemParameter = Mapper.Map<RoleView>(paraObject);
            itemParameter.ConcurrencyStamp = ConcurrencyStampHelper.New();
            itemParameter.TabViewJson = rolePermissionService.GetPermissionInputToJson(paraObject.RolePermission);

            await context.RoleView.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var permissionKeys = ParsePermissionKeys(itemParameter.TabViewJson);
            await rbacWriteService.SyncRolePermissionsAsync(itemParameter.Id, permissionKeys);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.Role.Create, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: itemParameter.Id.ToString(),
                detail: $"name={itemParameter.Name}; permissionKeyCount={permissionKeys.Count}; requireTwoFactor={itemParameter.RequireTwoFactor}");

            Logger.LogInformation("Role view created successfully. RoleViewId={RoleViewId}, Name={RoleName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create role view. Name={RoleName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "新增角色失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(RoleViewAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);

        try
        {
            RoleView itemData = Mapper.Map<RoleView>(paraObject);
            itemData.TabViewJson = rolePermissionService.GetPermissionInputToJson(paraObject.RolePermission);

            RoleView? item = await context.RoleView
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Role view update rejected because record was not found. RoleViewId={RoleViewId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的角色資料。");
            }

            var entry = context.Entry(itemData);
            entry.State = EntityState.Modified;
            ConcurrencyStampHelper.Apply(entry, paraObject.ConcurrencyStamp);
            SoftDeleteHelper.ProtectFlags(entry);
            await context.SaveChangesAsync();

            var permissionKeys = ParsePermissionKeys(itemData.TabViewJson);
            await rbacWriteService.SyncRolePermissionsAsync(itemData.Id, permissionKeys);

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.Role.Update, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: itemData.Id.ToString(),
                detail: $"name={itemData.Name}; permissionKeyCount={permissionKeys.Count}; requireTwoFactor={itemData.RequireTwoFactor}");

            Logger.LogInformation("Role view updated successfully. RoleViewId={RoleViewId}, Name={RoleName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 別人在這段期間先存過或刪除了這筆：使用者情境而非系統錯誤（LOG-11），記 Information、不帶例外物件。
            Logger.LogInformation("Role view update rejected by concurrency conflict. RoleViewId={RoleViewId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "修改角色失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting role view. RoleViewId={RoleViewId}", id);

        try
        {
            RoleView? item = await context.RoleView.FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Role view deletion rejected because record was not found. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的角色資料。");
            }

            // 預設角色是新帳號與 support 的角色；種子資料找不到它時會以「全部權限」重建，刪掉反而危險。
            if (item.Name == MagicObjectHelper.預設角色)
            {
                Logger.LogInformation("Role view deletion rejected for the default role. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, $"「{MagicObjectHelper.預設角色}」是新帳號與 support 使用的角色，不可刪除。");
            }

            // 仍是某些未刪除使用者（含停用者）的主要角色就不能刪：刪了之後那些人每次換頁都會被登出。
            if (await BuildPrimaryRoleInUseMessageAsync(context.MyUser, item.Name, id, "無法刪除。請先到使用者管理把他們的主要角色改成其他角色，再刪除。") is { } inUse)
            {
                Logger.LogInformation("Role view deletion rejected because it is still a primary role. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, inUse);
            }

            var (actorUserId, actorAccount) = ResolveActor();
            SoftDeleteHelper.MarkDeleted(item, actorAccount);
            await context.SaveChangesAsync();

            // 存檔後再查一次：上面的檢查與「使用者存檔時驗證角色有效」各自在不同的 DbContext，
            // 若有人剛好在這段期間被設成這個主要角色，就把角色還原並擋下，避免他被登出迴圈卡住。
            if (await BuildPrimaryRoleInUseMessageAsync(context.MyUser, item.Name, id, "無法刪除。請先到使用者管理把他們的主要角色改成其他角色，再刪除。") is { } raced)
            {
                SoftDeleteHelper.Restore(item);
                await context.SaveChangesAsync();
                Logger.LogInformation("Role view deletion rolled back because a user was assigned it meanwhile. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, raced);
            }

            await auditLogService.WriteAsync(
                AuditActions.Role.Delete, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: id.ToString(),
                detail: $"name={item.Name}");

            Logger.LogInformation("Role view deleted successfully. RoleViewId={RoleViewId}, Name={RoleName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Role view deletion rejected by concurrency conflict. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete role view. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除角色失敗。", ex);
        }
    }

    /// <summary>
    /// 以這個角色為主要角色的使用者還有人時，回傳「人數 + 前 5 個帳號 + <paramref name="suffix"/>」的訊息；沒有人則回 null。
    /// <paramref name="users"/> 決定範圍：一般的 <c>context.MyUser</c> 只算未刪除的，加上 <c>IgnoreQueryFilters</c> 連已刪除的也算。
    /// </summary>
    private static async Task<string?> BuildPrimaryRoleInUseMessageAsync(IQueryable<MyUser> users, string roleName, int roleId, string suffix)
    {
        var holders = users.Where(u => u.RoleViewId == roleId);
        var count = await holders.CountAsync();
        if (count == 0)
        {
            return null;
        }

        var accounts = await holders
            .OrderBy(u => u.Account)
            .Take(5)
            .Select(u => u.IsDeleted ? u.Account + "（已刪除）" : u.Account)
            .ToListAsync();
        var more = count > accounts.Count ? " 等" : string.Empty;
        return $"還有 {count} 位使用者以「{roleName}」為主要角色（{string.Join("、", accounts)}{more}），{suffix}";
    }

    /// <summary>已刪除的角色（「顯示已刪除」清單），固定依刪除時間由新到舊。</summary>
    public async Task<DataRequestResult<RoleViewAdapterModel>> GetDeletedAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<RoleView> dataSource = context.RoleView
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Where(x => x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x => x.Name.Contains(dataRequest.Search));
        }

        dataSource = dataSource.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Id);

        var result = new DataRequestResult<RoleViewAdapterModel> { Count = await dataSource.CountAsync() };
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        var items = Mapper.Map<List<RoleViewAdapterModel>>(await dataSource.ToListAsync());
        foreach (var item in items)
        {
            await OtherDependencyData(item);
        }

        result.Result = items;
        return result;
    }

    public async Task<VerifyRecordResult> RestoreAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Restoring role view. RoleViewId={RoleViewId}", id);

        try
        {
            RoleView? item = await context.RoleView
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Role view restore rejected because deleted record was not found. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要還原的角色（可能已被還原或永久刪除）。");
            }

            // 刪除期間可能有人建立了同名角色；比對規則與新增時相同（完全比對）。
            if (await context.RoleView.AnyAsync(x => x.Id != id && x.Name == item.Name))
            {
                Logger.LogInformation("Role view restore rejected because an active role has the same name. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, $"已有同名的角色「{item.Name}」，無法還原。請先將現有的同名角色改名後再還原。");
            }

            SoftDeleteHelper.Restore(item);
            await context.SaveChangesAsync();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.Role.Restore, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: id.ToString(),
                detail: $"name={item.Name}");

            Logger.LogInformation("Role view restored successfully. RoleViewId={RoleViewId}, Name={RoleName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Role view restore rejected by concurrency conflict. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to restore role view. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, "還原角色失敗。", ex);
        }
    }

    /// <summary>
    /// 永久刪除：只能對已刪除的角色執行，無法復原；權限對應與額外角色的關聯由資料庫 Cascade 一併刪除。
    /// 主要角色是 Restrict 外鍵，連已刪除的使用者都算 —— 先檢查並列出帳號，否則只會得到籠統的資料庫錯誤。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging role view. RoleViewId={RoleViewId}", id);

        try
        {
            RoleView? item = await context.RoleView
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Role view purge rejected because deleted record was not found. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要永久刪除的角色（只能永久刪除已刪除的資料）。");
            }

            var allUsers = context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]);
            if (await BuildPrimaryRoleInUseMessageAsync(allUsers, item.Name, id, "無法永久刪除。已刪除的使用者請先永久刪除，或還原角色後替他們改用其他角色。") is { } inUse)
            {
                Logger.LogInformation("Role view purge rejected because users still reference it as primary role. RoleViewId={RoleViewId}", id);
                return VerifyRecordResultFactory.Build(false, inUse);
            }

            context.RoleView.Remove(item);
            await context.SaveChangesAsync();

            var (actorUserId, actorAccount) = ResolveActor();
            await auditLogService.WriteAsync(
                AuditActions.Role.Purge, success: true, actorUserId: actorUserId, actorAccount: actorAccount,
                targetType: nameof(RoleView), targetId: id.ToString(),
                detail: $"name={item.Name}");

            Logger.LogInformation("Role view purged successfully. RoleViewId={RoleViewId}, Name={RoleName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Role view purge rejected by concurrency conflict. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, "這個角色已被其他人還原或變更，沒有執行永久刪除。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge role view. RoleViewId={RoleViewId}", id);
            return VerifyRecordResultFactory.Build(false, "永久刪除角色失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(RoleViewAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-create validation for role view. Name={RoleName}", paraObject.Name);

        var searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == paraObject.Name);

        if (searchItem != null)
        {
            Logger.LogInformation("Pre-create validation failed because role name already exists. Name={RoleName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "角色名稱已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(RoleViewAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);

        var searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogInformation("Pre-update validation failed because role view was not found. RoleViewId={RoleViewId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的角色資料不存在。");
        }

        searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == paraObject.Name && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogInformation("Pre-update validation failed because role name already exists. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "角色名稱已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(RoleViewAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for role view. RoleViewId={RoleViewId}, Name={RoleName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private Task OtherDependencyData(RoleViewAdapterModel data)
    {
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();
        List<string> permissions;

        try
        {
            permissions = JsonSerializer.Deserialize<List<string>>(data.TabViewJson) ?? [];
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to deserialize role permissions. RoleViewId={RoleViewId}", data.Id);
            permissions = [];
        }

        rolePermissionService.SetPermissionInput(rolePermission, permissions);
        data.RolePermission = rolePermission;
        return Task.CompletedTask;
    }

    public async Task<RoleViewAdapterModel> Get預設新建帳號角色Async()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading default role view for new user creation.");

        RoleView? item = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色);

        if (item is null)
        {
            Logger.LogWarning("Default role view was not found. RoleName={RoleName}", MagicObjectHelper.預設角色);
            return new RoleViewAdapterModel();
        }

        RoleViewAdapterModel result = Mapper.Map<RoleViewAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }
}
