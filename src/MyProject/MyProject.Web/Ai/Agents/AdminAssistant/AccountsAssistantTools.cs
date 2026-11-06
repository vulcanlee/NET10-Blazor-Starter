using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;
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

    public AccountsAssistantTools(
        MyUserService users,
        RoleViewService roles,
        AuditLogQueryService auditLogs,
        IAuditLogService audit,
        CurrentUserService currentUser)
    {
        this.users = users;
        this.roles = roles;
        this.auditLogs = auditLogs;
        this.audit = audit;
        this.currentUser = currentUser;
    }

    public static AiToolDefinition SearchUsersTool { get; } = Define(
        "search_users", "搜尋使用者", MagicObjectHelper.角色_使用者管理,
        $"搜尋使用者帳號（比對帳號、名稱、Email、主要角色），可依狀態（enabled／disabled／locked）與是否為管理員篩選，最多 {MaxUserTake} 筆。"
        + "回傳帳號、名稱、啟用、管理員、主要角色、是否鎖定、2FA、最後登入（由稽核推算）。",
        tools => tools.SearchUsersAsync);

    public static AiToolDefinition UserDetailTool { get; } = Define(
        "get_user_detail", "使用者明細", MagicObjectHelper.角色_使用者管理,
        "用帳號查一位使用者的明細：主要角色與額外角色、團隊、啟用、管理員、鎖定到何時、2FA、是否必須改密碼、密碼變更時間、最後登入。",
        tools => tools.GetUserDetailAsync);

    public static AiToolDefinition ListRolesTool { get; } = Define(
        "list_roles", "角色清單", MagicObjectHelper.角色_角色管理,
        "列出所有角色：名稱、是否要求 2FA、預設團隊、權限鍵數量。",
        tools => tools.ListRolesAsync);

    public static AiToolDefinition RoleDetailTool { get; } = Define(
        "get_role_detail", "角色明細", MagicObjectHelper.角色_角色管理,
        "用角色名稱查一個角色的權限鍵清單（「頁面」代表該頁全部動作，「頁面:view／create／edit／delete」代表單一動作）、預設團隊與是否要求 2FA。",
        tools => tools.GetRoleDetailAsync);

    public static AiToolDefinition SearchAuditLogsTool { get; } = Define(
        "search_audit_logs", "查詢稽核紀錄", MagicObjectHelper.角色_稽核紀錄,
        $"查詢稽核紀錄（誰在何時對什麼做了什麼、成功與否），可依時間、動作代碼（例如 Login.Failed、User.Update、Permission.Denied）、操作者帳號、成敗、關鍵字篩選。"
        + $"預設最近 24 小時，最多 {MaxAuditTake} 筆，由新到舊。",
        tools => tools.SearchAuditLogsAsync);

    public static AiToolDefinition AuditLogContentTool { get; } = Define(
        "get_audit_log_content", "稽核紀錄內容", MagicObjectHelper.角色_稽核紀錄,
        $"用稽核紀錄編號讀取詳細內容（修改前後差異、查詢條件），超過 {AuditContentMaxLength} 字會截斷。",
        tools => tools.GetAuditLogContentAsync);

    public static AiToolDefinition AccountSecurityTool { get; } = Define(
        "get_account_security_overview", "帳號安全總覽", MagicObjectHelper.角色_稽核紀錄,
        "巡檢用的帳號安全總覽：帳號總數與停用數、被鎖定的帳號、管理員名單與是否開 2FA、超過 N 天沒登入的啟用帳號，"
        + "以及最近 N 小時登入失敗、鎖定、2FA 失敗、Google 登入失敗、權限被拒、Agent 工具被擋的次數與最近幾筆。",
        tools => tools.GetAccountSecurityOverviewAsync);

    public static IReadOnlyList<AiToolDefinition> ReadTools { get; } =
        [SearchUsersTool, UserDetailTool, ListRolesTool, RoleDetailTool, SearchAuditLogsTool, AuditLogContentTool, AccountSecurityTool];

    public async Task<string> SearchUsersAsync(
        [Description("關鍵字（帳號、名稱、Email、主要角色名稱）；不填＝全部")] string? keyword = null,
        [Description("狀態：enabled（啟用）、disabled（停用）、locked（被鎖定）、any；不填＝any")] string? status = null,
        [Description("true＝只列管理員")] bool? adminOnly = null,
        [Description("最多回傳幾筆，1～50，預設 20")] int take = 20)
    {
        var notes = new List<string>();
        var all = await LoadUsersAsync(keyword, notes);
        var (filtered, statusNote) = FilterUsers(all, status, adminOnly, DateTime.UtcNow);
        var count = ClampWithNote(take, 1, MaxUserTake, "筆數", notes);
        var lastLogins = await auditLogs.GetLastLoginTimesAsync();

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.User.Search, "MyUser",
            AuditSearchCondition.Text("keyword", keyword), AuditSearchCondition.Of("showDeleted", false),
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

    public async Task<string> ListRolesAsync()
    {
        var all = await LoadRolesAsync();

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.Role.Search, "RoleView",
            AuditSearchCondition.Text("keyword", null), AuditSearchCondition.Of("showDeleted", false),
            AuditSearchCondition.Of("via", AuditSource));

        return ToJson(new
        {
            count = all.Count,
            roles = all.Select(role => new
            {
                role.Name,
                role.RequireTwoFactor,
                defaultTeams = role.DefaultTeams,
                permissionCount = ParsePermissionKeys(role.TabViewJson).Count,
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
        [Description("起始時間，" + TimeFormatHint + "；不填＝結束時間往前 24 小時")] string? start = null,
        [Description("結束時間，" + TimeFormatHint + "；不填＝現在")] string? end = null,
        [Description("動作代碼，完全相同才符合，例如 Login.Failed、User.Update、Permission.Denied；不填＝全部")] string? action = null,
        [Description("操作者帳號（部分比對）")] string? account = null,
        [Description("true＝只看成功、false＝只看失敗；不填＝全部")] bool? success = null,
        [Description("關鍵字（比對動作、帳號、目標、摘要、IP）")] string? keyword = null,
        [Description("最多回傳幾筆，1～50，預設 20")] int take = 20)
    {
        var (startTime, endTime, rangeNote) = ParseRange(start, end, DateTime.Now, TimeSpan.FromHours(24), TimeSpan.FromDays(366));
        var notes = new List<string>();
        var query = new AuditLogQuery
        {
            StartTime = startTime,
            EndTime = endTime,
            Action = action?.Trim() ?? string.Empty,
            Account = account?.Trim() ?? string.Empty,
            Success = success,
            Keyword = keyword?.Trim() ?? string.Empty,
            CurrentPage = 1,
            PageSize = ClampWithNote(take, 1, MaxAuditTake, "筆數", notes),
        };

        var result = await auditLogs.GetAsync(query);

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.Audit.Search, "AuditLog",
            AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime),
            ChoiceCondition("action", query.Action, await auditLogs.GetDistinctActionsAsync(), notes),
            AuditSearchCondition.Of("success", SuccessText(success)),
            AuditSearchCondition.Text("account", query.Account), AuditSearchCondition.Text("ip", null),
            AuditSearchCondition.Text("keyword", query.Keyword), AuditSearchCondition.Of("via", AuditSource));

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
        var content = await auditLogs.GetContentAsync(id);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"稽核紀錄 #{id} 沒有詳細內容（或編號不存在）。";
        }

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Audit.View, "AuditLog",
            id.ToString(CultureInfo.InvariantCulture), $"via={AuditSource}");

        return ToJson(new { id, content = Truncate(content, AuditContentMaxLength) });
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
            adminsWithoutTwoFactor = snapshot.Admins.Where(admin => admin.TwoFactorEnabled == false).Select(admin => admin.Account),
            inactiveAccounts = snapshot.Inactive.Select(item => new
            {
                item.Account,
                lastLogin = item.LastLogin is { } last ? Format(last) : NoLoginRecord,
            }),
            events,
        });
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
            source.Where(user => user.IsAdmin).Select(user => new AdminAccount(user.Account, user.TwoFactorEnabled)).ToList(),
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

        if (result.Count > MaxScanUsers)
        {
            notes.Add($"使用者共 {result.Count} 位，只檢查前 {MaxScanUsers} 位");
        }

        return result.Result.ToList();
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
}

/// <summary>帳號安全快照（<see cref="AccountsAssistantTools.BuildAccountSecurity"/>）。</summary>
public sealed record AccountSecuritySnapshot(
    int TotalCount,
    int DisabledCount,
    IReadOnlyList<string> LockedAccounts,
    IReadOnlyList<AdminAccount> Admins,
    IReadOnlyList<InactiveAccount> Inactive);

/// <summary>一位管理員與是否開了兩步驟驗證。</summary>
public sealed record AdminAccount(string Account, bool TwoFactorEnabled);

/// <summary>長期未登入的帳號；<see cref="LastLogin"/> 為 null 表示稽核保留期間內沒有登入紀錄。</summary>
public sealed record InactiveAccount(string Account, DateTime? LastLogin);
