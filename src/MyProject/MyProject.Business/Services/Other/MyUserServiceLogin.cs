using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;

namespace MyProject.Business.Services.Other;

public class MyUserServiceLogin
{
    #region 欄位與屬性
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;

    public IMapper Mapper { get; }
    public IConfiguration Configuration { get; }
    public ILogger<MyUserServiceLogin> Logger { get; }
    #endregion

    #region 建構式
    public MyUserServiceLogin(BackendDBContext context, IMapper mapper,
        IConfiguration configuration, ILogger<MyUserServiceLogin> logger,
        RolePermissionService rolePermissionService)
    {
        this.context = context;
        Mapper = mapper;
        Configuration = configuration;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
    }
    #endregion

    #region CRUD 服務
    public async Task<(string, MyUser)> LoginAsync(string username, string password)

    {
        MyUser item = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Account == username);

        if (item != null)
        {
            string hashPassword = PasswordHelper.GetPasswordSHA(item.Salt, password);
            if (item.Password != hashPassword)
            {
                Logger.LogWarning($"使用者 {username} 嘗試登入，但密碼錯誤");
                return ($"帳號或者密碼不正確", null);
            }

            return (string.Empty, item);
        }
        else
        {
            Logger.LogWarning($"使用者 {username} 嘗試登入，但查無此帳號");
            return ($"帳號或者密碼不正確", item);
        }
    }
    #endregion
}
