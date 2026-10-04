using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Business.Services.Other;

public class MyUserServiceLogin
{
    private readonly BackendDBContext context;
    private readonly RolePermissionService rolePermissionService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<LockoutSettings> lockoutOptions;
    private readonly INotificationSender notificationSender;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// 帳號不存在、密碼錯誤、帳號鎖定中一律回這一則（0.9.101 起），不讓人從訊息分辨帳號是否存在。
    /// 登入頁另外固定顯示鎖定規則，被鎖住的人知道可以等、用忘記密碼或找管理員。
    /// </summary>
    public const string InvalidCredentialsMessage = "帳號或密碼不正確，或帳號已被暫時鎖定。";

    public IMapper Mapper { get; }
    public IConfiguration Configuration { get; }
    public ILogger<MyUserServiceLogin> Logger { get; }

    public MyUserServiceLogin(
        BackendDBContext context,
        IMapper mapper,
        IConfiguration configuration,
        ILogger<MyUserServiceLogin> logger,
        RolePermissionService rolePermissionService,
        IAuditLogService auditLogService,
        IOptionsMonitor<LockoutSettings> lockoutOptions,
        INotificationSender notificationSender,
        TimeProvider timeProvider)
    {
        this.context = context;
        Mapper = mapper;
        Configuration = configuration;
        Logger = logger;
        this.rolePermissionService = rolePermissionService;
        this.auditLogService = auditLogService;
        this.lockoutOptions = lockoutOptions;
        this.notificationSender = notificationSender;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// 取回仍可使用的帳號；查無此人或已停用時回 null。
    /// 供 refresh token 換發時重新確認使用者現況（token claim 內的資料可能已過期）。
    /// </summary>
    public async Task<MyUser?> GetActiveUserAsync(int userId)
    {
        MyUser? item = await context.MyUser
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId);

        return item is { Status: true } ? item : null;
    }

    public async Task<(string, MyUser?)> LoginAsync(string username, string password)
    {
        Logger.LogInformation("Login attempt started for Account={Account}.", username);

        try
        {
            MyUser? item = await context.MyUser
                .FirstOrDefaultAsync(x => x.Account == username);

            if (item is null)
            {
                Logger.LogWarning("Login failed because account was not found. Account={Account}", username);
                await auditLogService.WriteAsync(AuditActions.Login.Failed, success: false, actorAccount: username, detail: "帳號不存在");
                return (InvalidCredentialsMessage, null);
            }

            // 停用帳號一律擋在發證之前。
            // 沿革：0.4.34 之前這裡沒有檢查 Status —— UI 路徑靠 AuthenticationStateHelper.Check
            // 在後續每次頁面載入時擋下，但 API 的 /api/Auth/login 會直接發出 JWT，
            // 而 PermissionChecker 與 HasPermissionAttribute 也都不看 Status，
            // 等於停用帳號仍可正常呼叫 API。
            if (!item.Status)
            {
                Logger.LogWarning("Login blocked because account is disabled. Account={Account}, UserId={UserId}", username, item.Id);
                await auditLogService.WriteAsync(AuditActions.Login.Disabled, success: false, actorUserId: item.Id, actorAccount: username);
                return ("帳號已停用，請聯絡系統管理員。", null);
            }

            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            bool changed = false;
            if (item.LockoutEndUtc is { } lockoutEndUtc)
            {
                if (lockoutEndUtc > nowUtc)
                {
                    // 鎖定中不驗證密碼、不累加次數；稽核記 Failed（LockedOut 只記造成鎖定的那一次）。
                    Logger.LogWarning("Login blocked because account is locked. Account={Account}, UserId={UserId}, LockoutEndUtc={LockoutEndUtc}", username, item.Id, lockoutEndUtc);
                    await auditLogService.WriteAsync(AuditActions.Login.Failed, success: false, actorUserId: item.Id, actorAccount: username, detail: "reason=Locked");
                    return (InvalidCredentialsMessage, null);
                }

                // ⚠️ 鎖定已到期：次數先歸零再驗證。0.9.100 之前次數留在門檻上，到期後只要再錯一次就立刻又鎖。
                item.AccessFailedCount = 0;
                item.LockoutEndUtc = null;
                changed = true;
            }

            PasswordVerificationOutcome outcome = SecurePasswordHasher.VerifyPassword(password, item.Password, item.Salt);
            if (outcome == PasswordVerificationOutcome.Failed)
            {
                var settings = lockoutOptions.CurrentValue;
                item.AccessFailedCount++;
                bool lockedNow = item.AccessFailedCount >= settings.MaxFailedAttempts;
                if (lockedNow)
                {
                    item.LockoutEndUtc = nowUtc.AddMinutes(settings.LockoutMinutes);
                    Logger.LogWarning("Account locked after too many failed attempts. Account={Account}, UserId={UserId}, LockoutEndUtc={LockoutEndUtc}", username, item.Id, item.LockoutEndUtc);
                }
                else
                {
                    Logger.LogWarning("Login failed because password validation failed. Account={Account}, UserId={UserId}, AccessFailedCount={AccessFailedCount}", username, item.Id, item.AccessFailedCount);
                }

                await context.SaveChangesAsync();
                await auditLogService.WriteAsync(
                    lockedNow ? AuditActions.Login.LockedOut : AuditActions.Login.Failed,
                    success: false, actorUserId: item.Id, actorAccount: username, detail: $"AccessFailedCount={item.AccessFailedCount}");
                if (lockedNow)
                {
                    await NotifyLockedAsync(item, settings.MaxFailedAttempts);
                }

                return (InvalidCredentialsMessage, null);
            }

            if (outcome == PasswordVerificationOutcome.SuccessRehashNeeded)
            {
                item.Password = SecurePasswordHasher.HashPassword(password);
                changed = true;
                Logger.LogInformation("Password hash upgraded to PBKDF2 for UserId={UserId}.", item.Id);
            }

            if (item.AccessFailedCount != 0)
            {
                item.AccessFailedCount = 0;
                changed = true;
            }

            // 重疊回收時舊版程式新增的帳號沒有工作階段版本：補一個，否則登入後的每一次檢查都會判定不符（0.9.103 起）。
            if (string.IsNullOrEmpty(item.SecurityStamp))
            {
                item.SecurityStamp = SecurityStamps.New();
                changed = true;
            }

            // 0.9.100 之前以「密碼是 123456」代表必須變更密碼，既有資料無法以 SQL 轉換（只有雜湊）——
            // 登入成功時看明文，還在用 123456 的人補上旗標，之後一律只看旗標。
            if (!item.MustChangePassword && string.Equals(password, MagicObjectHelper.NeedChangePassword, StringComparison.Ordinal))
            {
                item.MustChangePassword = true;
                changed = true;
                Logger.LogInformation("Legacy default password detected; password change is now required. UserId={UserId}", item.Id);
            }

            if (changed)
            {
                await context.SaveChangesAsync();
            }

            await auditLogService.WriteAsync(AuditActions.Login.Success, success: true, actorUserId: item.Id, actorAccount: item.Account);
            Logger.LogInformation("Login validation succeeded for Account={Account}, UserId={UserId}.", username, item.Id);
            return (string.Empty, item);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Login attempt failed unexpectedly for Account={Account}.", username);
            throw;
        }
    }

    /// <summary>通知全體管理員並寄信；<see cref="INotificationSender"/> 不會丟例外，不影響登入回應。</summary>
    private async Task NotifyLockedAsync(MyUser user, int attempts)
    {
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(user.LockoutEndUtc!.Value, timeProvider.LocalTimeZone);
        await notificationSender.SendAsync(new NotificationRequest(
            NotificationCategories.AccountLocked,
            $"帳號已鎖定：{user.Account}",
            $"連續輸錯密碼 {attempts} 次，鎖定到 {localEnd:yyyy-MM-dd HH:mm}。可在「使用者管理」解鎖，或等鎖定時間結束。",
            "/myusers",
            NotificationTarget.AllAdmins(),
            AlsoEmail: true));
    }
}
