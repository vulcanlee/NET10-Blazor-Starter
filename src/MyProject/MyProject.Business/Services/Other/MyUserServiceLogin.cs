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

    public async Task<(string, MyUser?)> LoginAsync(string username, string password)
    {
        Logger.LogInformation("Login attempt started for Account={Account}.", username);

        try
        {
            MyUser? item = await context.MyUser
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Account == username);

            if (item is null)
            {
                Logger.LogWarning("Login failed because account was not found. Account={Account}", username);
                return ("帳號或者密碼不正確", null);
            }

            PasswordVerificationOutcome outcome = SecurePasswordHasher.VerifyPassword(password, item.Password, item.Salt);
            if (outcome == PasswordVerificationOutcome.Failed)
            {
                Logger.LogWarning("Login failed because password validation failed. Account={Account}, UserId={UserId}", username, item.Id);
                return ("帳號或者密碼不正確", null);
            }

            if (outcome == PasswordVerificationOutcome.SuccessRehashNeeded)
            {
                await UpgradePasswordHashAsync(item.Id, password);
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

    /// <summary>
    /// 將舊 SHA256 格式的密碼在成功登入後自動升級為 PBKDF2；升級失敗不影響登入結果。
    /// </summary>
    private async Task UpgradePasswordHashAsync(int userId, string password)
    {
        try
        {
            MyUser? tracked = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
            if (tracked is null)
            {
                return;
            }

            tracked.Password = SecurePasswordHasher.HashPassword(password);
            await context.SaveChangesAsync();
            Logger.LogInformation("Password hash upgraded to PBKDF2 for UserId={UserId}.", userId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Password hash upgrade failed for UserId={UserId}; login still allowed.", userId);
        }
    }
}
