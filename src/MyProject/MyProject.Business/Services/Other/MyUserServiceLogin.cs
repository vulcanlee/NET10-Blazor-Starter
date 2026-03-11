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
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;

    public IMapper Mapper { get; }
    public IConfiguration Configuration { get; }
    public ILogger<MyUserServiceLogin> Logger { get; }

    public MyUserServiceLogin(
        BackendDBContext context,
        IMapper mapper,
        IConfiguration configuration,
        ILogger<MyUserServiceLogin> logger,
        RolePermissionService rolePermissionService)
    {
        this.context = context;
        Mapper = mapper;
        Configuration = configuration;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
    }

    public async Task<(string, MyUser)> LoginAsync(string username, string password)
    {
        Logger.LogInformation("Login attempt started for Account={Account}.", username);

        try
        {
            MyUser item = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Account == username);

            if (item is null)
            {
                Logger.LogWarning("Login failed because account was not found. Account={Account}", username);
                return ("帳號或者密碼不正確", null);
            }

            string hashPassword = PasswordHelper.GetPasswordSHA(item.Salt, password);
            if (item.Password != hashPassword)
            {
                Logger.LogWarning("Login failed because password validation failed. Account={Account}, UserId={UserId}", username, item.Id);
                return ("帳號或者密碼不正確", null);
            }

            Logger.LogInformation("Login validation succeeded for Account={Account}, UserId={UserId}.", username, item.Id);
            return (string.Empty, item);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login attempt failed unexpectedly for Account={Account}.", username);
            throw;
        }
    }
}
