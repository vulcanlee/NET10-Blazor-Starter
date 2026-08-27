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

public class TeamService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;

    public IMapper Mapper { get; }
    public ILogger<TeamService> Logger { get; }

    public TeamService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<TeamService> logger)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
    }

    public async Task<DataRequestResult<TeamAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading teams. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<TeamAdapterModel> result = new();
        IQueryable<Team> dataSource = context.Team.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Code != null && x.Code.Contains(dataRequest.Search)) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        IOrderedQueryable<Team>? sorted = null;

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(TeamAdapterModel.Name))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.Code))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Code).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Code).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.IsEnabled))
            {
                sorted = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsEnabled).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsEnabled).ThenBy(x => x.Id)
                        : null;
            }
            else if (dataRequest.SortField == nameof(TeamAdapterModel.UpdatedAt))
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

        List<Team> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<TeamAdapterModel>>(records);
        Logger.LogDebug("Loaded teams successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<TeamAdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Loading team by id. TeamId={TeamId}", id);

        Team? item = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogInformation("Team not found. TeamId={TeamId}", id);
            return new TeamAdapterModel();
        }

        return Mapper.Map<TeamAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Creating team. Name={TeamName}", paraObject.Name);

        try
        {
            Team itemParameter = Mapper.Map<Team>(paraObject);
            itemParameter.CreatedAt = DateTime.Now;
            itemParameter.UpdatedAt = DateTime.Now;

            await context.Team.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            Logger.LogInformation("Team created successfully. TeamId={TeamId}, Name={TeamName}", itemParameter.Id, itemParameter.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create team. Name={TeamName}", paraObject.Name);

            // 前置檢查與寫入不在同一個交易裡，唯一索引是最後一道防線；
            // 命中時要給明確訊息，不要被泛用的「新增團隊失敗。」蓋掉。
            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            return VerifyRecordResultFactory.Build(false, "新增團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Updating team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

        try
        {
            Team? item = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (item == null)
            {
                Logger.LogWarning("Team update rejected because record was not found. TeamId={TeamId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的團隊資料。");
            }

            Team itemData = Mapper.Map<Team>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();

            Logger.LogInformation("Team updated successfully. TeamId={TeamId}, Name={TeamName}", itemData.Id, itemData.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

            if (UniqueConstraintHelper.TryGetFriendlyMessage(ex, out var conflictMessage))
            {
                return VerifyRecordResultFactory.Build(false, conflictMessage, ex);
            }

            return VerifyRecordResultFactory.Build(false, "修改團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Deleting team. TeamId={TeamId}", id);

        try
        {
            Team? item = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Team deletion rejected because record was not found. TeamId={TeamId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的團隊資料。");
            }

            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();

            Logger.LogInformation("Team deleted successfully. TeamId={TeamId}, Name={TeamName}", id, item.Name);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete team. TeamId={TeamId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除團隊失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-create validation for team. Name={TeamName}", paraObject.Name);

        var name = NameNormalizer.Normalize(paraObject.Name);
        var nameItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower());

        if (nameItem != null)
        {
            Logger.LogInformation("Pre-create validation failed because team name already exists. Name={TeamName}", paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "團隊名稱已存在，無法新增。");
        }

        // 用 `is { } code` 取得不可為 null 的 string：外層的 null 檢查流程狀態
        // 不會延伸到底下的查詢 lambda 內。
        if (NameNormalizer.NormalizeOptional(paraObject.Code) is { } code)
        {
            var codeItem = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code != null && x.Code.ToLower() == code.ToLower());

            if (codeItem != null)
            {
                Logger.LogInformation("Pre-create validation failed because team code already exists. Code={TeamCode}", paraObject.Code);
                return VerifyRecordResultFactory.Build(false, "團隊代號已存在，無法新增。");
            }
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(TeamAdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug("Running pre-update validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);

        var searchItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogInformation("Pre-update validation failed because team was not found. TeamId={TeamId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的團隊資料不存在。");
        }

        var name = NameNormalizer.Normalize(paraObject.Name);
        var nameItem = await context.Team
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name.ToLower() == name.ToLower() && x.Id != paraObject.Id);

        if (nameItem != null)
        {
            Logger.LogInformation("Pre-update validation failed because team name already exists. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
            return VerifyRecordResultFactory.Build(false, "團隊名稱已存在，無法修改。");
        }

        // 用 `is { } code` 取得不可為 null 的 string：外層的 null 檢查流程狀態
        // 不會延伸到底下的查詢 lambda 內。
        if (NameNormalizer.NormalizeOptional(paraObject.Code) is { } code)
        {
            var codeItem = await context.Team
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Code != null && x.Code.ToLower() == code.ToLower() && x.Id != paraObject.Id);

            if (codeItem != null)
            {
                Logger.LogInformation("Pre-update validation failed because team code already exists. TeamId={TeamId}, Code={TeamCode}", paraObject.Id, paraObject.Code);
                return VerifyRecordResultFactory.Build(false, "團隊代號已存在，無法修改。");
            }
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(TeamAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for team. TeamId={TeamId}, Name={TeamName}", paraObject.Id, paraObject.Name);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    /// <summary>
    /// 取得所有啟用中的團隊名稱（依名稱排序），供其他頁面下拉選取使用。
    /// </summary>
    public async Task<List<string>> GetAllEnabledNamesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Team
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync();
    }
}
