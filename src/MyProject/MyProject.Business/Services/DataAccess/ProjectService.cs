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

public class ProjectService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<ProjectService> Logger { get; }

    public ProjectService(BackendDBContext context, IMapper mapper, ILogger<ProjectService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
    }

    public async Task<DataRequestResult<ProjectAdapterModel>> GetAsync(DataRequest dataRequest)
    {
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

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(ProjectAdapterModel.Title))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.StartDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.EndDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.EndDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Status))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Priority))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Priority).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CompletionPercentage))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CompletionPercentage).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CompletionPercentage).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Owner))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Owner).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Owner).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.UpdatedAt))
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
        result.Result = Mapper.Map<List<ProjectAdapterModel>>(records);
        Logger.LogDebug("Loaded projects successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<ProjectAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading project by id. ProjectId={ProjectId}", id);

        Project? item = await context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Project not found. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        return Mapper.Map<ProjectAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogInformation("Creating project. Title={Title}, Owner={Owner}", paraObject.Title, paraObject.Owner);

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project itemParameter = Mapper.Map<Project>(paraObject);

            await context.Project.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Project>(context);

            Logger.LogInformation("Project created successfully. ProjectId={ProjectId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create project. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogInformation("Updating project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project? currentItem = await context.Project
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Project update rejected because record was not found. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的專案資料。");
            }

            Project itemData = Mapper.Map<Project>(paraObject);
            CleanTrackingHelper.Clean<Project>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Project>(context);

            Logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", itemData.Id, itemData.Title);
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
        Logger.LogInformation("Deleting project. ProjectId={ProjectId}", id);

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project? item = await context.Project
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Project deletion rejected because record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的專案資料。");
            }

            CleanTrackingHelper.Clean<Project>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Project>(context);

            Logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除專案失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for project. Title={Title}", paraObject.Title);
        return ValidateBusinessRulesAsync(paraObject);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        CleanTrackingHelper.Clean<Project>(context);
        Project? searchItem = await context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because project was not found. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的專案資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private Task<VerifyRecordResult> ValidateBusinessRulesAsync(ProjectAdapterModel paraObject)
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

        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }
}
