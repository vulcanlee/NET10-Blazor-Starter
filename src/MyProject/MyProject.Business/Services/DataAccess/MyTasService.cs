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

public class MyTasService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<MyTasService> Logger { get; }

    public MyTasService(BackendDBContext context, IMapper mapper, ILogger<MyTasService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
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

    public async Task<VerifyRecordResult> AddAsync(MyTasAdapterModel paraObject)
    {
        Logger.LogInformation("Creating task. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);

        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas itemParameter = Mapper.Map<MyTas>(paraObject);
            itemParameter.Project = null;

            await context.MyTas.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);

            Logger.LogInformation("Task created successfully. TaskId={TaskId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create task. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "新增工作失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MyTasAdapterModel paraObject)
    {
        Logger.LogInformation("Updating task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas? currentItem = await context.MyTas
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Task update rejected because record was not found. TaskId={TaskId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的工作資料。");
            }

            MyTas itemData = Mapper.Map<MyTas>(paraObject);
            itemData.Project = null;

            CleanTrackingHelper.Clean<MyTas>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);

            Logger.LogInformation("Task updated successfully. TaskId={TaskId}, Title={Title}", itemData.Id, itemData.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改工作失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting task. TaskId={TaskId}", id);

        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas? item = await context.MyTas
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Task deletion rejected because record was not found. TaskId={TaskId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的工作資料。");
            }

            CleanTrackingHelper.Clean<MyTas>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);

            Logger.LogInformation("Task deleted successfully. TaskId={TaskId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete task. TaskId={TaskId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除工作失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(MyTasAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for task. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
        return ValidateBusinessRulesAsync(paraObject);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MyTasAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);

        CleanTrackingHelper.Clean<MyTas>(context);
        MyTas? searchItem = await context.MyTas
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because task was not found. TaskId={TaskId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的工作資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MyTasAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for task. TaskId={TaskId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private async Task<VerifyRecordResult> ValidateBusinessRulesAsync(MyTasAdapterModel paraObject)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Task validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。");
        }

        if (MyTasAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            Logger.LogWarning("Task validation failed because status is invalid. Title={Title}, Status={Status}", paraObject.Title, paraObject.Status);
            return VerifyRecordResultFactory.Build(false, "工作狀態不合法。");
        }

        if (MyTasAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            Logger.LogWarning("Task validation failed because priority is invalid. Title={Title}, Priority={Priority}", paraObject.Title, paraObject.Priority);
            return VerifyRecordResultFactory.Build(false, "工作優先順序不合法。");
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            Logger.LogWarning("Task validation failed because completion percentage is out of range. Title={Title}, CompletionPercentage={CompletionPercentage}", paraObject.Title, paraObject.CompletionPercentage);
            return VerifyRecordResultFactory.Build(false, "完成百分比必須介於 0 到 100。");
        }

        if (paraObject.ProjectId is null || paraObject.ProjectId <= 0)
        {
            Logger.LogWarning("Task validation failed because project id is missing. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "必須選擇所屬專案。");
        }

        bool projectExists = await context.Project
            .AsNoTracking()
            .AnyAsync(x => x.Id == paraObject.ProjectId);

        if (!projectExists)
        {
            Logger.LogWarning("Task validation failed because referenced project does not exist. Title={Title}, ProjectId={ProjectId}", paraObject.Title, paraObject.ProjectId);
            return VerifyRecordResultFactory.Build(false, "指定的專案不存在。");
        }

        return VerifyRecordResultFactory.Build(true);
    }
}
