using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
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
    private readonly IReadOnlyCollection<string> allowedUploadExtensions;

    public IMapper Mapper { get; }
    public ILogger<ProjectService> Logger { get; }

    public ProjectService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<ProjectService> logger,
        IOptions<SystemSettings> systemSettings,
        IRecordAccessScopeProvider accessScope)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
        projectFileRootPath = systemSettings.Value.ExternalFileSystem.ProjectFilePath;
        allowedUploadExtensions = systemSettings.Value.Upload.AllowedExtensions;
    }

    public async Task<DataRequestResult<ProjectAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading projects. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
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

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<Project>(x => x.Teams, scope.Teams));
        }

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
        result.Result = Mapper.Map<List<ProjectAdapterModel>>(records);
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
        if (!TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Project access denied by team scope. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        return Mapper.Map<ProjectAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating project. Title={Title}, Owner={Owner}", paraObject.Title, paraObject.Owner);

        try
        {
            Project itemParameter = Mapper.Map<Project>(paraObject);
            itemParameter.Files = [];

            await context.Project.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(context, itemParameter, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            Logger.LogInformation("Project created successfully. ProjectId={ProjectId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create project. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null,
        IEnumerable<int>? removedFileIds = null)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            Project? currentItem = await context.Project
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Project update rejected because record was not found. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的專案資料。");
            }

            currentItem.Title = paraObject.Title;
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

            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(context, currentItem, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            var removeFilesResult = await RemoveProjectFilesAsync(context, currentItem, removedFileIds);
            if (!removeFilesResult.Success)
            {
                return removeFilesResult;
            }

            Logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", currentItem.Id, currentItem.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting project. ProjectId={ProjectId}", id);

        try
        {
            Project? item = await context.Project
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Project deletion rejected because record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的專案資料。");
            }

            foreach (var file in item.Files.ToList())
            {
                DeletePhysicalFile(file);
            }

            context.Project.Remove(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除專案失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-create validation for project. Title={Title}", paraObject.Title);
        return ValidateBusinessRulesAsync(paraObject, uploadFiles);
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
            return VerifyRecordResultFactory.Build(false, "要修改的專案資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

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
        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(parent?.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Project file download denied by team scope. ProjectFileId={ProjectFileId}", projectFileId);
            return null;
        }

        var fullPath = GetFullPath(file.RelativePath);
        if (!IsUnderProjectFileRoot(fullPath))
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

    private Task<VerifyRecordResult> ValidateBusinessRulesAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Project validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。"));
        }

        if (ProjectAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            Logger.LogWarning("Project validation failed because status is invalid. Title={Title}, Status={Status}", paraObject.Title, paraObject.Status);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "專案狀態不合法。"));
        }

        if (ProjectAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            Logger.LogWarning("Project validation failed because priority is invalid. Title={Title}, Priority={Priority}", paraObject.Title, paraObject.Priority);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "專案優先順序不合法。"));
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            Logger.LogWarning("Project validation failed because completion percentage is out of range. Title={Title}, CompletionPercentage={CompletionPercentage}", paraObject.Title, paraObject.CompletionPercentage);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "完成百分比必須介於 0 到 100。"));
        }

        if (uploadFiles is not null)
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.FileSize > MaxUploadFileSize)
                {
                    Logger.LogWarning("Project upload validation failed because file exceeded the size limit. FileName={FileName}, FileSize={FileSize}", uploadFile.FileName, uploadFile.FileSize);
                    return Task.FromResult(VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 超過 1GB 限制"));
                }

                // 副檔名白名單。儲存檔名雖已改為 GUID，但副檔名會保留、下載時也會回吐
                // ContentType；允許 .html / .svg 等同在自己的網域上開一個 stored XSS。
                if (!UploadFileTypePolicy.IsAllowed(uploadFile.FileName, allowedUploadExtensions))
                {
                    Logger.LogWarning("Project upload validation failed because the file extension is not allowed. FileName={FileName}", uploadFile.FileName);
                    return Task.FromResult(VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 的類型不在允許清單中。"));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(projectFileRootPath))
        {
            Logger.LogWarning("Project upload validation failed because ProjectFilePath is not configured.");
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "尚未設定專案附件儲存目錄"));
        }

        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context**（同一個工作單元，
    /// 實體檔案落地與資料表紀錄要一起成敗），不可自行 CreateDbContext。
    /// </summary>
    private async Task<VerifyRecordResult> SaveNewFilesAsync(BackendDBContext context, Project project, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (uploadFiles is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        List<ProjectFile> newFiles = [];
        List<string> createdFullPaths = [];

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
            foreach (var fullPath in createdFullPaths)
            {
                TryDeleteFile(fullPath);
            }

            Logger.LogError(ex, "Failed to save project files. ProjectId={ProjectId}", project.Id);
            return VerifyRecordResultFactory.Build(false, "專案附件儲存失敗", ex);
        }
    }

    /// <summary>
    /// 由 Add/Update 呼叫，**必須沿用呼叫端的 context**（同一個工作單元，
    /// 實體檔案落地與資料表紀錄要一起成敗），不可自行 CreateDbContext。
    /// </summary>
    private async Task<VerifyRecordResult> RemoveProjectFilesAsync(BackendDBContext context, Project project, IEnumerable<int>? removedFileIds)
    {
        if (removedFileIds is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        var removedFileIdSet = removedFileIds.Distinct().ToHashSet();
        if (removedFileIdSet.Count == 0)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        var filesToRemove = project.Files
            .Where(x => removedFileIdSet.Contains(x.Id))
            .ToList();

        foreach (var file in filesToRemove)
        {
            DeletePhysicalFile(file);
            context.ProjectFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        return VerifyRecordResultFactory.Build(true);
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

        await using (var targetStream = File.Create(fullPath))
        {
            await uploadFile.Content.CopyToAsync(targetStream);
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

    private void DeletePhysicalFile(ProjectFile file)
    {
        var fullPath = GetFullPath(file.RelativePath);
        TryDeleteFile(fullPath);
    }

    private void TryDeleteFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        File.Delete(fullPath);
    }

    /// <summary>
    /// 解析後的絕對路徑必須確實落在設定的附件根目錄之下。
    ///
    /// RelativePath 一律由 <see cref="SavePhysicalFileAsync"/> 寫成「年/月/Guid.副檔名」，
    /// 正常情況不可能逸出；這裡守的是資料庫被直接改過的情況 —— 下載端點會把伺服器本機的
    /// 檔案內容送出去，不該把「這個值一定安全」當成前提。
    ///
    /// 根目錄未設定時一律拒絕：Path.Combine("", x) 會退化成相對於工作目錄的路徑。
    /// </summary>
    private bool IsUnderProjectFileRoot(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(projectFileRootPath))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectFileRootPath));
        return Path.GetFullPath(fullPath)
            .StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string GetFullPath(string relativePath)
    {
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(projectFileRootPath, normalizedRelativePath);
    }

    public class ProjectFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}
