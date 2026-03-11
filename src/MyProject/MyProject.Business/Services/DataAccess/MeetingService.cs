using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

public class MeetingService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<MeetingService> Logger { get; }

    public MeetingService(BackendDBContext context, IMapper mapper, ILogger<MeetingService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
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

    public async Task<VerifyRecordResult> AddAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogInformation("Creating meeting. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting itemParameter = Mapper.Map<Meeting>(paraObject);
            itemParameter.Project = null;

            await context.Meeting.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation("Meeting created successfully. MeetingId={MeetingId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create meeting. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "新增會議失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogInformation("Updating meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? currentItem = await context.Meeting
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Meeting update rejected because record was not found. MeetingId={MeetingId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的會議資料。");
            }

            Meeting itemData = Mapper.Map<Meeting>(paraObject);
            itemData.Project = null;

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);

            Logger.LogInformation("Meeting updated successfully. MeetingId={MeetingId}, Title={Title}", itemData.Id, itemData.Title);
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
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Meeting deletion rejected because record was not found. MeetingId={MeetingId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的會議資料。");
            }

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(item).State = EntityState.Deleted;
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

    public Task<VerifyRecordResult> BeforeAddCheckAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for meeting. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
        return ValidateBusinessRulesAsync(paraObject);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MeetingAdapterModel paraObject)
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

        return await ValidateBusinessRulesAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MeetingAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for meeting. MeetingId={MeetingId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private async Task<VerifyRecordResult> ValidateBusinessRulesAsync(MeetingAdapterModel paraObject)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Meeting validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。");
        }

        if (paraObject.ProjectId is null || paraObject.ProjectId <= 0)
        {
            Logger.LogWarning("Meeting validation failed because project id is missing. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "必須選擇所屬專案。");
        }

        bool projectExists = await context.Project
            .AsNoTracking()
            .AnyAsync(x => x.Id == paraObject.ProjectId);

        if (!projectExists)
        {
            Logger.LogWarning("Meeting validation failed because referenced project does not exist. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "指定的專案不存在。");
        }

        return VerifyRecordResultFactory.Build(true);
    }
}
