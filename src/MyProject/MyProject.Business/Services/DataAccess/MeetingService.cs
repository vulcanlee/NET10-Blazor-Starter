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

public class MeetingService
{
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    private readonly BackendDBContext context;
    private readonly string meetingFileRootPath;

    public IMapper Mapper { get; }
    public ILogger<MeetingService> Logger { get; }

    public MeetingService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<MeetingService> logger,
        IOptions<SystemSettings> systemSettings)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        meetingFileRootPath = systemSettings.Value.ExternalFileSystem.MeetingFilePath;
    }

    public async Task<DataRequestResult<MeetingAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading meetings. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<MeetingAdapterModel> result = new();
        IQueryable<Meeting> dataSource = context.Meeting
            .AsNoTracking()
            .Include(x => x.Project);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                (x.Summary ?? string.Empty).Contains(search) ||
                (x.Participants ?? string.Empty).Contains(search) ||
                (x.Project != null && x.Project.Title.Contains(search)));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(MeetingAdapterModel.Title))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.StartDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.EndDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.EndDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.ProjectTitle))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Project != null ? x.Project.Title : string.Empty).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Project != null ? x.Project.Title : string.Empty).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MeetingAdapterModel.UpdatedAt))
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
        result.Result = Mapper.Map<List<MeetingAdapterModel>>(records);
        Logger.LogDebug("Loaded meetings successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<MeetingAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading meeting by id. MeetingId={MeetingId}", id);

        Meeting? item = await context.Meeting
            .AsNoTracking()
            .Include(x => x.Project)
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Meeting not found. MeetingId={MeetingId}", id);
            return new MeetingAdapterModel();
        }

        return Mapper.Map<MeetingAdapterModel>(item);
    }

    public async Task<List<ProjectAdapterModel>> GetProjectsAsync()
    {
        Logger.LogDebug("Loading projects for meeting editor.");

        List<Project> projects = await context.Project
            .AsNoTracking()
            .OrderBy(x => x.Title)
            .ToListAsync();

        Logger.LogDebug("Loaded projects for meeting editor successfully. Count={Count}", projects.Count);
        return Mapper.Map<List<ProjectAdapterModel>>(projects);
    }

    public async Task<VerifyRecordResult> AddAsync(MeetingAdapterModel paraObject, IEnumerable<MeetingUploadFileInput>? uploadFiles = null)
    {
        Logger.LogInformation("Creating meeting. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting itemParameter = Mapper.Map<Meeting>(paraObject);
            itemParameter.Project = null;
            itemParameter.Files = [];

            await context.Meeting.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(itemParameter, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            Logger.LogInformation("Meeting created successfully. MeetingId={MeetingId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create meeting. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "新增會議失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(
        MeetingAdapterModel paraObject,
        IEnumerable<MeetingUploadFileInput>? uploadFiles = null,
        IEnumerable<int>? removedFileIds = null)
    {
        Logger.LogInformation("Updating meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? currentItem = await context.Meeting
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Meeting update rejected because record was not found. MeetingId={MeetingId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的會議資料。");
            }

            currentItem.Title = paraObject.Title;
            currentItem.Description = paraObject.Description;
            currentItem.Summary = paraObject.Summary;
            currentItem.Participants = paraObject.Participants;
            currentItem.StartDate = paraObject.StartDate;
            currentItem.EndDate = paraObject.EndDate;
            currentItem.ProjectId = paraObject.ProjectId;
            currentItem.UpdatedAt = paraObject.UpdatedAt;

            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(currentItem, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            var removeFilesResult = await RemoveMeetingFilesAsync(currentItem, removedFileIds);
            if (!removeFilesResult.Success)
            {
                return removeFilesResult;
            }

            Logger.LogInformation("Meeting updated successfully. MeetingId={MeetingId}, Title={Title}", currentItem.Id, currentItem.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改會議失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting meeting. MeetingId={MeetingId}", id);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? item = await context.Meeting
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Meeting deletion rejected because record was not found. MeetingId={MeetingId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的會議資料。");
            }

            foreach (var file in item.Files.ToList())
            {
                DeletePhysicalFile(file);
            }

            context.Meeting.Remove(item);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation("Meeting deleted successfully. MeetingId={MeetingId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete meeting. MeetingId={MeetingId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除會議失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(MeetingAdapterModel paraObject, IEnumerable<MeetingUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-create validation for meeting. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
        return ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(
        MeetingAdapterModel paraObject,
        IEnumerable<MeetingUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-update validation for meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);

        CleanTrackingHelper.Clean<Meeting>(context);
        Meeting? searchItem = await context.Meeting
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because meeting was not found. MeetingId={MeetingId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的會議資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<MeetingFileDownloadResult?> GetFileDownloadAsync(int meetingFileId)
    {
        var file = await context.MeetingFile
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == meetingFileId);

        if (file is null)
        {
            return null;
        }

        var fullPath = GetFullPath(file.RelativePath);
        if (!File.Exists(fullPath))
        {
            Logger.LogWarning("Meeting file metadata exists but physical file was not found. MeetingFileId={MeetingFileId}, FullPath={FullPath}", meetingFileId, fullPath);
            return null;
        }

        return new MeetingFileDownloadResult
        {
            Content = File.OpenRead(fullPath),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            DownloadFileName = file.OriginalFileName
        };
    }

    private async Task<VerifyRecordResult> ValidateBusinessRulesAsync(MeetingAdapterModel paraObject, IEnumerable<MeetingUploadFileInput>? uploadFiles)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Meeting validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。");
        }

        if (paraObject.ProjectId is null || paraObject.ProjectId <= 0)
        {
            Logger.LogWarning("Meeting validation failed because project id is missing. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "請選擇所屬專案。");
        }

        bool projectExists = await context.Project
            .AsNoTracking()
            .AnyAsync(x => x.Id == paraObject.ProjectId);

        if (!projectExists)
        {
            Logger.LogWarning("Meeting validation failed because referenced project does not exist. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "所選專案不存在。");
        }

        if (uploadFiles is not null)
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.FileSize > MaxUploadFileSize)
                {
                    Logger.LogWarning("Meeting upload validation failed because file exceeded the size limit. FileName={FileName}, FileSize={FileSize}", uploadFile.FileName, uploadFile.FileSize);
                    return VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 超過 1GB 限制");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(meetingFileRootPath))
        {
            Logger.LogWarning("Meeting upload validation failed because MeetingFilePath is not configured.");
            return VerifyRecordResultFactory.Build(false, "尚未設定會議附件儲存目錄。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    private async Task<VerifyRecordResult> SaveNewFilesAsync(Meeting meeting, IEnumerable<MeetingUploadFileInput>? uploadFiles)
    {
        if (uploadFiles is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        List<MeetingFile> newFiles = [];
        List<string> createdFullPaths = [];

        try
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.Content == Stream.Null)
                {
                    continue;
                }

                var fileMetadata = await SavePhysicalFileAsync(meeting, uploadFile);
                newFiles.Add(fileMetadata.File);
                createdFullPaths.Add(fileMetadata.FullPath);
            }

            if (newFiles.Count > 0)
            {
                await context.MeetingFile.AddRangeAsync(newFiles);
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

            Logger.LogError(ex, "Failed to save meeting files. MeetingId={MeetingId}", meeting.Id);
            return VerifyRecordResultFactory.Build(false, "會議附件儲存失敗。", ex);
        }
    }

    private async Task<VerifyRecordResult> RemoveMeetingFilesAsync(Meeting meeting, IEnumerable<int>? removedFileIds)
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

        var filesToRemove = meeting.Files
            .Where(x => removedFileIdSet.Contains(x.Id))
            .ToList();

        foreach (var file in filesToRemove)
        {
            DeletePhysicalFile(file);
            context.MeetingFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        return VerifyRecordResultFactory.Build(true);
    }

    private async Task<(MeetingFile File, string FullPath)> SavePhysicalFileAsync(Meeting meeting, MeetingUploadFileInput uploadFile)
    {
        var originalFileName = Path.GetFileName(uploadFile.FileName);
        var extension = Path.GetExtension(originalFileName);
        var year = meeting.CreatedAt.Year.ToString("0000");
        var month = meeting.CreatedAt.Month.ToString("00");
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
            new MeetingFile
            {
                MeetingId = meeting.Id,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(fullPath),
                RelativePath = relativePath.Replace('\\', '/'),
                ContentType = contentType,
                FileSize = uploadFile.FileSize,
                CreatedAt = DateTime.Now
            },
            fullPath);
    }

    private void DeletePhysicalFile(MeetingFile file)
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

        return Path.Combine(meetingFileRootPath, normalizedRelativePath);
    }

    public class MeetingFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}
