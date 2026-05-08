using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Business.Services.DataAccess;

public class MyUserService
{
    private readonly BackendDBContext context;

    public IMapper Mapper { get; }
    public ILogger<MyUserService> Logger { get; }

    public MyUserService(BackendDBContext context, IMapper mapper, ILogger<MyUserService> logger)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
    }

    public async Task<DataRequestResult<MyUserAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading users. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<MyUserAdapterModel> result = new();
        IQueryable<MyUser> dataSource = context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Account.Contains(dataRequest.Search) ||
                x.Name.Contains(dataRequest.Search) ||
                (x.Email ?? string.Empty).Contains(dataRequest.Search) ||
                (x.RoleView != null && x.RoleView.Name.Contains(dataRequest.Search)));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(MyUserAdapterModel.Account))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Account).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Account).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.Name))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Name).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.Email))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Email).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Email).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.RoleViewName))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.RoleView != null ? x.RoleView.Name : string.Empty).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.RoleView != null ? x.RoleView.Name : string.Empty).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.StatusText))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.IsAdminText))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.IsAdmin).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.IsAdmin).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.CreateAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreateAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(MyUserAdapterModel.UpdateAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdateAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdateAt).ThenBy(x => x.Id)
                        : dataSource;
            }
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        List<MyUser> records = await dataSource.ToListAsync();
        List<MyUserAdapterModel> adapterModelObjects = Mapper.Map<List<MyUserAdapterModel>>(records);
        foreach (var adapterModelItem in adapterModelObjects)
        {
            await OtherDependencyData(adapterModelItem);
        }

        result.Result = adapterModelObjects;
        Logger.LogDebug("Loaded users successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<MyUserAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading user by id. Id={UserId}", id);

        MyUser? item = await context.MyUser
            .AsNoTracking()
            .Include(x => x.RoleView)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("User not found. Id={UserId}", id);
            return new MyUserAdapterModel();
        }

        MyUserAdapterModel result = Mapper.Map<MyUserAdapterModel>(item);
        await OtherDependencyData(result);
        return result;
    }

    public async Task<List<RoleViewAdapterModel>> GetRoleViewsAsync()
    {
        Logger.LogDebug("Loading role views for user maintenance.");

        List<RoleView> roleViews = await context.RoleView
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync();

        Logger.LogDebug("Loaded role views successfully. Count={Count}", roleViews.Count);
        return Mapper.Map<List<RoleViewAdapterModel>>(roleViews);
    }

    public async Task<VerifyRecordResult> AddAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogInformation("Creating user. Account={Account}, RoleViewId={RoleViewId}", paraObject.Account, paraObject.RoleViewId);

        try
        {
            if (string.IsNullOrWhiteSpace(paraObject.Password))
            {
                Logger.LogWarning("User creation rejected because password is empty. Account={Account}", paraObject.Account);
                return VerifyRecordResultFactory.Build(false, "新增使用者時必須輸入密碼。");
            }

            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser itemParameter = Mapper.Map<MyUser>(paraObject);
            itemParameter.RoleView = null;
            itemParameter.Salt = Guid.NewGuid().ToString();
            itemParameter.Password = PasswordHelper.GetPasswordSHA(itemParameter.Salt, paraObject.Password);

            await context.MyUser.AddAsync(itemParameter);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            Logger.LogInformation("User created successfully. UserId={UserId}, Account={Account}", itemParameter.Id, itemParameter.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create user. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "新增使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogInformation("Updating user. UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);

        try
        {
            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser? currentItem = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("User update rejected because record was not found. UserId={UserId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的使用者資料。");
            }

            MyUser itemData = Mapper.Map<MyUser>(paraObject);
            itemData.RoleView = null;

            if (string.IsNullOrWhiteSpace(paraObject.Password))
            {
                itemData.Password = currentItem.Password;
                itemData.Salt = currentItem.Salt;
            }
            else
            {
                itemData.Salt = string.IsNullOrWhiteSpace(currentItem.Salt) ? Guid.NewGuid().ToString() : currentItem.Salt;
                itemData.Password = PasswordHelper.GetPasswordSHA(itemData.Salt, paraObject.Password);
            }

            CleanTrackingHelper.Clean<MyUser>(context);
            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            Logger.LogInformation("User updated successfully. UserId={UserId}, Account={Account}", itemData.Id, itemData.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update user. UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "修改使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting user. UserId={UserId}", id);

        try
        {
            CleanTrackingHelper.Clean<MyUser>(context);
            MyUser? item = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("User deletion rejected because record was not found. UserId={UserId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的使用者資料。");
            }

            CleanTrackingHelper.Clean<MyUser>(context);
            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<MyUser>(context);

            Logger.LogInformation("User deleted successfully. UserId={UserId}, Account={Account}", id, item.Account);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete user. UserId={UserId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除使用者失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-create validation for Account={Account}", paraObject.Account);

        MyUser? searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == paraObject.Account);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-create validation failed because account already exists. Account={Account}", paraObject.Account);
            return VerifyRecordResultFactory.Build(false, "帳號已存在，無法新增。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-update validation for UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);

        CleanTrackingHelper.Clean<MyUser>(context);
        MyUser? searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because record was not found. UserId={UserId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的使用者資料不存在。");
        }

        searchItem = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == paraObject.Account && x.Id != paraObject.Id);

        if (searchItem != null)
        {
            Logger.LogWarning("Pre-update validation failed because account already exists. Account={Account}, UserId={UserId}", paraObject.Account, paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "帳號已存在，無法修改。");
        }

        return VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(MyUserAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for UserId={UserId}, Account={Account}", paraObject.Id, paraObject.Account);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private Task OtherDependencyData(MyUserAdapterModel data)
    {
        data.Password = string.Empty;
        if (data.RoleView is not null)
        {
            data.RoleViewId = data.RoleView.Id;
        }

        return Task.CompletedTask;
    }

    public async Task<bool> NeedChangePasswordAsync(MyUserAdapterModel myUser)
    {
        Logger.LogDebug("Checking whether user must change password. UserId={UserId}", myUser.Id);

        CleanTrackingHelper.Clean<MyUser>(context);
        var user = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == myUser.Id);

        if (user == null)
        {
            Logger.LogWarning("Cannot check password-change requirement because user was not found. UserId={UserId}", myUser.Id);
            return false;
        }

        string hashPassword = PasswordHelper.GetPasswordSHA(user.Salt ?? string.Empty, MagicObjectHelper.NeedChangePassword);
        bool result = user.Password == hashPassword;

        Logger.LogDebug("Password-change requirement check completed. UserId={UserId}, NeedChangePassword={NeedChangePassword}", myUser.Id, result);
        return result;
    }
}
