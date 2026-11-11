using System.Text.Json;
using AutoMapper;
using Microsoft.Extensions.Logging;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;

namespace MyProject.Business.Services.Other;

/// <summary><see cref="ICurrentUserLoader.LoadAsync"/> 的帳號狀態。</summary>
public enum CurrentUserLoadStatus
{
    /// <summary>帳號存在、啟用、工作階段版本相符。</summary>
    Ok,

    /// <summary>帳號不存在或已刪除。</summary>
    NotFound,

    /// <summary>工作階段版本已換（改密碼、強制登出、改角色等）。</summary>
    SessionRevoked,

    /// <summary>帳號已停用。</summary>
    Disabled,
}

/// <param name="Status">帳號狀態；不是 <see cref="CurrentUserLoadStatus.Ok"/> 時其餘欄位只供稽核參考。</param>
/// <param name="User">資料庫裡的使用者（找不到時為 null）。</param>
/// <param name="CurrentUser">組好的目前使用者（含有效權限與團隊）；權限資料讀不出來時為 null，呼叫端應視為無效帳號。</param>
/// <param name="MustChangePassword">必須先變更密碼。</param>
/// <param name="RequiresTwoFactorSetup">必須先設定兩步驟驗證（必須改密碼時一律為 false：先改密碼，再設定兩步驟驗證）。</param>
/// <param name="HasPrimaryRole">
/// 有沒有主要角色（<c>RoleViewId</c> 可為空）。網頁版一直把沒有主要角色的帳號登出；API 一直可用（權限由 RBAC 表判定），
/// 所以這裡只回報，由呼叫端各自決定，兩邊既有的行為都不變。
/// </param>
public sealed record CurrentUserLoadResult(
    CurrentUserLoadStatus Status,
    MyUserAdapterModel? User,
    CurrentUser? CurrentUser,
    bool MustChangePassword,
    bool RequiresTwoFactorSetup,
    bool HasPrimaryRole = true);

/// <summary>
/// 依使用者 Id 與工作階段版本載入「目前使用者」（0.10.0 起從 Server 版的換頁檢查抽出；0.10.9 起只有 Web API 每個請求使用）。
/// 規則：帳號存在、啟用、版本相符，權限取 RBAC 有效權限、團隊取有效團隊，
/// 以及是否必須先改密碼或設定兩步驟驗證。這裡**只讀不寫**：導頁、稽核與回應由呼叫端決定。
/// </summary>
public interface ICurrentUserLoader
{
    Task<CurrentUserLoadResult> LoadAsync(int userId, string? stampClaim);
}

public sealed class CurrentUserLoader : ICurrentUserLoader
{
    /// <summary>
    /// 工作階段版本的查詢可以沿用幾秒內的結果：同一次換頁裡版面、選單、頁面各檢查一次，不必各查一次資料庫。
    /// 同一個行程裡版本一被改（強制登出、停用、改密碼等），<see cref="SecurityStampInvalidationInterceptor"/> 會立即清掉快取（0.10.0 起），
    /// 所以這段時間只影響別的行程。
    /// </summary>
    private static readonly TimeSpan SessionRecheckWindow = TimeSpan.FromSeconds(5);

    private readonly ILogger<CurrentUserLoader> logger;
    private readonly IMapper mapper;
    private readonly MyUserService myUserService;
    private readonly RolePermissionService rolePermissionService;
    private readonly IEffectiveTeamResolver effectiveTeamResolver;
    private readonly IPermissionChecker permissionChecker;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly ISecurityStampService securityStampService;
    private readonly ITwoFactorService twoFactorService;

    public CurrentUserLoader(
        ILogger<CurrentUserLoader> logger,
        IMapper mapper,
        MyUserService myUserService,
        RolePermissionService rolePermissionService,
        IEffectiveTeamResolver effectiveTeamResolver,
        IPermissionChecker permissionChecker,
        IPasswordPolicy passwordPolicy,
        ISecurityStampService securityStampService,
        ITwoFactorService twoFactorService)
    {
        this.logger = logger;
        this.mapper = mapper;
        this.myUserService = myUserService;
        this.rolePermissionService = rolePermissionService;
        this.effectiveTeamResolver = effectiveTeamResolver;
        this.permissionChecker = permissionChecker;
        this.passwordPolicy = passwordPolicy;
        this.securityStampService = securityStampService;
        this.twoFactorService = twoFactorService;
    }

    public async Task<CurrentUserLoadResult> LoadAsync(int userId, string? stampClaim)
    {
        var myUser = await myUserService.GetAsync(userId);
        if (myUser.Id == 0)
        {
            logger.LogWarning("Current user could not be loaded because UserId={UserId} was not found.", userId);
            return new CurrentUserLoadResult(CurrentUserLoadStatus.NotFound, null, null, false, false, false);
        }

        // ⚠️ 工作階段版本在「帳號存在」之後、其他檢查之前比（0.9.103 起）；這裡只比版本，停用由下一個檢查處理（結果不同）。
        if (!await securityStampService.IsValidAsync(userId, stampClaim, SessionRecheckWindow, requireActive: false))
        {
            logger.LogInformation("Current user could not be loaded because the session was revoked. UserId={UserId}", userId);
            return new CurrentUserLoadResult(CurrentUserLoadStatus.SessionRevoked, myUser, null, false, false);
        }

        if (!myUser.Status)
        {
            logger.LogWarning("Current user could not be loaded because UserId={UserId} is disabled.", userId);
            return new CurrentUserLoadResult(CurrentUserLoadStatus.Disabled, myUser, null, false, false);
        }

        // 0.9.101 起只看旗標與到期時間（不再每次換頁都以 PBKDF2 比對 123456）。
        var mustChangePassword = passwordPolicy.RequiresChange(myUser.Account, myUser.HasLocalPassword, myUser.MustChangePassword, myUser.PasswordChangedAtUtc);

        // 必須使用兩步驟驗證卻還沒設定（0.9.104 起）：在改密碼之後判斷，必須改密碼時不算，否則兩個頁面會來回跳。
        var requiresTwoFactorSetup = !mustChangePassword && !myUser.TwoFactorEnabled && await twoFactorService.IsRequiredAsync(userId);

        var hasPrimaryRole = myUser.RoleView != null;
        if (!hasPrimaryRole)
        {
            logger.LogWarning("User {UserId} does not have a role view assigned.", myUser.Id);
        }

        return new CurrentUserLoadResult(CurrentUserLoadStatus.Ok, myUser, await BuildCurrentUserAsync(myUser), mustChangePassword, requiresTwoFactorSetup, hasPrimaryRole);
    }

    private async Task<CurrentUser?> BuildCurrentUserAsync(MyUserAdapterModel myUser)
    {
        try
        {
            var currentUser = mapper.Map<CurrentUser>(myUser);

            // CurrentUser.CopyFrom 會解析 RoleJson，沒有主要角色時給空清單而不是空字串。
            currentUser.RoleJson = "[]";
            if (myUser.RoleView != null)
            {
                var rolePermission = rolePermissionService.InitializePermissionSetting();
                List<string> permissions = JsonSerializer.Deserialize<List<string>>(myUser.RoleView.TabViewJson) ?? [];
                rolePermissionService.SetPermissionInput(rolePermission, permissions);
                currentUser.RoleJson = myUser.RoleView.TabViewJson;
            }

            currentUser.TeamList = (await effectiveTeamResolver.GetEffectiveTeamNamesAsync(myUser.Id)).ToList();
            currentUser.IsAuthenticated = true;

            // ⚠️ CurrentUser.CopyFrom 會由 RoleJson 重建 RoleList，呼叫端複製之後要再設定 RoleList（見 ApplyTo）。
            // UI 與 API 的權限判定一律以 RBAC 表為單一權威來源（含多角色聯集），TabViewJson 僅保留供角色編輯畫面回填。
            currentUser.RoleList = (await permissionChecker.GetEffectivePermissionKeysAsync(myUser.Id)).ToList();
            return currentUser;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize permission data for UserId={UserId}.", myUser.Id);
            return null;
        }
    }

    /// <summary>把載入結果寫進 <see cref="CurrentUserService"/>（CopyFrom 之後再設定 RoleList，見 <see cref="CurrentUser.CopyFrom"/>）。</summary>
    public static void ApplyTo(CurrentUserService target, CurrentUser loaded)
    {
        var roleList = loaded.RoleList.ToList();
        target.CurrentUser.CopyFrom(loaded);
        target.CurrentUser.RoleList = roleList;
    }
}
