using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using System.Text.Json;

namespace MyProject.Business.Services.DataAccess;

public class RoleViewService
{
    #region 欄位與屬性
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;

    public IMapper Mapper { get; }
    public ILogger<RoleViewService> Logger { get; }
    #endregion

    #region 建構式
    public RoleViewService(BackendDBContext context, IMapper mapper,
        ILogger<RoleViewService> logger,
        RolePermissionService rolePermissionService)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
    }
    #endregion

    #region CRUD 服務
    public async Task<DataRequestResult<RoleViewAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        List<RoleViewAdapterModel> data = new();
        DataRequestResult<RoleViewAdapterModel> result = new();
        var DataSource = context.RoleView
            .AsNoTracking();
        #region 進行搜尋動作
        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            DataSource = DataSource
            .Where(x => x.Name.Contains(dataRequest.Search));
        }
        #endregion

        #region 進行排序動作
        Console.WriteLine($"排序 : {dataRequest.SortField}, 方向 : {dataRequest.SortDescending}");
        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(RoleViewAdapterModel.Name))
            {
                if (dataRequest.SortDescending == true)
                {
                    DataSource = DataSource
                        .OrderByDescending(x => x.Name)
                        .ThenByDescending(x => x.Id);
                }
                else if (dataRequest.SortDescending == false)
                {
                    DataSource = DataSource
                        .OrderBy(x => x.Name)
                        .ThenBy(x => x.Id);
                }
            }
            else if (dataRequest.SortField == nameof(RoleViewAdapterModel.CreateAt))
            {
                if (dataRequest.SortDescending == true)
                {
                    DataSource = DataSource
                        .OrderByDescending(x => x.CreateAt)
                        .ThenByDescending(x => x.Id);
                }
                else if (dataRequest.SortDescending == false)
                {
                    DataSource = DataSource
                        .OrderBy(x => x.CreateAt)
                        .ThenBy(x => x.Id);
                }
            }
            else if (dataRequest.SortField == nameof(RoleViewAdapterModel.UpdateAt))
            {
                if (dataRequest.SortDescending == true)
                {
                    DataSource = DataSource
                        .OrderByDescending(x => x.UpdateAt)
                        .ThenByDescending(x => x.Id);
                }
                else if (dataRequest.SortDescending == false)
                {
                    DataSource = DataSource
                        .OrderBy(x => x.UpdateAt)
                        .ThenBy(x => x.Id);
                }
            }
        }
        #endregion

        #region 進行分頁
        // 取得記錄總數量，將要用於分頁元件面板使用
        result.Count = DataSource.Cast<RoleView>().Count();
        DataSource = DataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            DataSource = DataSource.Take(dataRequest.PageSize);
        }
        #endregion

        #region 在這裡進行取得資料與與額外屬性初始化
        List<RoleViewAdapterModel> adapterModelObjects =
            Mapper.Map<List<RoleViewAdapterModel>>(DataSource);

        foreach (var adapterModelItem in adapterModelObjects)
        {
            await OhterDependencyData(adapterModelItem);
        }
        #endregion

        result.Result = adapterModelObjects;
        await Task.Yield();
        return result;
    }

    public async Task<RoleViewAdapterModel> GetAsync(int id)
    {
        RoleView item = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);
        RoleViewAdapterModel result = Mapper.Map<RoleViewAdapterModel>(item);
        await OhterDependencyData(result);
        return result;
    }

    public async Task<VerifyRecordResult> AddAsync(RoleViewAdapterModel paraObject)
    {
        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);
            RoleView itemParameter = Mapper.Map<RoleView>(paraObject);

            #region 對使用者權限做處理
            var permissions = paraObject.RolePermission;
            itemParameter.TabViewJson = rolePermissionService.GetPermissionInputToJson(permissions);
            #endregion

            await context.RoleView
                .AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<RoleView>(context);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "新增記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "新增記錄發生例外異常", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(RoleViewAdapterModel paraObject)
    {
        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);
            RoleView itemData = Mapper.Map<RoleView>(paraObject);

            #region 對使用者權限做處理
            var permissions = paraObject.RolePermission;
            itemData.TabViewJson = rolePermissionService.GetPermissionInputToJson(permissions);
            #endregion

            RoleView item = await context.RoleView
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);
            if (item == null)
            {
                return VerifyRecordResultFactory.Build(false, "無法修改紀錄");
            }
            else
            {
                CleanTrackingHelper.Clean<RoleView>(context);
                context.Entry(itemData).State = EntityState.Modified;
                await context.SaveChangesAsync();
                CleanTrackingHelper.Clean<RoleView>(context);
                return VerifyRecordResultFactory.Build(true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "修改記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "修改記錄發生例外異常", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        try
        {
            CleanTrackingHelper.Clean<RoleView>(context);
            RoleView item = await context.RoleView
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
            {
                return VerifyRecordResultFactory.Build(false, "無法刪除紀錄");
            }
            else
            {
                CleanTrackingHelper.Clean<RoleView>(context);
                context.Entry(item).State = EntityState.Deleted;
                await context.SaveChangesAsync();
                CleanTrackingHelper.Clean<RoleView>(context);
                return VerifyRecordResultFactory.Build(true);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "刪除記錄發生例外異常");
            return VerifyRecordResultFactory.Build(false, "刪除記錄發生例外異常", ex);
        }
    }
    #endregion

    #region CRUD 的限制條件檢查
    public async Task<VerifyRecordResult> BeforeAddCheckAsync(RoleViewAdapterModel paraObject)
    {
        var searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == paraObject.Name);
        if (searchItem != null)
        {
            return VerifyRecordResultFactory.Build(false, "要新增的紀錄已經存在無法新增");
        }
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(RoleViewAdapterModel paraObject)
    {
        CleanTrackingHelper.Clean<RoleView>(context);
        var searchItem = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);
        if (searchItem == null)
        {
            return VerifyRecordResultFactory.Build(false, "要更新的紀錄_發生同時存取衝突_已經不存在資料庫上");
        }

        searchItem = await context.RoleView
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.Name == paraObject.Name &&
           x.Id != paraObject.Id);
        if (searchItem != null)
        {
            return VerifyRecordResultFactory.Build(false, "要修改的紀錄已經存在無法修改");
        }
        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeDeleteCheckAsync(RoleViewAdapterModel paraObject)
    {
        try
        {
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            return VerifyRecordResultFactory.Build(false, "刪除記錄發生例外異常", ex);
        }
    }
    #endregion

    #region 其他服務方法
    Task OhterDependencyData(RoleViewAdapterModel data)
    {
        RolePermission rolePermission = rolePermissionService.InitializePermissionSetting();
        List<string> permissions = new();
        try
        {
            permissions = JsonSerializer.Deserialize<List<string>>(data.TabViewJson);
        }
        catch (Exception ex)
        {
            //Logger.LogWarning(ex, "在進行其他依賴資料處理時發生例外異常");
            permissions = new();
        }
        rolePermissionService
            .SetPermissionInput(rolePermission, permissions);
        data.RolePermission = rolePermission;
        return Task.FromResult(0);
    }

    public async Task<RoleViewAdapterModel> Get預設新建帳號角色Async()
    {
        RoleView item = await context.RoleView
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == MagicObjectHelper.預設角色);
        RoleViewAdapterModel result = Mapper.Map<RoleViewAdapterModel>(item);
        await OhterDependencyData(result);
        return result;
    }

    #endregion
}
