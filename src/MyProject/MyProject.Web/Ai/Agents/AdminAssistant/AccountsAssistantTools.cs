using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auditing;
using static MyProject.Web.Ai.Agents.AdminAssistant.AdminAssistantToolText;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理「帳號與權限」的唯讀工具（0.9.157 起）。權限沿用各頁的管理員專屬鍵；
/// 使用者、角色、稽核紀錄頁在查詢與檢視時都會寫稽核，這裡用同一個代碼寫，detail 加 <c>via=admin-assistant</c>。
///
/// <para>
/// ⚠️ 使用者資料表沒有「最後登入時間」：一律由稽核的 <c>Login.Success</c>／<c>Login.Sso.Success</c> 推算
/// （<see cref="AuditLogQueryService.GetLastLoginTimesAsync"/>），只看得到稽核保留期間內的登入。
/// 密碼與 Salt 絕不輸出（<see cref="DescribeUser"/> 只挑安全欄位）。
/// </para>
/// </summary>
public sealed class AccountsAssistantTools
{
    public const int MaxUserTake = 50;
    public const int MaxScanUsers = 5000;
    public const int MaxAuditTake = 50;
    public const int AuditDetailMaxLength = 300;
    public const int AuditContentMaxLength = 3000;
    public const int SecuritySampleCount = 5;
    /// <summary>從已刪除清單（關鍵字包含比對）精確找名稱時讀幾筆：要大到短名稱（例如「u」）也不會被其他包含它的名稱擠出去。</summary>
    public const int DeletedSearchPageSize = 5000;
    public const string NoLoginRecord = "無登入紀錄（可能超過稽核保留期間）";

    /// <summary>安全總覽計數的稽核動作（登入失敗、鎖定、2FA 失敗、Google 登入失敗、權限被拒、Agent 工具被擋）。</summary>
    public static readonly IReadOnlyList<string> SecurityActions =
    [
        AuditActions.Login.Failed, AuditActions.Login.LockedOut, AuditActions.Login.TwoFactorFailed,
        AuditActions.Login.SsoFailed, AuditActions.Permission.Denied, AuditActions.Ai.ToolDenied,
    ];

    private readonly MyUserService users;
    private readonly RoleViewService roles;
    private readonly AuditLogQueryService auditLogs;
    private readonly IAuditLogService audit;
    private readonly CurrentUserService currentUser;
    private readonly RolePermissionService rolePermissions;
    private readonly ITwoFactorService twoFactor;
    private readonly IOptionsMonitor<AuditLogSettings>? auditSettings;

    public AccountsAssistantTools(
        MyUserService users,
        RoleViewService roles,
        AuditLogQueryService auditLogs,
        IAuditLogService audit,
        CurrentUserService currentUser,
        RolePermissionService rolePermissions,
        ITwoFactorService twoFactor,
        IOptionsMonitor<AuditLogSettings>? auditSettings = null)
    {
        this.users = users;
        this.roles = roles;
        this.auditLogs = auditLogs;
        this.audit = audit;
        this.currentUser = currentUser;
        this.rolePermissions = rolePermissions;
        this.twoFactor = twoFactor;
        this.auditSettings = auditSettings;
    }

    /// <summary>
    /// 稽核記錄範圍的兩個開關（0.9.174 起，預設都關）。模型要知道「沒有記錄」不等於「沒有發生」：
    /// 瀏覽關閉時查不到誰看過，差異關閉時看不到改了什麼。未注入（單元測試）時等同預設。
    /// </summary>
    private AuditLogSettings AuditScope => auditSettings?.CurrentValue ?? new AuditLogSettings();

    public static AiToolDefinition SearchUsersTool { get; } = Define(
        "search_users", "搜尋使用者", MagicObjectHelper.角色_使用者管理,
        $"搜尋使用者帳號（比對帳號、名稱、Email、主要角色），可依狀態（enabled／disabled／locked／deleted＝已軟刪除）與是否為管理員篩選，最多 {MaxUserTake} 筆。"
        + "回傳帳號、名稱、啟用、管理員、主要角色、是否鎖定、2FA、最後登入（由稽核推算）。",
        tools => tools.SearchUsersAsync);

    public static AiToolDefinition UserDetailTool { get; } = Define(
        "get_user_detail", "使用者明細", MagicObjectHelper.角色_使用者管理,
        "用帳號查一位使用者的明細：主要角色與額外角色、團隊、啟用、管理員、鎖定到何時、2FA、是否必須改密碼、密碼變更時間、最後登入。",
        tools => tools.GetUserDetailAsync);

    public static AiToolDefinition ListRolesTool { get; } = Define(
        "list_roles", "角色清單", MagicObjectHelper.角色_角色管理,
        "列出所有角色：名稱、是否要求 2FA、預設團隊、權限鍵數量；includeDeleted=true 時也列出已軟刪除的角色（deleted=true）。",
        tools => tools.ListRolesAsync);

    public static AiToolDefinition RoleDetailTool { get; } = Define(
        "get_role_detail", "角色明細", MagicObjectHelper.角色_角色管理,
        "用角色名稱查一個角色的權限鍵清單（「頁面」代表該頁全部動作，「頁面:view／create／edit／delete」代表單一動作）、預設團隊與是否要求 2FA。",
        tools => tools.GetRoleDetailAsync);

    public static AiToolDefinition SearchAuditLogsTool { get; } = Define(
        "search_audit_logs", "查詢稽核紀錄", MagicObjectHelper.角色_稽核紀錄,
        $"查詢稽核紀錄（誰在何時對什麼做了什麼、成功與否），可依時間、動作代碼（例如 Login.Failed、User.Update、Permission.Denied）、操作者帳號、成敗、關鍵字篩選；"
        + "查某一筆資料的異動歷程用 targetType＋targetId（精確比對；給了目標且沒指定 action 時不含換頁、搜尋、查看這類瀏覽紀錄，要查誰看過請指定 action，例如 Category.View）。"
        + "瀏覽類（換頁、搜尋、查看）只在系統參數「稽核：記錄瀏覽類事件」開啟時才有紀錄，查不到不代表沒有人看過；回傳的 note 會說明。"
        + $"預設最近 24 小時（有給目標時一年），最多 {MaxAuditTake} 筆，由新到舊。",
        tools => tools.SearchAuditLogsAsync);

    public static AiToolDefinition AuditLogContentTool { get; } = Define(
        "get_audit_log_content", "稽核紀錄內容", MagicObjectHelper.角色_稽核紀錄,
        $"用稽核紀錄編號讀取詳細內容（修改前後差異、查詢條件），超過 {AuditContentMaxLength} 字會截斷。"
        + "修改前後差異只在系統參數「稽核：記錄修改前後差異」開啟時才有；沒有時回傳會說明原因。",
        tools => tools.GetAuditLogContentAsync);

    public static AiToolDefinition AccountSecurityTool { get; } = Define(
        "get_account_security_overview", "帳號安全總覽", MagicObjectHelper.角色_稽核紀錄,
        "巡檢用的帳號安全總覽：帳號總數與停用數、被鎖定的帳號、管理員名單與是否開 2FA、超過 N 天沒登入的啟用帳號，"
        + "以及最近 N 小時登入失敗、鎖定、2FA 失敗、Google 登入失敗、權限被拒、Agent 工具被擋的次數與最近幾筆。",
        tools => tools.GetAccountSecurityOverviewAsync);

    public static IReadOnlyList<AiToolDefinition> ReadTools { get; } =
        [SearchUsersTool, UserDetailTool, ListRolesTool, RoleDetailTool, SearchAuditLogsTool, AuditLogContentTool, AccountSecurityTool];

    // ── 異動工具（0.9.158 起）：每一個都要使用者核准；稽核由 MyUserService／RoleViewService 自己寫，這裡不重複寫。──

    public static AiToolDefinition SetUsersEnabledTool { get; } = DefineMutating(
        "set_users_enabled", "啟用／停用帳號", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        $"批次啟用或停用帳號（一次最多 {AdminAssistantMutation.MaxBatch} 個），逐筆回報成敗。",
        "停用後對方所有已登入的工作階段會失效，啟用後可以立刻登入。support 帳號與你自己不能停用；不能讓系統沒有啟用中的管理員。",
        tools => tools.SetUsersEnabledAsync);

    public static AiToolDefinition UnlockUsersTool { get; } = DefineMutating(
        "unlock_users", "解除帳號鎖定", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        $"批次解除因登入失敗被鎖定的帳號（一次最多 {AdminAssistantMutation.MaxBatch} 個），逐筆回報成敗。",
        "清除登入失敗次數與鎖定時間，對方可以立刻再登入。",
        tools => tools.UnlockUsersAsync);

    public static AiToolDefinition ForceLogoutUsersTool { get; } = DefineMutating(
        "force_logout_users", "強制登出", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        $"批次強制登出帳號（一次最多 {AdminAssistantMutation.MaxBatch} 個）：對方所有已登入的瀏覽器在下一次換頁時被登出。",
        "對方正在進行的操作會中斷、需要重新登入。不能強制登出你自己。",
        tools => tools.ForceLogoutUsersAsync);

    public static AiToolDefinition DeleteUsersTool { get; } = DefineMutating(
        "delete_users", "刪除帳號（可還原）", MagicObjectHelper.角色_使用者管理, PermissionActions.Delete,
        $"批次軟刪除帳號（一次最多 {AdminAssistantMutation.MaxBatch} 個），之後可以用 restore_users 還原。",
        "軟刪除：帳號會從清單消失、無法登入，可在使用者管理的「顯示已刪除」還原。support 與你自己不能刪除；不能刪掉最後一位啟用中的管理員。",
        tools => tools.DeleteUsersAsync);

    public static AiToolDefinition RestoreUsersTool { get; } = DefineMutating(
        "restore_users", "還原已刪除的帳號", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        $"批次還原已軟刪除的帳號（一次最多 {AdminAssistantMutation.MaxBatch} 個），逐筆回報成敗。",
        "帳號在刪除期間若已被別人使用，該筆會還原失敗。",
        tools => tools.RestoreUsersAsync);

    public static AiToolDefinition SetUserAdminTool { get; } = DefineMutating(
        "set_user_admin", "設定管理員身分", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        "把一位使用者設為管理員或取消管理員身分；角色與團隊維持不變。",
        "管理員擁有系統全部權限；身分變更後對方需要重新登入。不能取消你自己的管理員身分，也不能讓系統沒有啟用中的管理員。",
        tools => tools.SetUserAdminAsync);

    public static AiToolDefinition SetUserRolesTool { get; } = DefineMutating(
        "set_user_roles", "變更使用者角色", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        "變更一位使用者的主要角色與（或）額外角色；用角色名稱指定，團隊維持不變。",
        "角色變更後對方需要重新登入才會套用新權限；團隊不變。",
        tools => tools.SetUserRolesAsync);

    public static AiToolDefinition UpdateRolePermissionsTool { get; } = DefineMutating(
        "update_role_permissions", "調整角色權限", MagicObjectHelper.角色_角色管理, PermissionActions.Edit,
        "替一個角色加入或移除權限鍵（「頁面」＝該頁全部動作，「頁面:view／create／edit／delete」＝單一動作），"
        + "回報實際加入、移除與不認得而略過的鍵。",
        "會影響所有使用這個角色的人；權限鍵必須在權限目錄中，不認得的鍵不會套用。",
        tools => tools.UpdateRolePermissionsAsync);

    public static AiToolDefinition DeleteRolesTool { get; } = DefineMutating(
        "delete_roles", "刪除角色（可還原）", MagicObjectHelper.角色_角色管理, PermissionActions.Delete,
        $"批次軟刪除角色（一次最多 {AdminAssistantMutation.MaxBatch} 個），之後可以用 restore_roles 還原。",
        "軟刪除，可在角色管理的「顯示已刪除」還原。預設角色、仍是某人主要角色的角色不能刪除。",
        tools => tools.DeleteRolesAsync);

    public static AiToolDefinition RestoreRolesTool { get; } = DefineMutating(
        "restore_roles", "還原已刪除的角色", MagicObjectHelper.角色_角色管理, PermissionActions.Edit,
        $"批次還原已軟刪除的角色（一次最多 {AdminAssistantMutation.MaxBatch} 個），逐筆回報成敗。",
        "角色名稱在刪除期間若已被別的角色使用，該筆會還原失敗。",
        tools => tools.RestoreRolesAsync);

    public static IReadOnlyList<AiToolDefinition> MutatingTools { get; } =
    [
        SetUsersEnabledTool, UnlockUsersTool, ForceLogoutUsersTool, DeleteUsersTool, RestoreUsersTool,
        SetUserAdminTool, SetUserRolesTool, UpdateRolePermissionsTool, DeleteRolesTool, RestoreRolesTool,
    ];

    // ── 不可復原的工具（0.9.159 起）：核准窗紅字警告並要求輸入「確認」。徹底刪除與重設 2FA 的稽核由服務寫；清空稽核由這裡寫（同稽核紀錄頁）。──

    public static AiToolDefinition PurgeUsersTool { get; } = DefineIrreversible(
        "purge_users", "徹底刪除帳號", MagicObjectHelper.角色_使用者管理, PermissionActions.Delete,
        $"徹底刪除已軟刪除的帳號（一次最多 {AdminAssistantMutation.MaxBatch} 個；還沒軟刪除的請先用 delete_users）。角色與團隊設定一併刪除。",
        "徹底刪除後無法還原，帳號的角色與團隊設定也會一併刪除。只能刪除已在「顯示已刪除」清單中的帳號。",
        tools => tools.PurgeUsersAsync);

    public static AiToolDefinition PurgeRolesTool { get; } = DefineIrreversible(
        "purge_roles", "徹底刪除角色", MagicObjectHelper.角色_角色管理, PermissionActions.Delete,
        $"徹底刪除已軟刪除的角色（一次最多 {AdminAssistantMutation.MaxBatch} 個；還沒軟刪除的請先用 delete_roles）。",
        "徹底刪除後無法還原，權限設定與「額外角色」關聯也會一併刪除。仍是某個帳號（含已刪除帳號）主要角色的角色不能刪除。",
        tools => tools.PurgeRolesAsync);

    public static AiToolDefinition ResetTwoFactorTool { get; } = DefineIrreversible(
        "reset_two_factor", "重設兩步驟驗證", MagicObjectHelper.角色_使用者管理, PermissionActions.Edit,
        $"清除帳號的驗證器設定與備用碼（一次最多 {AdminAssistantMutation.MaxBatch} 個），只對已啟用 2FA 的帳號有效。",
        "驗證器設定與備用碼會被清除、無法復原；對方所有已登入的地方都會登出，下次登入只需要密碼（角色要求 2FA 時會被帶去重新設定）。不能重設你自己的。",
        tools => tools.ResetTwoFactorAsync);

    public static AiToolDefinition ClearAuditLogsTool { get; } = DefineIrreversible(
        "clear_audit_logs", "清空全部稽核紀錄", MagicObjectHelper.角色_稽核紀錄, PermissionActions.Delete,
        "刪除全部稽核紀錄與內容檔（只清除一段期間的請用 custom_purge 的 audit-log）。清空後會留下一筆清空的稽核。",
        "會刪除所有稽核紀錄與內容檔，無法復原；稽核是責任證據，有保存規定時請先確認。清空後會另外留下一筆「清空」的稽核。",
        tools => tools.ClearAuditLogsAsync);

    public static IReadOnlyList<AiToolDefinition> IrreversibleTools { get; } = [PurgeUsersTool, PurgeRolesTool, ResetTwoFactorTool, ClearAuditLogsTool];

    public async Task<string> SearchUsersAsync(
        [Description("關鍵字（帳號、名稱、Email、主要角色名稱）；不填＝全部")] string? keyword = null,
        [Description("狀態：enabled（啟用）、disabled（停用）、locked（被鎖定）、deleted（已軟刪除，可還原或徹底刪除）、any；不填＝any")] string? status = null,
        [Description("true＝只列管理員")] bool? adminOnly = null,
        [Description("最多回傳幾筆，1～50，預設 20")] int take = 20)
    {
        var notes = new List<string>();

        // 已軟刪除的帳號（0.9.159 起）：同使用者管理頁的「顯示已刪除」，徹底刪除或還原前用它確認對象。
        var showDeleted = string.Equals(status?.Trim(), "deleted", StringComparison.OrdinalIgnoreCase);
        var (filtered, statusNote) = showDeleted
            ? await LoadDeletedUsersAsync(keyword, adminOnly, notes)
            : FilterUsers(await LoadUsersAsync(keyword, notes), status, adminOnly, DateTime.UtcNow);
        var count = ClampWithNote(take, 1, MaxUserTake, "筆數", notes);
        var lastLogins = await auditLogs.GetLastLoginTimesAsync();

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.User.Search, "MyUser",
            AuditSearchCondition.Text("keyword", keyword), AuditSearchCondition.Of("showDeleted", showDeleted),
            AuditSearchCondition.Of("via", AuditSource));

        var nowUtc = DateTime.UtcNow;
        return ToJson(new
        {
            note = JoinNotes([statusNote, .. notes]),
            matchedCount = filtered.Count,
            users = filtered.Take(count).Select(user => DescribeUser(user, LastLoginOf(lastLogins, user.Id), nowUtc)),
        });
    }

    public async Task<string> GetUserDetailAsync([Description("帳號（Account），精確比對")] string account)
    {
        var user = await FindUserAsync(account);
        if (user is null)
        {
            return $"找不到帳號「{account}」。可以先用 search_users 搜尋。";
        }

        var (additionalRoleIds, teamNames) = await users.GetUserAssignmentsAsync(user.Id);
        var roleNames = (await users.GetRoleViewsAsync()).ToDictionary(role => role.Id, role => role.Name);
        var lastLogins = await auditLogs.GetLastLoginTimesAsync();

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.User.View, "MyUser",
            user.Id.ToString(CultureInfo.InvariantCulture), $"account={user.Account}; via={AuditSource}");

        return ToJson(new
        {
            user = DescribeUser(user, LastLoginOf(lastLogins, user.Id), DateTime.UtcNow),
            additionalRoles = additionalRoleIds.Select(id => roleNames.GetValueOrDefault(id, $"#{id}")),
            teams = teamNames,
        });
    }

    public async Task<string> ListRolesAsync([Description("true＝也列出已軟刪除的角色（可還原或徹底刪除）")] bool includeDeleted = false)
    {
        var all = (await LoadRolesAsync()).Select(role => (Role: role, Deleted: false)).ToList();
        if (includeDeleted)
        {
            var deleted = await roles.GetDeletedAsync(new DataRequest { CurrentPage = 1, PageSize = DeletedSearchPageSize, Take = 1 });
            all.AddRange(deleted.Result.Select(role => (Role: role, Deleted: true)));
        }

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.Role.Search, "RoleView",
            AuditSearchCondition.Text("keyword", null), AuditSearchCondition.Of("showDeleted", includeDeleted),
            AuditSearchCondition.Of("via", AuditSource));

        return ToJson(new
        {
            count = all.Count,
            roles = all.Select(item => new
            {
                item.Role.Name,
                item.Role.RequireTwoFactor,
                defaultTeams = item.Role.DefaultTeams,
                permissionCount = ParsePermissionKeys(item.Role.TabViewJson).Count,
                deleted = item.Deleted ? true : (bool?)null,
            }),
        });
    }

    public async Task<string> GetRoleDetailAsync([Description("角色名稱，精確比對")] string name)
    {
        var all = await LoadRolesAsync();
        var role = all.FirstOrDefault(item => string.Equals(item.Name, name?.Trim(), StringComparison.Ordinal))
            ?? all.FirstOrDefault(item => string.Equals(item.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (role is null)
        {
            return $"找不到角色「{name}」。現有角色：{string.Join("、", all.Select(item => item.Name))}。";
        }

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Role.View, "RoleView",
            role.Id.ToString(CultureInfo.InvariantCulture), $"name={role.Name}; via={AuditSource}");

        return ToJson(new
        {
            role.Name,
            role.RequireTwoFactor,
            defaultTeams = role.DefaultTeams,
            permissions = ParsePermissionKeys(role.TabViewJson),
        });
    }

    public async Task<string> SearchAuditLogsAsync(
        [Description("起始時間，" + TimeFormatHint + "；不填＝結束時間往前 24 小時（有給 targetType／targetId 時往前一年）")] string? start = null,
        [Description("結束時間，" + TimeFormatHint + "；不填＝現在")] string? end = null,
        [Description("動作代碼，完全相同才符合，例如 Login.Failed、User.Update、Permission.Denied；不填＝全部")] string? action = null,
        [Description("操作者帳號（部分比對）")] string? account = null,
        [Description("true＝只看成功、false＝只看失敗；不填＝全部")] bool? success = null,
        [Description("關鍵字（比對動作、帳號、目標、摘要、IP）")] string? keyword = null,
        [Description("最多回傳幾筆，1～50，預設 20")] int take = 20,
        [Description("目標類型，完全相同才符合：Category（分類）、Project（專案）、Team（團隊）、MyUser（使用者）、RoleView（角色）；查某一筆資料的異動歷程時與 targetId 一起給")] string? targetType = null,
        [Description("目標識別（資料的編號），完全相同才符合，12 不會混到 112")] string? targetId = null)
    {
        // 查某一筆資料是誰改的（0.9.173 起）通常不是最近 24 小時的事：有給目標時預設往前查一年。
        var hasTarget = string.IsNullOrWhiteSpace(targetType) == false || string.IsNullOrWhiteSpace(targetId) == false;
        var defaultSpan = hasTarget ? TimeSpan.FromDays(366) : TimeSpan.FromHours(24);
        var (startTime, endTime, rangeNote) = ParseRange(start, end, DateTime.Now, defaultSpan, TimeSpan.FromDays(366));
        var notes = new List<string>();
        var query = new AuditLogQuery
        {
            StartTime = startTime,
            EndTime = endTime,
            Action = action?.Trim() ?? string.Empty,
            Account = account?.Trim() ?? string.Empty,
            Success = success,
            Keyword = keyword?.Trim() ?? string.Empty,
            TargetType = targetType?.Trim() ?? string.Empty,
            TargetId = targetId?.Trim() ?? string.Empty,
            // 查「某一筆是誰改的」：常被打開來看的資料，查看紀錄會把真正的修改擠出前 20 筆（與資料頁「異動歷程」同規則）。
            ExcludeBrowsing = hasTarget && string.IsNullOrWhiteSpace(action),
            CurrentPage = 1,
            PageSize = ClampWithNote(take, 1, MaxAuditTake, "筆數", notes),
        };

        var result = await auditLogs.GetAsync(query);

        // 查「誰看過」時，瀏覽類沒有記錄的話，0 筆不代表沒有人看過（0.9.174 起預設不記）。
        if (AuditActions.IsBrowsing(query.Action) && AuditScope.RecordBrowsing == false)
        {
            notes.Add("目前沒有記錄瀏覽類事件（換頁、搜尋、查看；系統參數「稽核：記錄瀏覽類事件」關閉中），查不到不代表沒有人看過");
        }

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.Audit.Search, "AuditLog",
            AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime),
            ChoiceCondition("action", query.Action, await auditLogs.GetDistinctActionsAsync(), notes),
            AuditSearchCondition.Of("success", SuccessText(success)),
            AuditSearchCondition.Text("account", query.Account), AuditSearchCondition.Text("ip", null),
            AuditSearchCondition.Text("keyword", query.Keyword),
            // 模型給的文字：目標類型不在已知清單（例如把 MyUser 猜成 User）時只記有沒有填並提醒；目標識別比照稽核頁只記有沒有填。
            ChoiceCondition("targetType", query.TargetType, await auditLogs.GetDistinctTargetTypesAsync(), notes),
            AuditSearchCondition.Text("targetId", query.TargetId),
            AuditSearchCondition.Of("via", AuditSource));

        return ToJson(new
        {
            appliedStart = Format(startTime),
            appliedEnd = Format(endTime),
            note = JoinNotes([rangeNote, .. notes]),
            totalCount = result.Count,
            items = result.Result.Select(DescribeAuditLog),
        });
    }

    public async Task<string> GetAuditLogContentAsync([Description("稽核紀錄編號（search_audit_logs 回傳的 id）")] int id)
    {
        var item = await auditLogs.GetByIdAsync(id);
        if (item is null)
        {
            return $"稽核紀錄 #{id} 不存在。";
        }

        var content = item.ContentFile is null ? null : await auditLogs.GetContentAsync(id);

        // 同稽核紀錄頁：看明細就寫一筆（不管有沒有內容檔），detail 記動作代碼。
        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Audit.View, "AuditLog",
            id.ToString(CultureInfo.InvariantCulture), $"action={item.Action}; via={AuditSource}");

        if (string.IsNullOrWhiteSpace(content) == false)
        {
            return ToJson(new { id, item.Action, content = Truncate(content, AuditContentMaxLength) });
        }

        var reason = item.ContentFile is not null ? "內容檔已被清除或寫檔失敗"
            : AuditActions.CarriesFieldDiff(item.Action)
                ? $"修改前後差異只在系統參數「稽核：記錄修改前後差異」開啟時記錄，目前{(AuditScope.RecordFieldChanges ? "開啟（這筆可能是開啟前寫的）" : "關閉")}"
                : "這筆本來就沒有內容檔";
        return $"稽核紀錄 #{id}（{item.Action}）沒有詳細內容（{reason}）。";
    }

    public async Task<string> GetAccountSecurityOverviewAsync(
        [Description("統計最近幾小時的登入失敗與權限被拒，1～720，預設 24")] int hours = 24,
        [Description("超過幾天沒登入算「長期未登入」，7～3650，預設 90")] int inactiveDays = 90)
    {
        var notes = new List<string>();
        var rangeHours = ClampWithNote(hours, 1, 720, "小時數", notes);
        var days = ClampWithNote(inactiveDays, 7, 3650, "未登入天數", notes);
        var nowLocal = DateTime.Now;
        var startTime = nowLocal.AddHours(-rangeHours);

        var all = await LoadUsersAsync(null, notes);
        var lastLogins = await auditLogs.GetLastLoginTimesAsync();
        var snapshot = BuildAccountSecurity(all, lastLogins, nowLocal, DateTime.UtcNow, days);

        var events = new Dictionary<string, object>();
        foreach (var action in SecurityActions)
        {
            var result = await auditLogs.GetAsync(new AuditLogQuery
            {
                StartTime = startTime,
                EndTime = nowLocal,
                Action = action,
                CurrentPage = 1,
                PageSize = SecuritySampleCount,
            });
            events[action] = new { count = result.Count, recent = result.Result.Select(DescribeAuditLog) };
        }

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.User.Search, "MyUser",
            AuditSearchCondition.Text("keyword", null), AuditSearchCondition.Of("showDeleted", false),
            AuditSearchCondition.Of("via", AuditSource));
        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.Audit.Search, "AuditLog",
            AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", nowLocal),
            AuditSearchCondition.Of("action", string.Join(",", SecurityActions)), AuditSearchCondition.Of("success", string.Empty),
            AuditSearchCondition.Text("account", null), AuditSearchCondition.Text("ip", null),
            AuditSearchCondition.Text("keyword", null), AuditSearchCondition.Of("via", AuditSource));

        return ToJson(new
        {
            appliedStart = Format(startTime),
            appliedEnd = Format(nowLocal),
            inactiveDays = days,
            note = JoinNotes(notes),
            accounts = new { total = snapshot.TotalCount, disabled = snapshot.DisabledCount },
            lockedAccounts = snapshot.LockedAccounts,
            admins = snapshot.Admins,
            adminsWithoutTwoFactor = snapshot.AdminsWithoutTwoFactor,
            inactiveAccounts = snapshot.Inactive.Select(item => new
            {
                item.Account,
                lastLogin = item.LastLogin is { } last ? Format(last) : NoLoginRecord,
            }),
            events,
        });
    }

    public async Task<string> SetUsersEnabledAsync(
        [Description("帳號清單（Account，精確比對），最多 50 個")] string[] accounts,
        [Description("true＝啟用、false＝停用")] bool enabled)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(accounts);
        if (error is not null)
        {
            return error;
        }

        var all = await LoadUsersAsync(null, []);
        var resolved = ResolveUsers(all, items);
        if (enabled == false && CheckLosingAdmins(all, resolved.Select(item => item.User)) is { } adminError)
        {
            return adminError;
        }

        var result = new BatchResult(enabled ? "啟用帳號" : "停用帳號");
        foreach (var (target, user) in resolved)
        {
            if (user is null)
            {
                result.Add(target, false, UserNotFound(target));
            }
            else if (user.Status == enabled)
            {
                result.Add(user.Account, true, enabled ? "原本就是啟用，沒有變更" : "原本就是停用，沒有變更");
            }
            else
            {
                AddOutcome(result, user.Account, await users.SetStatusAsync(user.Id, enabled));
            }
        }

        return ToJson(result.ToOutput());
    }

    public Task<string> UnlockUsersAsync([Description("帳號清單（Account，精確比對），最多 50 個")] string[] accounts)
        => ForEachUserAsync(accounts, "解除鎖定", user => users.UnlockAsync(user.Id));

    public Task<string> ForceLogoutUsersAsync([Description("帳號清單（Account，精確比對），最多 50 個")] string[] accounts)
        => ForEachUserAsync(accounts, "強制登出", user => user.Id == currentUser.CurrentUser.Id
            ? Task.FromResult(VerifyRecordResultFactory.Build(false, "不能強制登出你自己（會連你目前的工作階段一起登出）。"))
            : users.ForceLogoutAsync(user.Id));

    public async Task<string> DeleteUsersAsync([Description("帳號清單（Account，精確比對），最多 50 個")] string[] accounts)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(accounts);
        if (error is not null)
        {
            return error;
        }

        var all = await LoadUsersAsync(null, []);
        var resolved = ResolveUsers(all, items);
        if (CheckLosingAdmins(all, resolved.Select(item => item.User)) is { } adminError)
        {
            return adminError;
        }

        var result = new BatchResult("刪除帳號（可還原）");
        foreach (var (target, user) in resolved)
        {
            if (user is null)
            {
                result.Add(target, false, UserNotFound(target));
            }
            else
            {
                AddOutcome(result, user.Account, await users.DeleteAsync(user.Id));
            }
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> PurgeUsersAsync([Description("已軟刪除的帳號清單（Account，精確比對），最多 50 個")] string[] accounts)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(accounts);
        if (error is not null)
        {
            return error;
        }

        var result = new BatchResult("徹底刪除帳號");
        foreach (var target in items)
        {
            if (await FindDeletedUserAsync(target) is { } user)
            {
                AddOutcome(result, user.Account, await users.PurgeAsync(user.Id));
            }
            else
            {
                result.Add(target, false, $"已刪除的帳號中找不到「{target}」（只能徹底刪除已軟刪除的帳號）");
            }
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> PurgeRolesAsync([Description("已軟刪除的角色名稱清單，精確比對，最多 50 個")] string[] names)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(names);
        if (error is not null)
        {
            return error;
        }

        var result = new BatchResult("徹底刪除角色");
        foreach (var target in items)
        {
            if (await FindDeletedRoleAsync(target) is { } role)
            {
                AddOutcome(result, role.Name, await roles.PurgeAsync(role.Id));
            }
            else
            {
                result.Add(target, false, $"已刪除的角色中找不到「{target}」（只能徹底刪除已軟刪除的角色）");
            }
        }

        return ToJson(result.ToOutput());
    }

    public Task<string> ResetTwoFactorAsync([Description("帳號清單（Account，精確比對），最多 50 個")] string[] accounts)
        => ForEachUserAsync(accounts, "重設兩步驟驗證", user =>
            user.Id == currentUser.CurrentUser.Id
                ? Task.FromResult(VerifyRecordResultFactory.Build(false, "不能重設你自己的兩步驟驗證，請到個人資料頁操作。"))
                : user.TwoFactorEnabled == false
                    ? Task.FromResult(VerifyRecordResultFactory.Build(false, "這個帳號沒有啟用兩步驟驗證，不需要重設。"))
                    : twoFactor.ResetAsync(user.Id));

    public async Task<string> ClearAuditLogsAsync()
    {
        var result = await auditLogs.ClearAllAsync();
        if (result.Success == false)
        {
            return $"沒有清空：{result.Message}";
        }

        // 同稽核紀錄頁：刪完再寫，這一筆才會留在清空後的資料表裡。
        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Audit.ClearAll, "AuditLog", "*",
            $"清空全部稽核紀錄：{result.Message}; via={AuditSource}");
        return $"{result.Message}已另外留下一筆「清空全部稽核紀錄」的稽核。";
    }

    public async Task<string> RestoreUsersAsync([Description("已刪除的帳號清單（Account，精確比對），最多 50 個")] string[] accounts)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(accounts);
        if (error is not null)
        {
            return error;
        }

        var result = new BatchResult("還原帳號");
        foreach (var target in items)
        {
            if (await FindDeletedUserAsync(target) is { } user)
            {
                AddOutcome(result, user.Account, await users.RestoreAsync(user.Id));
            }
            else
            {
                result.Add(target, false, $"已刪除的帳號中找不到「{target}」");
            }
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> SetUserAdminAsync(
        [Description("帳號（Account），精確比對")] string account,
        [Description("true＝設為管理員、false＝取消管理員身分")] bool isAdmin)
    {
        var all = await LoadUsersAsync(null, []);
        var user = PickByName(all, account, item => item.Account);
        if (user is null)
        {
            return UserNotFound(account) + "。可以先用 search_users 搜尋。";
        }

        if (user.IsAdmin == isAdmin)
        {
            return $"「{user.Account}」原本就{(isAdmin ? "是" : "不是")}管理員，沒有變更。";
        }

        if (isAdmin == false)
        {
            if (user.Id == currentUser.CurrentUser.Id)
            {
                return "不能取消你自己的管理員身分，沒有執行。請由另一位管理員處理。";
            }

            if (CheckLosingAdmins(all, [user]) is { } adminError)
            {
                return adminError;
            }
        }

        var outcome = await UpdateUserAsync(user.Id, model => model.IsAdmin = isAdmin);
        if (outcome.Success == false)
        {
            return $"沒有變更：{outcome.Message}";
        }

        return isAdmin
            ? $"已將「{user.Account}」設為管理員（對方需要重新登入才會套用）。"
            : $"已取消「{user.Account}」的管理員身分（對方需要重新登入才會套用）。";
    }

    public async Task<string> SetUserRolesAsync(
        [Description("帳號（Account），精確比對")] string account,
        [Description("新的主要角色名稱；不填＝不變")] string? primaryRole = null,
        [Description("新的額外角色名稱清單（整份取代）；不填＝不變，給空清單＝清除所有額外角色")] string[]? additionalRoles = null)
    {
        if (string.IsNullOrWhiteSpace(primaryRole) && additionalRoles is null)
        {
            return "請至少指定主要角色或額外角色。";
        }

        var user = PickByName(await LoadUsersAsync(null, []), account, item => item.Account);
        if (user is null)
        {
            return UserNotFound(account) + "。可以先用 search_users 搜尋。";
        }

        // 角色一變，服務就換掉工作階段版本：改自己的角色等於把自己強制登出（頁面存檔後會換發 Cookie，工具做不到）。
        if (user.Id == currentUser.CurrentUser.Id)
        {
            return "不能用助理變更你自己的角色（變更後你目前的工作階段會失效），沒有執行。請到「使用者管理」頁自己修改。";
        }

        var roleViews = await users.GetRoleViewsAsync();
        var unknown = new List<string>();
        int? primaryId = null;
        if (string.IsNullOrWhiteSpace(primaryRole) == false)
        {
            primaryId = PickByName(roleViews, primaryRole, role => role.Name)?.Id;
            if (primaryId is null)
            {
                unknown.Add(primaryRole.Trim());
            }
        }

        List<int>? additionalIds = null;
        if (additionalRoles is not null)
        {
            additionalIds = [];
            foreach (var name in additionalRoles.Where(name => string.IsNullOrWhiteSpace(name) == false))
            {
                if (PickByName(roleViews, name, role => role.Name) is { } role)
                {
                    additionalIds.Add(role.Id);
                }
                else
                {
                    unknown.Add(name.Trim());
                }
            }
        }

        if (unknown.Count > 0)
        {
            return $"找不到角色：{string.Join("、", unknown)}，沒有變更。現有角色：{string.Join("、", roleViews.Select(role => role.Name))}。";
        }

        var outcome = await UpdateUserAsync(user.Id, model =>
        {
            model.RoleViewId = primaryId ?? model.RoleViewId;
            model.AdditionalRoleIds = additionalIds?.Distinct().ToList() ?? model.AdditionalRoleIds;
        });
        if (outcome.Success == false)
        {
            return $"沒有變更：{outcome.Message}";
        }

        var names = roleViews.ToDictionary(role => role.Id, role => role.Name);
        string NameOf(int id) => names.GetValueOrDefault(id, $"#{id}");
        var (afterAdditional, _) = await users.GetUserAssignmentsAsync(user.Id);
        var afterPrimary = (await users.GetAsync(user.Id)).RoleViewId;
        return $"已更新「{user.Account}」的角色：主要角色 {(afterPrimary is { } primary ? NameOf(primary) : "（無）")}；"
            + $"額外角色 {(afterAdditional.Count == 0 ? "（無）" : string.Join("、", afterAdditional.Select(NameOf)))}。"
            + "團隊不變；對方需要重新登入才會套用新權限。";
    }

    public async Task<string> UpdateRolePermissionsAsync(
        [Description("角色名稱，精確比對")] string roleName,
        [Description("要加入的權限鍵清單，例如 專案項目、專案項目:edit")] string[]? addKeys = null,
        [Description("要移除的權限鍵清單")] string[]? removeKeys = null)
    {
        var (adds, _) = AdminAssistantMutation.ValidateBatch(addKeys, int.MaxValue);
        var (removes, _) = AdminAssistantMutation.ValidateBatch(removeKeys, int.MaxValue);
        if (adds.Count == 0 && removes.Count == 0)
        {
            return "請至少指定一個要加入或移除的權限鍵。";
        }

        var all = await LoadRolesAsync();
        var found = PickByName(all, roleName, role => role.Name);
        if (found is null)
        {
            return $"找不到角色「{roleName}」，沒有變更。現有角色：{string.Join("、", all.Select(item => item.Name))}。";
        }

        var model = (await roles.GetAsync(found.Id)).Clone();
        var valid = ValidPermissionKeys(model.RolePermission);
        var before = rolePermissions.GetPermissionInput(model.RolePermission);
        var unknown = adds.Concat(removes).Where(key => valid.Contains(key) == false).Distinct(StringComparer.Ordinal).ToList();
        var (next, added, removed, notPresent, alreadyCovered) = ApplyPermissionChanges(
            before, [.. adds.Where(valid.Contains)], [.. removes.Where(valid.Contains)]);

        var applied = false;
        string message;
        if (added.Count == 0 && removed.Count == 0)
        {
            message = "沒有任何需要變更的權限鍵，沒有修改角色。";
        }
        else
        {
            rolePermissions.SetPermissionInput(model.RolePermission, next);
            model.UpdateAt = DateTime.Now;
            var check = await roles.BeforeUpdateCheckAsync(model);
            var outcome = check.Success ? await roles.UpdateAsync(model) : check;
            applied = outcome.Success;
            message = outcome.Success ? "已更新；使用這個角色的人重新登入後套用。" : $"沒有變更：{outcome.Message}";
        }

        return ToJson(new
        {
            role = found.Name,
            applied,
            message,
            added = applied ? added : [],
            removed = applied ? removed : [],
            notPresent = notPresent.Count == 0 ? null : notPresent,
            alreadyCovered = alreadyCovered.Count == 0 ? null : alreadyCovered,
            ignoredUnknownKeys = unknown.Count == 0 ? null : unknown,
            beforeCount = before.Count,
            afterCount = applied ? rolePermissions.GetPermissionInput(model.RolePermission).Count : before.Count,
        });
    }

    public async Task<string> DeleteRolesAsync([Description("角色名稱清單，精確比對，最多 50 個")] string[] names)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(names);
        if (error is not null)
        {
            return error;
        }

        var all = await LoadRolesAsync();
        var result = new BatchResult("刪除角色（可還原）");
        var seen = new HashSet<int>();
        foreach (var target in items)
        {
            if (PickByName(all, target, role => role.Name) is { } role)
            {
                // 只差大小寫、指到同一個角色的名稱只處理一次。
                if (seen.Add(role.Id))
                {
                    AddOutcome(result, role.Name, await roles.DeleteAsync(role.Id));
                }
            }
            else
            {
                result.Add(target, false, $"找不到角色「{target}」");
            }
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> RestoreRolesAsync([Description("已刪除的角色名稱清單，精確比對，最多 50 個")] string[] names)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(names);
        if (error is not null)
        {
            return error;
        }

        var result = new BatchResult("還原角色");
        foreach (var target in items)
        {
            if (await FindDeletedRoleAsync(target) is { } role)
            {
                AddOutcome(result, role.Name, await roles.RestoreAsync(role.Id));
            }
            else
            {
                result.Add(target, false, $"已刪除的角色中找不到「{target}」");
            }
        }

        return ToJson(result.ToOutput());
    }

    private static readonly string[] PermissionActionNames =
        [PermissionActions.View, PermissionActions.Create, PermissionActions.Edit, PermissionActions.Delete];

    /// <summary>
    /// 套用加入與移除（鍵都已確認在目錄內）。頁面鍵代表該頁全部動作，所以：
    /// 移除單一動作而目前是頁面鍵時，展開成其餘動作；加入單一動作而已有頁面鍵時，算「已涵蓋」、不變更。
    /// </summary>
    public static (List<string> Next, List<string> Added, List<string> Removed, List<string> NotPresent, List<string> AlreadyCovered)
        ApplyPermissionChanges(IReadOnlyList<string> before, IReadOnlyList<string> adds, IReadOnlyList<string> removes)
    {
        var current = new List<string>(before);
        List<string> added = [], removed = [], notPresent = [], covered = [];
        foreach (var key in removes)
        {
            if (current.Remove(key))
            {
                removed.Add(key);
            }
            else if (SplitActionKey(key) is { } split && current.Remove(split.Page))
            {
                current.AddRange(PermissionActionNames.Where(action => action != split.Action).Select(action => PermissionKey.For(split.Page, action)));
                removed.Add(key);
            }
            else
            {
                notPresent.Add(key);
            }
        }

        foreach (var key in adds)
        {
            if (current.Contains(key) || (SplitActionKey(key) is { } split && current.Contains(split.Page)))
            {
                covered.Add(key);
            }
            else
            {
                current.Add(key);
                added.Add(key);
            }
        }

        return (current, added, removed, notPresent, covered);
    }

    /// <summary>「頁面:動作」拆成頁面與動作；不是這個形式時回 null。</summary>
    private static (string Page, string Action)? SplitActionKey(string key)
    {
        var index = key.LastIndexOf(':');
        if (index <= 0)
        {
            return null;
        }

        var action = key[(index + 1)..];
        return PermissionActionNames.Contains(action, StringComparer.Ordinal) && PermissionKey.For(key[..index], action) == key
            ? (key[..index], action)
            : null;
    }

    /// <summary>權限目錄認得的鍵：群組名稱、頁面（＝該頁全部動作），以及「頁面:動作」。</summary>
    public static HashSet<string> ValidPermissionKeys(RolePermission permission)
    {
        string[] actions = PermissionActionNames;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in permission.Groups)
        {
            keys.Add(group.Name);
            foreach (var item in group.Permissions)
            {
                keys.Add(item.Name);
                foreach (var action in actions)
                {
                    keys.Add(PermissionKey.For(item.Name, action));
                }
            }
        }

        return keys;
    }

    private static string UserNotFound(string account) => $"找不到帳號「{account}」（或有多個只差大小寫的帳號，請給完全正確的帳號）";

    private static void AddOutcome(BatchResult result, string target, VerifyRecordResult outcome)
        => result.Add(target, outcome.Success, outcome.Success ? null : outcome.Message);

    /// <summary>
    /// 名稱精確比對；找不到時才忽略大小寫，而且只在剛好一筆時採用。帳號與角色名稱區分大小寫（Bob 與 bob 可以並存），
    /// 核准窗只顯示模型給的字串，猜錯就會改到別人。
    /// </summary>
    private static T? PickByName<T>(IEnumerable<T> source, string? name, Func<T, string> nameOf)
        where T : class
    {
        var text = name?.Trim() ?? string.Empty;
        var list = source as IReadOnlyList<T> ?? source.ToList();
        if (list.FirstOrDefault(item => string.Equals(nameOf(item), text, StringComparison.Ordinal)) is { } exact)
        {
            return exact;
        }

        var loose = list.Where(item => string.Equals(nameOf(item), text, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return loose.Count == 1 ? loose[0] : null;
    }

    /// <summary>逐一對應帳號；同一位使用者被列兩次（大小寫不同）只處理第一次。</summary>
    private static List<(string Target, MyUserAdapterModel? User)> ResolveUsers(IReadOnlyList<MyUserAdapterModel> all, IReadOnlyList<string> accounts)
    {
        var seen = new HashSet<int>();
        var resolved = new List<(string Target, MyUserAdapterModel? User)>();
        foreach (var account in accounts)
        {
            var user = PickByName(all, account, item => item.Account);
            if (user is null || seen.Add(user.Id))
            {
                resolved.Add((account, user));
            }
        }

        return resolved;
    }

    /// <summary>這批裡啟用中的管理員全部失去管理能力後，是否還剩至少一位（整批檢查，不能逐筆判斷）。</summary>
    private static string? CheckLosingAdmins(IReadOnlyList<MyUserAdapterModel> all, IEnumerable<MyUserAdapterModel?> targets)
    {
        var losing = targets.OfType<MyUserAdapterModel>().Where(user => user.IsAdmin && user.Status).Select(user => user.Id).ToHashSet();
        return losing.Count == 0 ? null : AdminAssistantMutation.CheckAdminsRemain(all, losing);
    }

    private async Task<string> ForEachUserAsync(string[] accounts, string action, Func<MyUserAdapterModel, Task<VerifyRecordResult>> run)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(accounts);
        if (error is not null)
        {
            return error;
        }

        var result = new BatchResult(action);
        foreach (var (target, user) in ResolveUsers(await LoadUsersAsync(null, []), items))
        {
            if (user is null)
            {
                result.Add(target, false, UserNotFound(target));
            }
            else
            {
                AddOutcome(result, user.Account, await run(user));
            }
        }

        return ToJson(result.ToOutput());
    }

    /// <summary>
    /// 走和使用者管理頁「修改」相同的路徑：讀出最新資料（含版本戳記）→ 帶回額外角色與團隊（否則會被清掉）→
    /// 密碼留空（保留原密碼）→ 修改 → <c>BeforeUpdateCheckAsync</c> → <c>UpdateAsync</c>（服務寫 <c>User.Update</c> 稽核）。
    /// </summary>
    private async Task<VerifyRecordResult> UpdateUserAsync(int id, Action<MyUserAdapterModel> change)
    {
        var model = (await users.GetAsync(id)).Clone();
        if (model.Id != id)
        {
            return VerifyRecordResultFactory.Build(false, "找不到這位使用者，可能已被刪除。");
        }

        var (additionalRoleIds, teamNames) = await users.GetUserAssignmentsAsync(id);
        model.AdditionalRoleIds = additionalRoleIds;
        model.TeamNames = teamNames;
        model.Password = string.Empty;
        model.UpdateAt = DateTime.Now;
        change(model);

        var check = await users.BeforeUpdateCheckAsync(model);
        return check.Success ? await users.UpdateAsync(model) : check;
    }

    /// <summary>依狀態與是否管理員篩選（記憶體內）；狀態無法辨識時視為 any 並回說明。</summary>
    public static (List<MyUserAdapterModel> Users, string? Note) FilterUsers(
        IEnumerable<MyUserAdapterModel> source, string? status, bool? adminOnly, DateTime nowUtc)
    {
        string? note = null;
        var normalized = status?.Trim().ToLowerInvariant();
        Func<MyUserAdapterModel, bool> statusFilter = normalized switch
        {
            null or "" or "any" or "all" => _ => true,
            "enabled" => user => user.Status,
            "disabled" => user => user.Status == false,
            "locked" => user => IsLocked(user, nowUtc),
            _ => _ => true,
        };
        if (normalized is not (null or "" or "any" or "all" or "enabled" or "disabled" or "locked"))
        {
            note = $"狀態「{status}」無法辨識（可用 enabled、disabled、locked、any），改為不篩選狀態。";
        }

        var list = source.Where(statusFilter).Where(user => adminOnly != true || user.IsAdmin).ToList();
        return (list, note);
    }

    /// <summary>給模型看的使用者：只挑安全欄位，<b>絕不</b>含密碼與 Salt。</summary>
    public static object DescribeUser(MyUserAdapterModel user, DateTime? lastLoginLocal, DateTime nowUtc) => new
    {
        account = user.Account,
        name = user.Name,
        email = user.Email,
        enabled = user.Status,
        isAdmin = user.IsAdmin,
        primaryRole = string.IsNullOrEmpty(user.RoleViewName) ? null : user.RoleViewName,
        locked = IsLocked(user, nowUtc),
        lockedUntil = IsLocked(user, nowUtc) ? Format(AuditLogQueryService.ToLocal(user.LockoutEndUtc!.Value)) : null,
        twoFactorEnabled = user.TwoFactorEnabled,
        mustChangePassword = user.MustChangePassword,
        passwordChangedAt = user.PasswordChangedAtUtc is { } changed ? Format(AuditLogQueryService.ToLocal(changed)) : null,
        signIn = user.IsGoogleAccount ? "Google" : "密碼",
        lastLogin = lastLoginLocal is { } last ? Format(last) : NoLoginRecord,
        createdAt = Format(user.CreateAt),
    };

    /// <summary>角色的權限鍵（<c>TabViewJson</c> 是 JSON 字串陣列）；讀不懂時回空清單。</summary>
    public static List<string> ParsePermissionKeys(string? tabViewJson)
    {
        if (string.IsNullOrWhiteSpace(tabViewJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tabViewJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 帳號安全快照：總數、停用、被鎖定、管理員與 2FA、長期未登入（只看啟用中、建立超過 N 天的帳號；
    /// 有登入紀錄的看最後登入，沒有的列為「無紀錄」）。
    /// </summary>
    public static AccountSecuritySnapshot BuildAccountSecurity(
        IReadOnlyList<MyUserAdapterModel> source,
        IReadOnlyDictionary<int, DateTime> lastLogins,
        DateTime nowLocal,
        DateTime nowUtc,
        int inactiveDays)
    {
        var threshold = nowLocal.AddDays(-inactiveDays);
        var inactive = source
            .Where(user => user.Status && user.CreateAt < threshold)
            .Select(user => (User: user, Last: LastLoginOf(lastLogins, user.Id)))
            .Where(item => item.Last is null || item.Last < threshold)
            .Select(item => new InactiveAccount(item.User.Account, item.Last))
            .ToList();

        return new AccountSecuritySnapshot(
            source.Count,
            source.Count(user => user.Status == false),
            source.Where(user => IsLocked(user, nowUtc)).Select(user => user.Account).ToList(),
            source.Where(user => user.IsAdmin).Select(user => new AdminAccount(user.Account, user.TwoFactorEnabled, user.Status, user.IsGoogleAccount)).ToList(),
            inactive);
    }

    private static bool IsLocked(MyUserAdapterModel user, DateTime nowUtc)
        => user.LockoutEndUtc is { } end && DateTime.SpecifyKind(end, DateTimeKind.Utc) > nowUtc;

    private static DateTime? LastLoginOf(IReadOnlyDictionary<int, DateTime> lastLogins, int userId)
        => lastLogins.TryGetValue(userId, out var last) ? last : null;

    private static string SuccessText(bool? success) => success switch
    {
        true => "true",
        false => "false",
        null => string.Empty,
    };

    private static object DescribeAuditLog(AuditLogAdapterModel item) => new
    {
        item.Id,
        time = Format(item.OccurredAt),
        item.Success,
        item.Action,
        actor = item.ActorAccount,
        target = string.IsNullOrEmpty(item.TargetType) ? null : $"{item.TargetType}#{item.TargetId}",
        detail = item.Detail is null ? null : Truncate(item.Detail, AuditDetailMaxLength),
        ip = item.ClientIp,
        hasContent = string.IsNullOrEmpty(item.ContentFile) ? (bool?)null : true,
    };

    /// <summary>全部使用者（排除已刪除）；超過上限時在 <paramref name="notes"/> 註明只檢查了前幾位。</summary>
    private async Task<List<MyUserAdapterModel>> LoadUsersAsync(string? keyword, List<string> notes)
    {
        // ⚠️ MyUserService.GetAsync 只有 Take != 0 時才套用 PageSize（畫面傳 0 代表全部）。
        var result = await users.GetAsync(new DataRequest
        {
            Search = keyword?.Trim() ?? string.Empty,
            CurrentPage = 1,
            PageSize = MaxScanUsers,
            Take = 1,
        });

        if (ScanNote(result.Count, MaxScanUsers, "使用者") is { } note)
        {
            notes.Add(note);
        }

        return result.Result.ToList();
    }

    /// <summary>超過讀取上限時給模型的說明（0.9.160 起共用）；沒超過時回 null。</summary>
    public static string? ScanNote(int total, int limit, string kind)
        => total > limit ? $"{kind}共 {total} 位，只檢查前 {limit} 位" : null;

    /// <summary>已軟刪除的帳號（同使用者管理頁的「顯示已刪除」）；超過上限時在 <paramref name="notes"/> 註明。</summary>
    private async Task<(List<MyUserAdapterModel> Users, string? Note)> LoadDeletedUsersAsync(string? keyword, bool? adminOnly, List<string> notes)
    {
        var deleted = await users.GetDeletedAsync(new DataRequest { Search = keyword?.Trim() ?? string.Empty, CurrentPage = 1, PageSize = MaxScanUsers, Take = 1 });
        if (ScanNote(deleted.Count, MaxScanUsers, "已刪除的帳號") is { } note)
        {
            notes.Add(note);
        }

        return ([.. deleted.Result.Where(user => adminOnly != true || user.IsAdmin)], null);
    }

    private async Task<MyUserAdapterModel?> FindUserAsync(string account)
    {
        var text = account?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        var candidates = await LoadUsersAsync(text, []);
        return candidates.FirstOrDefault(user => string.Equals(user.Account, text, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(user => string.Equals(user.Account, text, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<RoleViewAdapterModel>> LoadRolesAsync()
    {
        var result = await roles.GetAsync(new DataRequest { CurrentPage = 1, PageSize = 1000, Take = 1, SortField = "Name", SortDescending = false });
        return result.Result.ToList();
    }

    private static AiToolDefinition Define(
        string name, string displayName, string permissionPage, string description, Func<AccountsAssistantTools, Delegate> method)
        => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            PermissionPage = permissionPage,
            PermissionAction = PermissionActions.View,
            Create = services => AIFunctionFactory.Create(method(services.GetRequiredService<AccountsAssistantTools>()), name, description),
        };

    private static AiToolDefinition DefineIrreversible(
        string name, string displayName, string permissionPage, string permissionAction, string description, string approvalNote,
        Func<AccountsAssistantTools, Delegate> method)
        => DefineMutating(name, displayName, permissionPage, permissionAction, description, approvalNote, method) with { IsIrreversible = true };

    /// <summary>從已刪除清單精確找帳號（只差大小寫有多筆時不猜）。</summary>
    private async Task<MyUserAdapterModel?> FindDeletedUserAsync(string account)
        => PickByName((await users.GetDeletedAsync(new DataRequest { Search = account, CurrentPage = 1, PageSize = DeletedSearchPageSize, Take = 1 })).Result,
            account, item => item.Account);

    private async Task<RoleViewAdapterModel?> FindDeletedRoleAsync(string name)
        => PickByName((await roles.GetDeletedAsync(new DataRequest { Search = name, CurrentPage = 1, PageSize = DeletedSearchPageSize, Take = 1 })).Result,
            name, item => item.Name);

    private static AiToolDefinition DefineMutating(
        string name, string displayName, string permissionPage, string permissionAction, string description, string approvalNote,
        Func<AccountsAssistantTools, Delegate> method)
        => Define(name, displayName, permissionPage, description, method) with
        {
            PermissionAction = permissionAction,
            IsMutating = true,
            ApprovalNote = approvalNote,
        };
}

/// <summary>帳號安全快照（<see cref="AccountsAssistantTools.BuildAccountSecurity"/>）。</summary>
public sealed record AccountSecuritySnapshot(
    int TotalCount,
    int DisabledCount,
    IReadOnlyList<string> LockedAccounts,
    IReadOnlyList<AdminAccount> Admins,
    IReadOnlyList<InactiveAccount> Inactive)
{
    /// <summary>
    /// 該開而沒開 2FA 的管理員（0.9.159 起）：只算啟用中、用密碼登入的；停用的登不進來，Google 登入由 Google 負責第二道驗證。
    /// </summary>
    public IReadOnlyList<string> AdminsWithoutTwoFactor
        => [.. Admins.Where(admin => admin.TwoFactorEnabled == false && admin.Enabled && admin.UsesGoogle == false).Select(admin => admin.Account)];
}

/// <summary>一位管理員、是否開了兩步驟驗證、是否啟用、是否用 Google 登入。</summary>
public sealed record AdminAccount(string Account, bool TwoFactorEnabled, bool Enabled = true, bool UsesGoogle = false);

/// <summary>長期未登入的帳號；<see cref="LastLogin"/> 為 null 表示稽核保留期間內沒有登入紀錄。</summary>
public sealed record InactiveAccount(string Account, DateTime? LastLogin);
