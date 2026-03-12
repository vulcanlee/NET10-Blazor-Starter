using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

public class MyTasService
{
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    private readonly BackendDBContext context;
    private readonly string taskFileRootPath;

    public IMapper Mapper { get; }
    public ILogger<MyTasService> Logger { get; }

    public MyTasService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<MyTasService> logger,
        IOptions<SystemSettings> systemSettings)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        taskFileRootPath = systemSettings.Value.ExternalFileSystem.TaskFilePath;
    }

    public async Task<DataRequestResult<MyTasAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading tasks. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<MyTasAdapterModel> result = new();
        IQueryable<MyTas> dataSource = context.MyTas
            .AsNoTracking()
            .Include(x => x.Project);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                x.Category.Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                x.Owner.Contains(search) ||
                (x.Project != null && x.Project.Title.Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(MyTasAdapterModel.Title))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.StartDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.EndDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.EndDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.Category))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Category).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Category).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.Status))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.Priority))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Priority).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.CompletionPercentage))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CompletionPercentage).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CompletionPercentage).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.Owner))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Owner).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Owner).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.ProjectTitle))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Project != null ? x.Project.Title : string.Empty).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Project != null ? x.Project.Title : string.Empty).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyTasAdapterModel.UpdatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        var records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<MyTasAdapterModel>>(records);
        Logger.LogDebug("Loaded tasks successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<MyTasAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading task by id. TaskId={TaskId}", id);

        MyTas? item = await context.MyTas
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Task not found. TaskId={TaskId}", id);
            return new MyTasAdapterModel();
        }

        return Mapper.Map<MyTasAdapterModel>(item);
    }

    public async Task<List<ProjectAdapterModel>> GetProjectsAsync()
    {
        Logger.LogDebug("Loading projects for task editor.");

        List<Project> projects = await context.Project
            .AsNoTracking()
            .OrderBy(x => x.Title)
            .ToListAsync();

        Logger.LogDebug("Loaded projects for task editor successfully. Count={Count}", projects.Count);
        return Mapper.Map<List<ProjectAdapterModel>>(projects);
    }

    public async Task<VerifyRecordResult> AddAsync(MyTasAdapterModel paraObject, IEnumerable<MyTasUploadFileInput>? uploadFiles = null)
    {
        Logger.LogInformation("Creating task. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);

        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas itemParameter = Mapper.Map<MyTas>(paraObject);
            itemParameter.Project = null;
            itemParameter.Files = [];

            await context.MyTas.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(itemParameter, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            Logger.LogInformation("Task created successfully. TaskId={TaskId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create task. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "新增工作項目失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(
        MyTasAdapterModel paraObject,
        IEnumerable<MyTasUploadFileInput>? uploadFiles = null,
        IEnumerable<int>? removedFileIds = null)
    {
        Logger.LogInformation("Updating task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas? currentItem = await context.MyTas
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Task update rejected because record was not found. TaskId={TaskId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的工作項目資料。");
            }

            currentItem.Title = paraObject.Title;
            currentItem.Description = paraObject.Description;
            currentItem.StartDate = paraObject.StartDate;
            currentItem.EndDate = paraObject.EndDate;
            currentItem.Category = paraObject.Category;
            currentItem.Status = paraObject.Status;
            currentItem.Priority = paraObject.Priority;
            currentItem.CompletionPercentage = paraObject.CompletionPercentage;
            currentItem.Owner = paraObject.Owner;
            currentItem.ProjectId = paraObject.ProjectId;
            currentItem.UpdatedAt = paraObject.UpdatedAt;

            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(currentItem, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            var removeFilesResult = await RemoveTaskFilesAsync(currentItem, removedFileIds);
            if (!removeFilesResult.Success)
            {
                return removeFilesResult;
            }

            Logger.LogInformation("Task updated successfully. TaskId={TaskId}, Title={Title}", currentItem.Id, currentItem.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改工作項目失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting task. TaskId={TaskId}", id);

        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas? item = await context.MyTas
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Task deletion rejected because record was not found. TaskId={TaskId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的工作項目資料。");
            }

            foreach (var file in item.Files.ToList())
            {
                DeletePhysicalFile(file);
            }

            context.MyTas.Remove(item);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);

            Logger.LogInformation("Task deleted successfully. TaskId={TaskId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete task. TaskId={TaskId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除工作項目失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(MyTasAdapterModel paraObject, IEnumerable<MyTasUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-create validation for task. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
        return ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(
        MyTasAdapterModel paraObject,
        IEnumerable<MyTasUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-update validation for task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);

        CleanTrackingHelper.Clean<MyTas>(context);
        MyTas? searchItem = await context.MyTas
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because task was not found. TaskId={TaskId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的工作項目資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MyTasAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<TaskFileDownloadResult?> GetFileDownloadAsync(int taskFileId)
    {
        var file = await context.MyTasFile
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == taskFileId);

        if (file is null)
        {
            return null;
        }

        var fullPath = GetFullPath(file.RelativePath);
        if (!File.Exists(fullPath))
        {
            Logger.LogWarning("Task file metadata exists but physical file was not found. TaskFileId={TaskFileId}, FullPath={FullPath}", taskFileId, fullPath);
            return null;
        }

        return new TaskFileDownloadResult
        {
            Content = File.OpenRead(fullPath),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            DownloadFileName = file.OriginalFileName
        };
    }

    private async Task<VerifyRecordResult> ValidateBusinessRulesAsync(MyTasAdapterModel paraObject, IEnumerable<MyTasUploadFileInput>? uploadFiles)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Task validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。");
        }

        if (MyTasAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            Logger.LogWarning("Task validation failed because status is invalid. Title={Title}, Status={Status}", paraObject.Title, paraObject.Status);
            return VerifyRecordResultFactory.Build(false, "工作項目狀態不合法。");
        }

        if (MyTasAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            Logger.LogWarning("Task validation failed because priority is invalid. Title={Title}, Priority={Priority}", paraObject.Title, paraObject.Priority);
            return VerifyRecordResultFactory.Build(false, "工作項目優先順序不合法。");
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            Logger.LogWarning("Task validation failed because completion percentage is out of range. Title={Title}, CompletionPercentage={CompletionPercentage}", paraObject.Title, paraObject.CompletionPercentage);
            return VerifyRecordResultFactory.Build(false, "完成百分比必須介於 0 到 100。");
        }

        if (paraObject.ProjectId is null || paraObject.ProjectId <= 0)
        {
            Logger.LogWarning("Task validation failed because project id is missing. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "請選擇所屬專案。");
        }

        bool projectExists = await context.Project
            .AsNoTracking()
            .AnyAsync(x => x.Id == paraObject.ProjectId);

        if (!projectExists)
        {
            Logger.LogWarning("Task validation failed because referenced project does not exist. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "所選專案不存在。");
        }

        if (uploadFiles is not null)
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.FileSize > MaxUploadFileSize)
                {
                    Logger.LogWarning("Task upload validation failed because file exceeded the size limit. FileName={FileName}, FileSize={FileSize}", uploadFile.FileName, uploadFile.FileSize);
                    return VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 超過 1GB 限制");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(taskFileRootPath))
        {
            Logger.LogWarning("Task upload validation failed because TaskFilePath is not configured.");
            return VerifyRecordResultFactory.Build(false, "尚未設定工作項目附件儲存目錄。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    private async Task<VerifyRecordResult> SaveNewFilesAsync(MyTas task, IEnumerable<MyTasUploadFileInput>? uploadFiles)
    {
        if (uploadFiles is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        List<MyTasFile> newFiles = [];
        List<string> createdFullPaths = [];

        try
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.Content == Stream.Null)
                {
                    continue;
                }

                var fileMetadata = await SavePhysicalFileAsync(task, uploadFile);
                newFiles.Add(fileMetadata.File);
                createdFullPaths.Add(fileMetadata.FullPath);
            }

            if (newFiles.Count > 0)
            {
                await context.MyTasFile.AddRangeAsync(newFiles);
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

            Logger.LogError(ex, "Failed to save task files. TaskId={TaskId}", task.Id);
            return VerifyRecordResultFactory.Build(false, "工作項目附件儲存失敗。", ex);
        }
    }

    private async Task<VerifyRecordResult> RemoveTaskFilesAsync(MyTas task, IEnumerable<int>? removedFileIds)
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

        var filesToRemove = task.Files
            .Where(x => removedFileIdSet.Contains(x.Id))
            .ToList();

        foreach (var file in filesToRemove)
        {
            DeletePhysicalFile(file);
            context.MyTasFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        return VerifyRecordResultFactory.Build(true);
    }

    private async Task<(MyTasFile File, string FullPath)> SavePhysicalFileAsync(MyTas task, MyTasUploadFileInput uploadFile)
    {
        var originalFileName = Path.GetFileName(uploadFile.FileName);
        var extension = Path.GetExtension(originalFileName);
        var year = task.CreatedAt.Year.ToString("0000");
        var month = task.CreatedAt.Month.ToString("00");
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

        var contentType = string.IsNullOrWhiteSpace(uploadFile.ContentType)
            ? "application/octet-stream"
            : uploadFile.ContentType;

        return (
            new MyTasFile
            {
                MyTasId = task.Id,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(fullPath),
                RelativePath = relativePath.Replace('\\', '/'),
                ContentType = contentType,
                FileSize = uploadFile.FileSize,
                CreatedAt = DateTime.Now
            },
            fullPath);
    }

    private void DeletePhysicalFile(MyTasFile file)
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

    private string GetFullPath(string relativePath)
    {
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(taskFileRootPath, normalizedRelativePath);
    }

    public class TaskFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}
