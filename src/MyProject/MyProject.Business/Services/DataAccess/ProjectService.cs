using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Mappers;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

public class ProjectService
{
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly string projectFileRootPath;
    private readonly ProjectFileStore fileStore;
    private readonly IReadOnlyCollection<string> allowedUploadExtensions;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public ILogger<ProjectService> Logger { get; }

    public ProjectService(
        IDbContextFactory<BackendDBContext> contextFactory,
        ILogger<ProjectService> logger,
        IOptions<SystemSettings> systemSettings,
        IRecordAccessScopeProvider accessScope,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ProjectFileStore fileStore)
    {
        this.fileStore = fileStore;
        this.contextFactory = contextFactory;
        Logger = logger;
        this.accessScope = accessScope;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        projectFileRootPath = systemSettings.Value.ExternalFileSystem.ProjectFilePath;
        allowedUploadExtensions = systemSettings.Value.Upload.AllowedExtensions;
    }

    /// <param name="filter">Web API 原本就有的篩選（負責人、狀態、日期區間等，0.10.3 起）；畫面不帶。</param>
    public async Task<DataRequestResult<ProjectAdapterModel>> GetAsync(DataRequest dataRequest, ProjectListFilter? filter = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading projects. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            string.IsNullOrWhiteSpace(dataRequest.Search) == false,
            dataRequest.Search?.Length ?? 0,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<ProjectAdapterModel> result = new();
        IQueryable<Project> dataSource = context.Project.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                x.Owner.Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Teams, dataRequest.TeamFilters));
        }

        dataSource = ApplyFilter(dataSource, filter);

        var scope = await accessScope.GetAsync();
        dataSource = RecordTeamScope.Apply(dataSource, x => x.Teams, scope);

        IOrderedQueryable<Project>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(ProjectAdapterModel.Title))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.StartDate))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.EndDate))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.EndDate).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Status))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Priority))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Priority).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CompletionPercentage))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CompletionPercentage).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CompletionPercentage).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Owner))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Owner).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Owner).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CreatedAt))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.UpdatedAt))
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

        var records = await dataSource.ToListAsync();
        result.Result = ProjectMapper.ToModels(records);
        Logger.LogDebug("Loaded projects successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<ProjectAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading project by id. ProjectId={ProjectId}", id);

        Project? item = await context.Project
            .AsNoTracking()
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("Project not found. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        var scope = await accessScope.GetAsync();
        if (!RecordTeamScope.CanAccess(item.Teams, scope))
        {
            Logger.LogWarning("Project access denied by team scope. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        return ProjectMapper.ToModel(item);
    }

    /// <summary>
    /// 寫一筆稽核（LOG-14）。操作者取自 <see cref="CurrentUserService"/>：畫面由換頁檢查填入；
    /// Web API（0.10.3 起也走本服務）由 ApiActorMiddleware 依存取 Token 填入。AuditLogService 失敗不拋出。
    /// </summary>
    private Task WriteAuditAsync(string action, int targetId, string detail, string? content = null)
    {
        var user = currentUserService.CurrentUser;
        return auditLogService.WriteAsync(
            action,
            success: true,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: "Project",
            targetId: targetId.ToString(),
            detail: detail,
            content: content);
    }

    /// <summary>
    /// 新增（可帶附件）。資料與附件在同一個交易裡（0.10.3 起，WASM 遷移 M4 審查）：附件存失敗就整筆不留、剛寫入的實體檔一併刪掉，
    /// 使用者照原本的視窗再按一次儲存即可；不會出現「資料存了、附件沒存、稽核也沒寫」。
    /// </summary>
    public async Task<VerifyRecordResult> AddAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating project. Title={Title}, Owner={Owner}", paraObject.Title, paraObject.Owner);
        List<string> createdFullPaths = [];
        var committed = false;

        try
        {
            // 非管理員只能指定自己範圍內的團隊（0.9.105 起），否則建出來的專案自己也看不到。
            if (RecordTeamScope.CheckAssignment(null, TagStringHelper.ToStored(paraObject.Teams), await accessScope.GetAsync()) is { } teamError)
            {
                Logger.LogInformation("Project create rejected by team assignment rule. Title={Title}", paraObject.Title);
                return VerifyRecordResultFactory.Invalid(teamError);
            }

            Project itemParameter = ProjectMapper.ToEntity(paraObject);
            itemParameter.Files = [];
            itemParameter.ConcurrencyStamp = ConcurrencyStampHelper.New();

            // 存的就是比對重名時用的那個字串（0.10.3 起）：前後的空白、Tab、全形空白不會讓兩個看起來一樣的名稱並存。
            itemParameter.Title = NameNormalizer.Normalize(itemParameter.Title);

            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.Project.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(context, itemParameter, uploadFiles, createdFullPaths);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            await transaction.CommitAsync();
            committed = true;

            // 回填給呼叫端（0.10.3 起 Web API 以它回傳新資料）。
            paraObject.Id = itemParameter.Id;
            paraObject.ConcurrencyStamp = itemParameter.ConcurrencyStamp;

            Logger.LogInformation("Project created successfully. ProjectId={ProjectId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            await WriteAuditAsync(AuditActions.Project.Create, itemParameter.Id, $"title={itemParameter.Title}");
            await WriteFileAuditAsync(AuditActions.Project.FileUpload, itemParameter.Id, uploadFiles?.Count() ?? 0);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create project. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Error("新增專案失敗。", ex);
        }
        finally
        {
            // 交易沒提交（失敗或例外）：資料已回滾，這次寫入的實體檔也不能留下。
            if (!committed)
            {
                DeleteCreatedFiles(createdFullPaths);
            }
        }
    }

    /// <summary>
    /// 修改（可新增、移除附件）。資料與附件在同一個交易裡（0.10.3 起）：任何一步失敗就整筆回到修改前（版本號也不變），
    /// 新寫入的實體檔刪掉、要移除的實體檔留著，使用者照原本的視窗再按一次儲存即可；要移除的實體檔在提交之後才刪。
    /// </summary>
    public async Task<VerifyRecordResult> UpdateAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null,
        IEnumerable<int>? removedFileIds = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
        List<string> createdFullPaths = [];
        var committed = false;

        try
        {
            Project? currentItem = await context.Project
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Project update rejected because record was not found. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.NotFound("找不到要修改的專案資料。");
            }

            // 0.9.105 之前修改不檢查團隊範圍：看不到的專案只要知道 Id 就能改（刪除、還原早已檢查）。
            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(currentItem.Teams, scope))
            {
                Logger.LogWarning("Project update denied by team scope. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.NotFound("這筆專案不在你的團隊範圍內，無法修改。");
            }

            if (RecordTeamScope.CheckAssignment(currentItem.Teams, TagStringHelper.ToStored(paraObject.Teams), scope) is { } teamError)
            {
                Logger.LogInformation("Project update rejected by team assignment rule. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Invalid(teamError);
            }

            // 修改前的快照（0.9.131 起，給稽核差異用）：要在逐欄指定之前取，之後 currentItem 就是新值了。
            var before = (Project)context.Entry(currentItem).CurrentValues.ToObject();

            currentItem.Title = NameNormalizer.Normalize(paraObject.Title);
            currentItem.Description = paraObject.Description;
            currentItem.StartDate = paraObject.StartDate;
            currentItem.EndDate = paraObject.EndDate;
            currentItem.Status = paraObject.Status;
            currentItem.Priority = paraObject.Priority;
            currentItem.CompletionPercentage = paraObject.CompletionPercentage;
            currentItem.Owner = paraObject.Owner;
            currentItem.Categories = TagStringHelper.ToStored(paraObject.Categories);
            currentItem.Teams = TagStringHelper.ToStored(paraObject.Teams);
            currentItem.UpdatedAt = paraObject.UpdatedAt;
            var diff = AuditDiff.Compare(before, currentItem).Render($"專案 #{currentItem.Id}（{before.Title}）修改");

            // 先載入再複製欄位的寫法：一定要以使用者開窗時的版本號比對，否則永遠偵測不到衝突。
            // 衝突時第一次 SaveChanges 就會失敗，附件的新增與刪除都不會執行。
            ConcurrencyStampHelper.Apply(context.Entry(currentItem), paraObject.ConcurrencyStamp);
            SoftDeleteHelper.ProtectFlags(context.Entry(currentItem));
            await using var transaction = await context.Database.BeginTransactionAsync();
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(context, currentItem, uploadFiles, createdFullPaths);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            var removedRelativePaths = await RemoveProjectFilesAsync(context, currentItem, removedFileIds);
            await transaction.CommitAsync();
            committed = true;

            // ⚠️ 先提交資料庫、成功後才刪實體檔（0.9.97 起的原則）：提交失敗時資料列還在，檔案也必須還在。
            // 刪檔經 ProjectFileStore（有根目錄檢查），刪不掉只記警告。
            foreach (var relativePath in removedRelativePaths)
            {
                fileStore.Delete(relativePath);
            }

            Logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", currentItem.Id, currentItem.Title);
            await WriteAuditAsync(AuditActions.Project.Update, currentItem.Id, $"title={currentItem.Title}", diff);
            await WriteFileAuditAsync(AuditActions.Project.FileUpload, currentItem.Id, uploadFiles?.Count() ?? 0);
            await WriteFileAuditAsync(AuditActions.Project.FileDelete, currentItem.Id, removedFileIds?.Count() ?? 0);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // 別人在這段期間先存過或刪除了這筆：使用者情境而非系統錯誤（LOG-11），記 Information、不帶例外物件。
            Logger.LogInformation("Project update rejected by concurrency conflict. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Conflict(ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Error("修改專案失敗。", ex);
        }
        finally
        {
            if (!committed)
            {
                DeleteCreatedFiles(createdFullPaths);
            }
        }
    }

    /// <summary>
    /// 刪除 = 軟刪除（0.9.94 起）：資料與附件實體檔都保留，可在「顯示已刪除」中還原；
    /// 附件在永久刪除（<see cref="PurgeAsync"/>）時才刪。軟刪除期間附件無法下載（見 GetFileDownloadAsync）。
    /// </summary>
    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project.FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Project deletion rejected because record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.NotFound("找不到要刪除的專案資料。");
            }

            // 0.9.93 之前刪除完全不檢查團隊範圍；畫面上看不到不代表伺服器端不該擋。
            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(item.Teams, scope))
            {
                Logger.LogWarning("Project deletion denied by team scope. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.NotFound("這筆專案不在你的團隊範圍內，無法刪除。");
            }

            SoftDeleteHelper.MarkDeleted(item, currentUserService.CurrentUser.Id > 0 ? currentUserService.CurrentUser.Account : null);
            await context.SaveChangesAsync();

            Logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            await WriteAuditAsync(AuditActions.Project.Delete, id, $"title={item.Title}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Project deletion rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Conflict(ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Error("刪除專案失敗。", ex);
        }
    }

    /// <summary>已刪除的專案（「顯示已刪除」清單）。沿用搜尋與團隊範圍，固定依刪除時間由新到舊。</summary>
    public async Task<DataRequestResult<ProjectAdapterModel>> GetDeletedAsync(DataRequest dataRequest, ProjectListFilter? filter = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        IQueryable<Project> dataSource = context.Project
            .IgnoreQueryFilters([ISoftDeletable.FilterName])
            .AsNoTracking()
            .Where(x => x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                x.Owner.Contains(search));
        }

        // 分類、團隊過濾（0.10.3 起）：API 公開了這兩個條件，已刪除的清單也要照做（畫面在「顯示已刪除」時不送）。
        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Teams, dataRequest.TeamFilters));
        }

        dataSource = ApplyFilter(dataSource, filter);

        var scope = await accessScope.GetAsync();
        dataSource = RecordTeamScope.Apply(dataSource, x => x.Teams, scope);

        dataSource = dataSource.OrderByDescending(x => x.DeletedAt).ThenByDescending(x => x.Id);

        var result = new DataRequestResult<ProjectAdapterModel> { Count = await dataSource.CountAsync() };
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        result.Result = ProjectMapper.ToModels(await dataSource.ToListAsync());
        return result;
    }

    public async Task<VerifyRecordResult> RestoreAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Restoring project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Project restore rejected because deleted record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.NotFound("找不到要還原的專案（可能已被還原或永久刪除）。");
            }

            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(item.Teams, scope))
            {
                Logger.LogWarning("Project restore denied by team scope. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.NotFound("這筆專案不在你的團隊範圍內，無法還原。");
            }

            // 0.10.3 起專案名稱不可重複：刪除期間有人新增了同名的專案，還原就會出現兩筆同名（與分類的還原相同處理）。
            if (await TitleTakenAsync(context, item.Title, item.Id))
            {
                Logger.LogInformation("Project restore rejected because an active project has the same title. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Conflict($"已有同名的專案「{item.Title}」，無法還原。請先將現有的同名專案改名後再還原。");
            }

            SoftDeleteHelper.Restore(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("Project restored successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            await WriteAuditAsync(AuditActions.Project.Restore, id, $"title={item.Title}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Project restore rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Conflict(ConcurrencyStampHelper.ConflictMessage, ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to restore project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Error("還原專案失敗。", ex);
        }
    }

    /// <summary>
    /// 永久刪除：只能對已刪除的專案執行，無法復原。
    /// ⚠️ 順序是「先提交資料庫、成功後才刪附件實體檔」：0.9.93 之前是先刪檔再寫資料庫，資料庫失敗時檔案已經不見了。
    /// 刪檔失敗只記警告（資料列已不存在，留下的是孤兒檔，不影響資料正確性）。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Purging project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project
                .IgnoreQueryFilters([ISoftDeletable.FilterName])
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id && x.IsDeleted);

            if (item == null)
            {
                Logger.LogWarning("Project purge rejected because deleted record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.NotFound("找不到要永久刪除的專案（只能永久刪除已刪除的資料）。");
            }

            var scope = await accessScope.GetAsync();
            if (!RecordTeamScope.CanAccess(item.Teams, scope))
            {
                Logger.LogWarning("Project purge denied by team scope. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.NotFound("這筆專案不在你的團隊範圍內，無法永久刪除。");
            }

            var files = item.Files.ToList();
            context.Project.Remove(item);
            await context.SaveChangesAsync();

            foreach (var file in files)
            {
                // 經 ProjectFileStore：路徑跑出附件根目錄時拒絕，刪不掉只記警告（0.9.97 起）。
                fileStore.Delete(file.RelativePath);
            }

            Logger.LogInformation("Project purged successfully. ProjectId={ProjectId}, Title={Title}, Files={FileCount}", id, item.Title, files.Count);
            await WriteAuditAsync(AuditActions.Project.Purge, id, $"title={item.Title}; files={files.Count}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Logger.LogInformation("Project purge rejected by concurrency conflict. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Conflict("這筆專案已被其他人還原或變更，沒有執行永久刪除。", ex);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Error("永久刪除專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-create validation for project. Title={Title}", paraObject.Title);
        if (TitleTooLong(paraObject.Title) is { } tooLong)
        {
            return tooLong;
        }

        var rules = await ValidateBusinessRulesAsync(paraObject, uploadFiles);
        if (!rules.Success)
        {
            return rules;
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        if (await UnavailableOptionAsync(context, paraObject, before: null) is { } optionError)
        {
            Logger.LogInformation("Pre-create validation failed because a selected option is no longer available. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Invalid(optionError);
        }

        if (await TitleTakenAsync(context, paraObject.Title, excludeId: null))
        {
            Logger.LogInformation("Pre-create validation failed because project title already exists. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Conflict("專案名稱已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        Project? searchItem = await context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogInformation("Pre-update validation failed because project was not found. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.NotFound("要修改的專案資料不存在。");
        }

        var rules = await ValidateBusinessRulesAsync(paraObject, uploadFiles);
        if (!rules.Success)
        {
            return rules;
        }

        if (await UnavailableOptionAsync(context, paraObject, searchItem) is { } optionError)
        {
            Logger.LogInformation("Pre-update validation failed because a selected option is no longer available. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Invalid(optionError);
        }

        // 名稱沒改就不檢查：0.10.3 之前網頁允許重名，舊的重名資料只改其他欄位時照常可存。標題上限（0.10.17 起）同理。
        var renamed = NormalizedTitle(searchItem.Title) != NormalizedTitle(paraObject.Title);
        if (renamed && TitleTooLong(paraObject.Title) is { } tooLong)
        {
            return tooLong;
        }

        if (renamed && await TitleTakenAsync(context, paraObject.Title, paraObject.Id))
        {
            Logger.LogInformation("Pre-update validation failed because project title already exists. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Conflict("專案名稱已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    /// <summary>這次新選的分類或團隊已不存在或已停用（0.10.15 起，見 <see cref="SelectedOptionGuard"/>）：回給使用者的訊息，都有效時回 null。</summary>
    private static async Task<string?> UnavailableOptionAsync(BackendDBContext context, ProjectAdapterModel model, Project? before)
    {
        if (SelectedOptionGuard.FindUnavailable(model.Categories, TagStringHelper.ToList(before?.Categories), await SelectedOptionGuard.EnabledCategoryNamesAsync(context)) is { } category)
        {
            return SelectedOptionGuard.Message("分類", category);
        }

        return SelectedOptionGuard.FindUnavailable(model.Teams, TagStringHelper.ToList(before?.Teams), await SelectedOptionGuard.EnabledTeamNamesAsync(context)) is { } team
            ? SelectedOptionGuard.Message("團隊", team)
            : null;
    }

    /// <summary>
    /// 專案名稱是否已被其他未刪除的專案使用（0.10.3 起，使用者決定統一擋重名）：去前後空白、只把 A-Z 視為不分大小寫（與 SQLite 的 lower() 相同）。
    /// 放在存檔前檢查而不是 <see cref="AddAsync"/>：AI Agent 工具與「產生測試專案」直接呼叫新增，不受影響。
    /// </summary>
    private static Task<bool> TitleTakenAsync(BackendDBContext context, string? title, int? excludeId)
    {
        var normalized = NormalizedTitle(title);
        return context.Project.AsNoTracking().AnyAsync(x => x.Title.Trim().ToLower() == normalized && (excludeId == null || x.Id != excludeId));
    }

    private static string NormalizedTitle(string? title) => NameNormalizer.ToLowerAscii(NameNormalizer.Normalize(title));

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<ProjectFileDownloadResult?> GetFileDownloadAsync(int projectFileId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var file = await context.ProjectFile
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectFileId);

        if (file is null)
        {
            return null;
        }

        var parent = await context.Project.AsNoTracking().FirstOrDefaultAsync(p => p.Id == file.ProjectId);

        // ⚠️ 父專案查不到（0.9.94 起最常見的原因：已被軟刪除）就不提供下載。
        // 不能交給下面的團隊檢查：IsTeamAccessible(null) 的語意是「沒有團隊 = 公開」，
        // 原本限團隊的附件會因此對任何有專案檢視權限的人公開。
        if (parent is null)
        {
            Logger.LogInformation("Project file download refused because the project is deleted or missing. ProjectFileId={ProjectFileId}", projectFileId);
            return null;
        }

        var scope = await accessScope.GetAsync();
        if (!RecordTeamScope.CanAccess(parent?.Teams, scope))
        {
            Logger.LogWarning("Project file download denied by team scope. ProjectFileId={ProjectFileId}", projectFileId);
            return null;
        }

        var fullPath = fileStore.TryResolveFullPath(file.RelativePath);
        if (fullPath is null)
        {
            Logger.LogWarning(
                "Project file download refused because the resolved path escapes the configured root. ProjectFileId={ProjectFileId}, RelativePath={RelativePath}",
                projectFileId, file.RelativePath);
            return null;
        }

        if (!File.Exists(fullPath))
        {
            Logger.LogWarning("Project file metadata exists but physical file was not found. ProjectFileId={ProjectFileId}, FullPath={FullPath}", projectFileId, fullPath);
            return null;
        }

        return new ProjectFileDownloadResult
        {
            Content = File.OpenRead(fullPath),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            DownloadFileName = file.OriginalFileName
        };
    }

    /// <summary>
    /// 標題上限（0.10.17 起，使用者 2026/11/14 決定；以前沒有上限）：去頭尾空白後計。新增時一律檢查；修改時只在標題有改時檢查，
    /// 以前存下的長標題只改其他欄位時照常可存（同重名、帳號格式的做法）。畫面的輸入框另以 MaxLength 擋住新輸入。
    /// </summary>
    private VerifyRecordResult? TitleTooLong(string? title)
    {
        if (NameNormalizer.Normalize(title).Length <= ProjectAdapterModel.ProjectTitleMaxLength)
        {
            return null;
        }

        Logger.LogWarning("Project validation failed because the title is too long. Length={Length}", title?.Length ?? 0);
        return VerifyRecordResultFactory.Invalid($"專案標題長度不可超過 {ProjectAdapterModel.ProjectTitleMaxLength} 字元。");
    }

    private Task<VerifyRecordResult> ValidateBusinessRulesAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Project validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return Task.FromResult(VerifyRecordResultFactory.Invalid("結束日期不可早於開始日期。"));
        }

        if (ProjectAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            Logger.LogWarning("Project validation failed because status is invalid. Title={Title}, Status={Status}", paraObject.Title, paraObject.Status);
            return Task.FromResult(VerifyRecordResultFactory.Invalid("專案狀態不合法。"));
        }

        if (ProjectAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            Logger.LogWarning("Project validation failed because priority is invalid. Title={Title}, Priority={Priority}", paraObject.Title, paraObject.Priority);
            return Task.FromResult(VerifyRecordResultFactory.Invalid("專案優先順序不合法。"));
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            Logger.LogWarning("Project validation failed because completion percentage is out of range. Title={Title}, CompletionPercentage={CompletionPercentage}", paraObject.Title, paraObject.CompletionPercentage);
            return Task.FromResult(VerifyRecordResultFactory.Invalid("完成百分比必須介於 0 到 100。"));
        }

        if (uploadFiles is not null)
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.FileSize > MaxUploadFileSize)
                {
                    Logger.LogWarning("Project upload validation failed because file exceeded the size limit. FileName={FileName}, FileSize={FileSize}", uploadFile.FileName, uploadFile.FileSize);
                    return Task.FromResult(VerifyRecordResultFactory.Invalid($"檔案 {uploadFile.FileName} 超過 1GB 限制"));
                }

                // 副檔名白名單。儲存檔名雖已改為 GUID，但副檔名會保留、下載時也會回吐
                // ContentType；允許 .html / .svg 等同在自己的網域上開一個 stored XSS。
                if (!UploadFileTypePolicy.IsAllowed(uploadFile.FileName, allowedUploadExtensions))
                {
                    Logger.LogWarning("Project upload validation failed because the file extension is not allowed. FileName={FileName}", uploadFile.FileName);
                    return Task.FromResult(VerifyRecordResultFactory.Invalid($"檔案 {uploadFile.FileName} 的類型不在允許清單中。"));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(projectFileRootPath))
        {
            Logger.LogWarning("Project upload validation failed because ProjectFilePath is not configured.");
            return Task.FromResult(VerifyRecordResultFactory.Invalid("尚未設定專案附件儲存目錄"));
        }

        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context**（同一個工作單元，
    /// 實體檔案落地與資料表紀錄要一起成敗），不可自行 CreateDbContext。
    /// </summary>
    /// <param name="createdFullPaths">這一次寫入的實體檔（呼叫端的交易沒提交時要刪掉）。</param>
    private async Task<VerifyRecordResult> SaveNewFilesAsync(BackendDBContext context, Project project, IEnumerable<ProjectUploadFileInput>? uploadFiles, List<string> createdFullPaths)
    {
        if (uploadFiles is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        List<ProjectFile> newFiles = [];

        try
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.Content == Stream.Null)
                {
                    continue;
                }

                var fileMetadata = await SavePhysicalFileAsync(project, uploadFile);
                newFiles.Add(fileMetadata.File);
                createdFullPaths.Add(fileMetadata.FullPath);
            }

            if (newFiles.Count > 0)
            {
                await context.ProjectFile.AddRangeAsync(newFiles);
                await context.SaveChangesAsync();
            }

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // 剛寫入的實體檔由呼叫端在交易沒提交時刪掉（DeleteCreatedFiles）。
            Logger.LogError(ex, "Failed to save project files. ProjectId={ProjectId}", project.Id);
            return VerifyRecordResultFactory.Error("專案附件儲存失敗", ex);
        }
    }

    /// <summary>
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context**（同一個工作單元，
    /// 實體檔案落地與資料表紀錄要一起成敗），不可自行 CreateDbContext。
    /// </summary>
    /// <returns>要在交易提交之後才刪的實體檔（相對路徑）。</returns>
    private static async Task<List<string>> RemoveProjectFilesAsync(BackendDBContext context, Project project, IEnumerable<int>? removedFileIds)
    {
        var removedFileIdSet = (removedFileIds ?? []).Distinct().ToHashSet();
        if (removedFileIdSet.Count == 0)
        {
            return [];
        }

        var filesToRemove = project.Files
            .Where(x => removedFileIdSet.Contains(x.Id))
            .ToList();

        // ⚠️ 先存資料庫、成功後才刪實體檔（0.9.97 起）：0.9.96 之前是先刪檔再存，
        // 存檔失敗時資料列還在、檔案卻已經不見了。0.10.3 起連「提交」都要成功才刪（由 UpdateAsync 刪）。
        var relativePaths = filesToRemove.Select(x => x.RelativePath).ToList();
        foreach (var file in filesToRemove)
        {
            context.ProjectFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        return relativePaths;
    }

    private async Task<(ProjectFile File, string FullPath)> SavePhysicalFileAsync(Project project, ProjectUploadFileInput uploadFile)
    {
        var originalFileName = Path.GetFileName(uploadFile.FileName);
        var extension = Path.GetExtension(originalFileName);
        var year = project.CreatedAt.Year.ToString("0000");
        var month = project.CreatedAt.Month.ToString("00");
        var relativePath = Path.Combine(year, month, $"{Guid.NewGuid():N}{extension}");
        var fullPath = GetFullPath(relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (uploadFile.Content.CanSeek)
        {
            uploadFile.Content.Position = 0;
        }

        try
        {
            await using var targetStream = File.Create(fullPath);
            await uploadFile.Content.CopyToAsync(targetStream);
        }
        catch
        {
            // 寫到一半失敗（上傳中斷、磁碟滿）：這個檔案還沒交給呼叫端記錄，在這裡就要清掉，否則留下半個檔案。
            DeleteCreatedFiles([fullPath]);
            throw;
        }

        // ⚠️ 不使用 uploadFile.ContentType：呼叫端可以上傳 .txt 卻宣稱是 text/html。
        // 一律依副檔名對應（副檔名本身已通過白名單驗證）。
        var contentType = UploadFileTypePolicy.ResolveContentType(originalFileName);

        return (
            new ProjectFile
            {
                ProjectId = project.Id,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(fullPath),
                RelativePath = relativePath.Replace('\\', '/'),
                ContentType = contentType,
                FileSize = uploadFile.FileSize,
                CreatedAt = DateTime.Now
            },
            fullPath);
    }

    /// <summary>附件上傳／刪除的稽核：只記筆數，沒有異動就不寫。</summary>
    private Task WriteFileAuditAsync(string action, int projectId, int count)
        => count > 0 ? WriteAuditAsync(action, projectId, $"count={count}") : Task.CompletedTask;

    /// <summary>
    /// 只用在存檔沒成功時回收這一次剛寫入的檔案（路徑是這裡自己產生的）；其他刪檔一律經 <see cref="ProjectFileStore"/>。
    /// 刪不掉只記警告：資料已回滾，留下的是孤兒檔，不影響資料正確性。
    /// </summary>
    private void DeleteCreatedFiles(List<string> fullPaths)
    {
        foreach (var fullPath in fullPaths)
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.LogWarning(ex, "Failed to remove an uploaded file after the save was rolled back. FullPath={FullPath}", fullPath);
            }
        }
    }

    private string GetFullPath(string relativePath)
    {
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(projectFileRootPath, normalizedRelativePath);
    }

    /// <summary>Web API 的既有篩選（0.10.3 起）；未填的條件不篩。</summary>
    private static IQueryable<Project> ApplyFilter(IQueryable<Project> source, ProjectListFilter? filter)
    {
        if (filter is null)
        {
            return source;
        }

        if (!string.IsNullOrEmpty(filter.Owner))
        {
            source = source.Where(x => x.Owner == filter.Owner);
        }

        if (!string.IsNullOrEmpty(filter.Status))
        {
            source = source.Where(x => x.Status == filter.Status);
        }

        if (!string.IsNullOrEmpty(filter.Priority))
        {
            source = source.Where(x => x.Priority == filter.Priority);
        }

        if (filter.StartDateFrom is { } from)
        {
            source = source.Where(x => x.StartDate >= from);
        }

        if (filter.StartDateTo is { } to)
        {
            source = source.Where(x => x.StartDate <= to);
        }

        if (filter.CompletionPercentageMin is { } min)
        {
            source = source.Where(x => x.CompletionPercentage >= min);
        }

        if (filter.CompletionPercentageMax is { } max)
        {
            source = source.Where(x => x.CompletionPercentage <= max);
        }

        return source;
    }

    public class ProjectFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}
