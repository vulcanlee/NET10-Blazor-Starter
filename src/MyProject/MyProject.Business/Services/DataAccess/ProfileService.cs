using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>個人資料頁要顯示的內容（0.9.102 起）。時間一律 UTC。</summary>
public sealed record ProfileInfo(
    int Id,
    string Account,
    string Name,
    string? Email,
    bool IsGoogleAccount,
    bool HasLocalPassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Teams,
    DateTime? PasswordChangedAtUtc,
    DateTime? PasswordExpiresAtUtc,
    bool MustChangePassword,
    string ConcurrencyStamp);

/// <summary>一筆自己的登入或登出紀錄。</summary>
public sealed record LoginRecord(DateTime OccurredAtUtc, string Action, bool Success);

/// <summary>
/// 使用者自己的個人資料（0.9.102 起）：讀取、最近的登入紀錄、修改姓名。
///
/// ⚠️ 只做「本人」：每個方法都以呼叫端傳入的目前使用者 Id 為準，登入紀錄以精確的 <c>ActorUserId</c> 篩選（不用帳號比對 ——
/// 帳號刪除後可能被別人重新使用）。Email、管理員、啟用與鎖定一律不在這裡改。
/// </summary>
public class ProfileService
{
    public const int LoginHistorySize = 20;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<ProfileService> logger;

    public ProfileService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPasswordPolicy passwordPolicy,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<ProfileService> logger)
    {
        this.contextFactory = contextFactory;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.passwordPolicy = passwordPolicy;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    public async Task<ProfileInfo?> GetAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            logger.LogWarning("Profile requested for a user that does not exist. UserId={UserId}", userId);
            return null;
        }

        // 角色＝額外角色 ∪ 主要角色，經 RoleView 排除已刪除的角色（與權限判斷相同的規則）。
        var roleIds = await context.UserRole.Where(x => x.MyUserId == userId).Select(x => x.RoleViewId).ToListAsync();
        if (user.RoleViewId is { } primary)
        {
            roleIds.Add(primary);
        }

        var roles = await context.RoleView.AsNoTracking().Where(x => roleIds.Contains(x.Id)).Select(x => x.Name).OrderBy(x => x).ToListAsync();
        var teams = (await effectiveTeamResolver.GetEffectiveTeamNamesAsync(userId)).Order(StringComparer.Ordinal).ToList();
        var hasLocalPassword = !string.IsNullOrEmpty(user.Password);

        return new ProfileInfo(
            user.Id,
            user.Account,
            user.Name,
            user.Email,
            string.Equals(user.OAuthProvider, "Google", StringComparison.OrdinalIgnoreCase),
            hasLocalPassword,
            roles,
            teams,
            user.PasswordChangedAtUtc,
            passwordPolicy.GetExpiresAtUtc(user.Account, hasLocalPassword, user.PasswordChangedAtUtc),
            user.MustChangePassword,
            user.ConcurrencyStamp);
    }

    /// <summary>自己最近 <see cref="LoginHistorySize"/> 筆登入與登出紀錄（新到舊）。</summary>
    public async Task<List<LoginRecord>> GetRecentLoginsAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.AuditLog.AsNoTracking()
            .Where(x => x.ActorUserId == userId && (x.Action.StartsWith("Login.") || x.Action == AuditActions.Logout))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(LoginHistorySize)
            .Select(x => new LoginRecord(x.OccurredAt, x.Action, x.Success))
            .ToListAsync();
    }

    /// <summary>
    /// 修改自己的姓名。只寫 <c>Name</c>（與更新時間）；以開啟頁面時的版本號比對，管理員同時修改就回衝突。成功後通知右上角更新。
    /// </summary>
    public async Task<VerifyRecordResult> UpdateNameAsync(int userId, string? name, string? expectedStamp)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return VerifyRecordResultFactory.Build(false, "姓名不可空白。");
        }

        await using var context = await contextFactory.CreateDbContextAsync();
        var user = await context.MyUser.FirstOrDefaultAsync(x => x.Id == userId);
        if (user is null)
        {
            logger.LogWarning("Profile update rejected because the user does not exist. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        user.Name = trimmed;
        user.UpdateAt = DateTime.Now;
        var entry = context.Entry(user);
        ConcurrencyStampHelper.Apply(entry, expectedStamp);
        SoftDeleteHelper.ProtectFlags(entry);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Profile update rejected because the record changed. UserId={UserId}", userId);
            return VerifyRecordResultFactory.Build(false, ConcurrencyStampHelper.ConflictMessage);
        }

        // 姓名是個資，稽核與日誌都只記「改了姓名」，不記內容。
        await auditLogService.WriteAsync(
            AuditActions.User.ProfileUpdate, success: true, actorUserId: user.Id, actorAccount: user.Account,
            targetType: nameof(MyUser), targetId: user.Id.ToString(), detail: "field=Name");
        logger.LogInformation("Profile name updated. UserId={UserId}", userId);

        // 右上角的姓名與縮寫立即更新（MainLayout 聽 CurrentUserService.Changed）。
        if (currentUserService.CurrentUser.Id == userId)
        {
            currentUserService.CurrentUser.Name = trimmed;
            currentUserService.NotifyChanged();
        }

        return VerifyRecordResultFactory.Build(true);
    }
}
