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

        result.Count = dataSource.Count();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        result.Result = Mapper.Map<List<MyTasAdapterModel>>(dataSource);
        await Task.Yield();
        return result;
    }

    public async Task<MyTasAdapterModel> GetAsync(int id)
    {
        MyTas? item = await context.MyTas
            .AsNoTracking()
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == id);

        return item is null
            ? new MyTasAdapterModel()
            : Mapper.Map<MyTasAdapterModel>(item);
    }

    public async Task<List<ProjectAdapterModel>> GetProjectsAsync()
    {
        List<Project> projects = await context.Project
            .AsNoTracking()
            .OrderBy(x => x.Title)
            .ToListAsync();

        return Mapper.Map<List<ProjectAdapterModel>>(projects);
    }

    public async Task<VerifyRecordResult> AddAsync(MyTasAdapterModel paraObject)
    {
        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas itemParameter = Mapper.Map<MyTas>(paraObject);
            itemParameter.Project = null;

            await context.MyTas.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "新增工作記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "新增工作記錄發生例外異常", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MyTasAdapterModel paraObject)
    {
        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas? currentItem = await context.MyTas
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);
            if (currentItem == null)
            {
                return VerifyRecordResultFactory.Build(false, "無法修改工作記錄");
            }

            MyTas itemData = Mapper.Map<MyTas>(paraObject);
            itemData.Project = null;

            CleanTrackingHelper.Clean<MyTas>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "修改工作記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "修改工作記錄發生例外異常", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        try
        {
            CleanTrackingHelper.Clean<MyTas>(context);
            MyTas? item = await context.MyTas
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                return VerifyRecordResultFactory.Build(false, "無法刪除工作記錄");
            }

            CleanTrackingHelper.Clean<MyTas>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyTas>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "刪除工作記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "刪除工作記錄發生例外異常", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(MyTasAdapterModel paraObject)
    {
        return ValidateBusinessRulesAsync(paraObject);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MyTasAdapterModel paraObject)
    {
        CleanTrackingHelper.Clean<MyTas>(context);
        MyTas? searchItem = await context.MyTas
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);
        if (searchItem == null)
        {
            return VerifyRecordResultFactory.Build(false, "要更新的工作記錄已不存在資料庫");
        }

        return await ValidateBusinessRulesAsync(paraObject);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MyTasAdapterModel paraObject)
    {
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private async Task<VerifyRecordResult> ValidateBusinessRulesAsync(MyTasAdapterModel paraObject)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            return VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期");
        }

        if (MyTasAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            return VerifyRecordResultFactory.Build(false, "工作狀態不在允許清單內");
        }

        if (MyTasAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            return VerifyRecordResultFactory.Build(false, "工作優先級不在允許清單內");
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            return VerifyRecordResultFactory.Build(false, "完成百分比必須介於 0 到 100");
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
