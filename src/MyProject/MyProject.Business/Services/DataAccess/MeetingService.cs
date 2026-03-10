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

        result.Count = dataSource.Count();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        result.Result = Mapper.Map<List<MeetingAdapterModel>>(dataSource);
        await Task.Yield();
        return result;
    }

    public async Task<MeetingAdapterModel> GetAsync(int id)
    {
        Meeting? item = await context.Meeting
            .AsNoTracking()
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == id);

        return item is null
            ? new MeetingAdapterModel()
            : Mapper.Map<MeetingAdapterModel>(item);
    }

    public async Task<List<ProjectAdapterModel>> GetProjectsAsync()
    {
        List<Project> projects = await context.Project
            .AsNoTracking()
            .OrderBy(x => x.Title)
            .ToListAsync();

        return Mapper.Map<List<ProjectAdapterModel>>(projects);
    }

    public async Task<VerifyRecordResult> AddAsync(MeetingAdapterModel paraObject)
    {
        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting itemParameter = Mapper.Map<Meeting>(paraObject);
            itemParameter.Project = null;

            await context.Meeting.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "新增會議記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "新增會議記錄發生例外異常", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MeetingAdapterModel paraObject)
    {
        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? currentItem = await context.Meeting
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);
            if (currentItem == null)
            {
                return VerifyRecordResultFactory.Build(false, "無法修改會議記錄");
            }

            Meeting itemData = Mapper.Map<Meeting>(paraObject);
            itemData.Project = null;

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "修改會議記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "修改會議記錄發生例外異常", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        try
        {
            CleanTrackingHelper.Clean<Meeting>(context);
            Meeting? item = await context.Meeting
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                return VerifyRecordResultFactory.Build(false, "無法刪除會議記錄");
            }

            CleanTrackingHelper.Clean<Meeting>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Meeting>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "刪除會議記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "刪除會議記錄發生例外異常", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(MeetingAdapterModel paraObject)
    {
        return ValidateBusinessRulesAsync(paraObject);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MeetingAdapterModel paraObject)
    {
        CleanTrackingHelper.Clean<Meeting>(context);
        Meeting? searchItem = await context.Meeting
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);
        if (searchItem == null)
        {
            return VerifyRecordResultFactory.Build(false, "要更新的會議記錄已不存在資料庫");
        }

        return await ValidateBusinessRulesAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MeetingAdapterModel paraObject)
    {
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private async Task<VerifyRecordResult> ValidateBusinessRulesAsync(MeetingAdapterModel paraObject)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            return VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期");
        }

        if (paraObject.ProjectId is null || paraObject.ProjectId <= 0)
        {
            return VerifyRecordResultFactory.Build(false, "請選擇所屬專案");
        }

        bool projectExists = await context.Project
            .AsNoTracking()
            .AnyAsync(x => x.Id == paraObject.ProjectId);
        if (!projectExists)
        {
            return VerifyRecordResultFactory.Build(false, "所選專案不存在");
        }

        return VerifyRecordResultFactory.Build(true);
    }
}
