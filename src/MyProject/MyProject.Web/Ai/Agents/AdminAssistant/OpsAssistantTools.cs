using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Layout;
using MyProject.Web.Components.Views.Admins;
using MyProject.Web.Backup;
using MyProject.Web.Configuration;
using MyProject.Web.Configuration.Parameters;
using MyProject.Web.Dashboard;
using MyProject.Web.DataRetention;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;
using static MyProject.Web.Ai.Agents.AdminAssistant.AdminAssistantToolText;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理「系統設定與維運」的唯讀工具（0.9.155 起排程作業；0.9.157 起加系統參數、公告、備份、資料保留）。
/// 這幾頁讀取時都不寫稽核，這裡也不寫（工具外殼仍會寫 <c>Ai.ToolInvoked</c>）。
/// 系統參數只走 <see cref="SystemParameterManager"/> 的目錄（不含任何金鑰）；孤兒鍵只列鍵名、不列值。
/// </summary>
public sealed class OpsAssistantTools
{
    public const int MaxRunTake = 50;
    public const int RunMessageMaxLength = 500;
    public const int MaxBackupTake = 50;
    public const int AnnouncementContentMaxLength = 300;

    /// <summary>「自訂清除」可預覽的資料種類（同「資料保留與清理」頁）。</summary>
    public static readonly IReadOnlyList<string> PurgeRowKeys =
    [
        "exception-log", "token-usage", "ai-call-log", "agent-conversation", "audit-log", "notification", "job-run",
        "deleted-project", "deleted-category", "deleted-team", "deleted-user", "deleted-role",
    ];

    private readonly ScheduledJobOverviewService overview;
    private readonly SystemParameterManager parameters;
    private readonly AnnouncementService announcements;
    private readonly BackupStore backups;
    private readonly IOptionsMonitor<BackupSettings> backupSettings;
    private readonly DataRetentionOverviewService retention;
    private readonly IAuditLogService audit;
    private readonly CurrentUserService currentUser;
    private readonly AnnouncementCache announcementCache;
    private readonly INotificationSignal signal;

    public OpsAssistantTools(
        ScheduledJobOverviewService overview,
        SystemParameterManager parameters,
        AnnouncementService announcements,
        BackupStore backups,
        IOptionsMonitor<BackupSettings> backupSettings,
        DataRetentionOverviewService retention,
        IAuditLogService audit,
        CurrentUserService currentUser,
        AnnouncementCache announcementCache,
        INotificationSignal signal)
    {
        this.overview = overview;
        this.parameters = parameters;
        this.announcements = announcements;
        this.backups = backups;
        this.backupSettings = backupSettings;
        this.retention = retention;
        this.audit = audit;
        this.currentUser = currentUser;
        this.announcementCache = announcementCache;
        this.signal = signal;
    }

    public static AiToolDefinition SystemParametersTool { get; } = Define(
        "get_system_parameters", "系統參數總覽",
        "列出可在「系統參數」頁調整的設定（不含任何金鑰或密碼）：分組、名稱、目前值、設定檔值、來源（設定檔或覆寫）、問題、生效時機、是否為保留天數。"
        + "可只看某個分組，例如「資料保留」「AI 模型」「AI 用量」。",
        tools => tools.GetSystemParametersAsync, MagicObjectHelper.角色_系統參數);

    public static AiToolDefinition AnnouncementsTool { get; } = Define(
        "list_announcements", "公告清單",
        $"列出公告：編號（修改時用）、標題、內容（截斷 {AnnouncementContentMaxLength} 字）、開始與結束時間、對象（全體、角色或團隊）、建立者。預設只列尚未結束的。",
        tools => tools.ListAnnouncementsAsync, MagicObjectHelper.角色_公告管理);

    public static AiToolDefinition BackupsTool { get; } = Define(
        "list_backups", "備份清單",
        $"列出系統備份檔（由新到舊，預設 10 份，最多 {MaxBackupTake} 份）：檔名、大小、建立時間、觸發方式、遺失檔案數、備份時的系統版本；"
        + "以及保留份數與備份排程的上次和下次執行。",
        tools => tools.ListBackups, MagicObjectHelper.角色_系統備份);

    public static AiToolDefinition RetentionTool { get; } = Define(
        "get_data_retention_overview", "資料保留總覽",
        "列出各類資料（例外、Token 用量、AI 對話紀錄、Agent 對話、稽核、通知、排程紀錄、已刪除資料、備份、日誌檔）的筆數、目錄大小、最舊資料、保留規則與下次預計清除筆數。"
        + "⚠️ 會量測目錄大小，需要幾秒；巡檢時不要呼叫。",
        tools => tools.GetDataRetentionOverviewAsync, MagicObjectHelper.角色_資料保留與清理);

    public static AiToolDefinition PurgePreviewTool { get; } = Define(
        "preview_data_purge", "清除預覽",
        "只計算筆數、不刪除：如果對某類資料做「只留最近 N 天」或「刪除某日之前」，會刪掉幾筆。"
        + $"資料種類：{string.Join("、", PurgeRowKeys)}。",
        tools => tools.PreviewDataPurgeAsync, MagicObjectHelper.角色_資料保留與清理);

    public static AiToolDefinition JobsOverviewTool { get; } = Define(
        "get_scheduled_jobs_overview", "排程總覽",
        "取得所有排程作業的總覽：排程總開關、每個作業的 cron、是否啟用、下次執行、上次執行結果（狀態、時間、耗時、訊息）、"
        + "是否逾期或排隊中，以及需要注意的項目（上次失敗、被中斷、逾期、已停用）。",
        tools => tools.GetScheduledJobsOverviewAsync);

    public static AiToolDefinition JobRecentRunsTool { get; } = Define(
        "get_job_recent_runs", "排程執行紀錄",
        $"取得某個排程作業最近幾次的執行紀錄（由新到舊，最多 {MaxRunTake} 筆），含觸發方式、狀態、開始時間、耗時、訊息與 TraceId"
        + "（可用 TraceId 去查例外與日誌）。",
        tools => tools.GetJobRecentRunsAsync);

    public static IReadOnlyList<AiToolDefinition> ReadTools { get; } =
        [JobsOverviewTool, JobRecentRunsTool, SystemParametersTool, AnnouncementsTool, BackupsTool, RetentionTool, PurgePreviewTool];

    // ── 異動工具（0.9.158 起）：每一個都要使用者核准；稽核同各頁（代碼與 detail 相同），detail 加 via。
    // 會永久刪除資料的動作（縮短保留天數、立即執行清理類排程）這一版一律拒絕，留到里程碑 4。──

    /// <summary>會永久刪除資料的排程作業：這一版不能從助理立即執行（停用、啟用仍可以）。</summary>
    public static readonly IReadOnlySet<string> PurgeJobNames = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditLogRetentionJob.JobName, ExceptionLogRetentionJob.JobName, AiCallLogRetentionJob.JobName,
        AiAgentConversationRetentionJob.JobName, TokenUsageLogRetentionJob.JobName, NotificationRetentionJob.JobName,
        SoftDeletePurgeJob.JobName, AdminPatrolReportRetentionJob.JobName,
    };

    public static AiToolDefinition SetSystemParameterTool { get; } = DefineMutating(
        "set_system_parameter", "修改系統參數", MagicObjectHelper.角色_系統參數, PermissionActions.Edit,
        "修改一個系統參數（用 get_system_parameters 回傳的 key 或名稱指定），值的格式同「系統參數」頁。會縮短保留期限的修改請改用 shorten_retention。",
        "立即寫入並套用（生效時機依參數而定）；之後可以改回設定檔的值。會縮短保留期限的修改要用「縮短保留期限」（紅字核准）。",
        tools => tools.SetSystemParameterAsync) with
    {
        ApprovalPreview = PreviewParameterChangeAsync,
    };

    public static AiToolDefinition ResetSystemParameterTool { get; } = DefineMutating(
        "reset_system_parameter", "系統參數改回設定檔值", MagicObjectHelper.角色_系統參數, PermissionActions.Edit,
        "刪除一個系統參數的覆寫，改回設定檔（appsettings.json）的值。改回後會縮短保留期限時請改用 shorten_retention（value=default）。",
        "改回設定檔（appsettings.json）的值，立即套用。",
        tools => tools.ResetSystemParameterAsync) with
    {
        ApprovalPreview = PreviewParameterChangeAsync,
    };

    public static AiToolDefinition TriggerScheduledJobTool { get; } = DefineMutating(
        "trigger_scheduled_job", "立即執行排程作業", MagicObjectHelper.角色_排程作業, PermissionActions.Edit,
        "把一個排程作業排入立即執行（背景進行）。會永久刪除資料的清理類作業請改用 run_purge_job。",
        "在背景立即執行一次。「系統備份」會依保留份數刪除最舊的備份；清理類作業要用「立即執行清理排程」（紅字核准）。",
        tools => tools.TriggerScheduledJobAsync);

    public static AiToolDefinition SetScheduledJobEnabledTool { get; } = DefineMutating(
        "set_scheduled_job_enabled", "啟用／停用排程作業", MagicObjectHelper.角色_排程作業, PermissionActions.Edit,
        "啟用或停用一個排程作業的自動執行。會永久刪除資料的清理類作業只能停用、不能從這裡啟用。",
        "停用後不再依排程自動執行（仍可手動立即執行）；重新啟用不會補跑停用期間錯過的時段。",
        tools => tools.SetScheduledJobEnabledAsync);

    public static AiToolDefinition CreateAnnouncementTool { get; } = DefineMutating(
        "create_announcement", "新增公告", MagicObjectHelper.角色_公告管理, PermissionActions.Create,
        "新增一則公告（標題最多 100 字、內容最多 2000 字），可指定開始與結束時間，對象可以是全體、某個角色或某個團隊。",
        "公告會在開始時間起顯示在對象畫面的頂端，所有開著的畫面會立即更新。",
        tools => tools.CreateAnnouncementAsync);

    public static AiToolDefinition UpdateAnnouncementTool { get; } = DefineMutating(
        "update_announcement", "修改公告", MagicObjectHelper.角色_公告管理, PermissionActions.Edit,
        "修改一則公告的標題、內容、開始或結束時間（用 list_announcements 回傳的編號指定；只改有給的欄位）。",
        "修改會立即反映在所有人的畫面。",
        tools => tools.UpdateAnnouncementAsync);

    public static IReadOnlyList<AiToolDefinition> MutatingTools { get; } =
    [
        SetSystemParameterTool, ResetSystemParameterTool, TriggerScheduledJobTool, SetScheduledJobEnabledTool,
        CreateAnnouncementTool, UpdateAnnouncementTool,
    ];

    // ── 不可復原的工具（0.9.159 起）：核准窗紅字警告並要求輸入「確認」；稽核同各頁（頁面寫的），成功後才寫並加 via。──

    public static AiToolDefinition DeleteAnnouncementsTool { get; } = DefineIrreversible(
        "delete_announcements", "刪除公告", MagicObjectHelper.角色_公告管理, PermissionActions.Delete,
        $"用公告編號（list_announcements 回傳的 id）刪除公告，一次最多 {AdminAssistantMutation.MaxBatch} 筆。",
        "公告刪除後立即不再顯示，無法復原。",
        tools => tools.DeleteAnnouncementsAsync);

    public static AiToolDefinition DeleteBackupsTool { get; } = DefineIrreversible(
        "delete_backups", "刪除備份", MagicObjectHelper.角色_系統備份, PermissionActions.Delete,
        $"用檔名（list_backups 回傳的 fileName）刪除備份檔，一次最多 {AdminAssistantMutation.MaxBatch} 個。",
        "備份檔刪除後無法復原；刪掉最新的一份時，要等下一次備份才有可還原的版本。",
        tools => tools.DeleteBackupsAsync);

    public static AiToolDefinition RunPurgeJobTool { get; } = DefineIrreversible(
        "run_purge_job", "立即執行清理排程", MagicObjectHelper.角色_排程作業, PermissionActions.Edit,
        "把會永久刪除資料的清理類排程（保留期清理、已刪除資料清除）排入立即執行；其他排程請用 trigger_scheduled_job。",
        "會依目前的保留天數永久刪除超出的資料，無法復原。執行在背景進行，完成後可查執行紀錄看刪了多少。",
        tools => tools.RunPurgeJobAsync);

    public static AiToolDefinition EnablePurgeJobTool { get; } = DefineIrreversible(
        "enable_purge_job", "啟用清理排程", MagicObjectHelper.角色_排程作業, PermissionActions.Edit,
        "啟用一個清理類排程的自動執行；其他排程請用 set_scheduled_job_enabled（停用清理排程也用它）。",
        "啟用後下一個時段就會依保留天數永久刪除資料（包含停用期間累積的），無法復原。",
        tools => tools.EnablePurgeJobAsync);

    public static AiToolDefinition ShortenRetentionTool { get; } = DefineIrreversible(
        "shorten_retention", "縮短保留期限", MagicObjectHelper.角色_系統參數, PermissionActions.Edit,
        "把保留類參數（資料保留天數、備份保留份數）改短（value 給新的值，或 default＝改回設定檔的值且那樣會縮短時）；"
        + "呼叫前先用 get_system_parameters 讀目前值，並在回覆寫出「從多少改成多少」。不會縮短的修改請用 set_system_parameter／reset_system_parameter。",
        "依這個參數的生效時機（保留天數是下一次清理排程、備份份數是下一次備份成功時），永久刪除超出新設定的資料或備份，無法復原。",
        tools => tools.ShortenRetentionAsync) with
    {
        ApprovalPreview = PreviewParameterChangeAsync,
    };

    public static IReadOnlyList<AiToolDefinition> IrreversibleTools { get; } =
        [DeleteAnnouncementsTool, DeleteBackupsTool, RunPurgeJobTool, EnablePurgeJobTool, ShortenRetentionTool];

    /// <summary>自訂清除（0.9.159 起）：依天數或日期清除任一類資料；分派到任何一個專家領域都會載入（「清除舊的稽核紀錄」會分到帳號領域）。</summary>
    public static AiToolDefinition CustomPurgeTool { get; } = DefineIrreversible(
        "custom_purge", "自訂清除", MagicObjectHelper.角色_資料保留與清理, PermissionActions.Delete,
        "依「只留最近 N 天」或「刪除某日之前」永久清除一類資料（同「資料保留與清理」頁的自訂清除），回報刪了幾筆。"
        + $"資料種類：{string.Join("、", PurgeRowKeys)}。執行前請先用 preview_data_purge 預覽筆數。",
        "會永久刪除符合條件的資料，無法復原；不會改到系統參數的保留天數。",
        tools => tools.CustomPurgeAsync);

    public async Task<string> GetSystemParametersAsync(
        [Description("只看某個分組（例如「資料保留」「AI 模型」）；不填＝全部")] string? group = null,
        CancellationToken cancellationToken = default)
        => ToJson(DescribeParameters(await parameters.GetOverviewAsync(cancellationToken), group));

    public async Task<string> ListAnnouncementsAsync([Description("true＝也列出已結束的公告")] bool includeEnded = false)
    {
        var now = DateTime.Now;
        var all = await announcements.GetAllAsync();
        var list = all.Where(item => includeEnded || item.EndAt is null || item.EndAt > now).ToList();
        return ToJson(new
        {
            count = list.Count,
            announcements = list.Select(item => new
            {
                item.Id,
                item.Title,
                content = Truncate(item.Content, AnnouncementContentMaxLength),
                startAt = Format(item.StartAt),
                endAt = Format(item.EndAt),
                showingNow = item.StartAt <= now && (item.EndAt is null || item.EndAt > now),
                target = item.TargetName,
                createdBy = item.CreatedByAccount,
            }),
        });
    }

    public async Task<string> ListBackups([Description("最多幾份，1～50，預設 10")] int take = 10)
    {
        var notes = new List<string>();
        var count = ClampWithNote(take, 1, MaxBackupTake, "份數", notes);
        var job = (await overview.GetOverviewAsync()).Items.FirstOrDefault(item => item.Name == "SystemBackup");
        return ToJson(new
        {
            note = JoinNotes(notes),
            keepCount = backupSettings.CurrentValue.KeepCount,
            backupJob = job is null ? null : new
            {
                job.IsEnabled,
                nextRun = Format(job.NextRunLocal),
                lastRun = DescribeRun(job.LastRun),
            },
            backups = DescribeBackups(backups.List(), count),
        });
    }

    public async Task<string> GetDataRetentionOverviewAsync()
    {
        var result = await retention.GetOverviewAsync();
        return ToJson(new
        {
            result.SchedulingEnabled,
            result.SettingsError,
            rows = result.Rows.Select(row => new
            {
                row.Key,
                row.Label,
                row.Count,
                row.CountUnit,
                folderBytes = row.Folder?.Bytes,
                folderFiles = row.Folder?.Files,
                oldest = Format(row.OldestLocal),
                rule = row.RuleText,
                expectedToPurge = row.Expected,
                row.ExpectedNote,
                schedule = row.ScheduleText,
                row.Error,
            }),
        });
    }

    public async Task<string> PreviewDataPurgeAsync(
        [Description("資料種類，例如 audit-log、exception-log、token-usage、ai-call-log、deleted-user")] string rowKey,
        [Description("只留最近 N 天（1～3650）；與 beforeDate 二選一")] int? keepDays = null,
        [Description("刪除這一天之前的資料（yyyy-MM-dd，不含當天）；與 keepDays 二選一")] string? beforeDate = null)
    {
        var (cutoff, error) = ParsePurgeRequest(rowKey, keepDays, beforeDate, DateOnly.FromDateTime(DateTime.Today));
        if (cutoff is null)
        {
            return error!;
        }

        var count = await retention.PreviewCustomAsync(rowKey.Trim(), cutoff);
        return ToJson(new { rowKey = rowKey.Trim(), condition = cutoff.Describe(), wouldPurge = count, note = "只是預覽，沒有刪除任何資料。" });
    }

    /// <summary>系統參數：只輸出目錄內的鍵（對應成新物件，不序列化含 <see cref="Type"/> 的定義）；孤兒鍵只列鍵名、不列值。</summary>
    public static object DescribeParameters(SystemParameterOverview overview, string? group)
    {
        var groupText = group?.Trim();
        var items = overview.Items.AsEnumerable();
        string? note = null;
        if (string.IsNullOrEmpty(groupText) == false)
        {
            var filtered = overview.Items.Where(item => string.Equals(item.Definition.Group, groupText, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count > 0)
            {
                items = filtered;
            }
            else
            {
                note = $"找不到分組「{groupText}」，改列全部。可用的分組：{string.Join("、", overview.Items.Select(item => item.Definition.Group).Distinct())}。";
            }
        }

        return new
        {
            note,
            parameters = items.Select(item => new
            {
                item.Definition.Group,
                item.Definition.Label,
                item.Definition.Key,
                current = item.EffectiveValue,
                fromFile = item.BaseValue,
                source = item.Source,
                unit = item.Definition.Unit,
                zeroMeaning = item.Definition.ZeroMeaning,
                isRetention = item.Definition.IsRetention ? true : (bool?)null,
                effect = item.Definition.EffectNote,
                problem = item.Problem,
            }),
            orphanKeys = overview.Orphans.Count == 0 ? null : overview.Orphans.Select(orphan => orphan.ParameterKey),
            configErrors = overview.BaseInvalid.Count == 0 ? null : overview.BaseInvalid,
        };
    }

    /// <summary>備份清單：只有檔名（不含根目錄路徑）、大小、時間與 manifest 摘要。</summary>
    public static object DescribeBackups(IReadOnlyList<BackupFileInfo> files, int take) => new
    {
        totalCount = files.Count,
        files = files.Take(take).Select(file => new
        {
            file.FileName,
            sizeBytes = file.SizeBytes,
            createdAt = Format(file.CreatedAtLocal),
            trigger = file.Manifest?.Trigger,
            missingFiles = file.Manifest?.MissingFileCount,
            systemVersion = file.Manifest?.SystemVersion,
        }),
    };

    /// <summary>清除預覽的參數檢查；不合法時回說明（不丟例外）。</summary>
    public static (CustomPurgeCutoff? Cutoff, string? Error) ParsePurgeRequest(string? rowKey, int? keepDays, string? beforeDate, DateOnly today)
    {
        var key = rowKey?.Trim() ?? string.Empty;
        if (PurgeRowKeys.Contains(key) == false)
        {
            return (null, $"資料種類「{rowKey}」不能預覽。可用的種類：{string.Join("、", PurgeRowKeys)}。");
        }

        DateOnly? date = null;
        if (string.IsNullOrWhiteSpace(beforeDate) == false)
        {
            if (DateOnly.TryParseExact(beforeDate.Trim(), ["yyyy-MM-dd", "yyyy/MM/dd"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed) == false)
            {
                return (null, $"日期「{beforeDate}」無法解析（格式 yyyy-MM-dd）。");
            }

            date = parsed;
        }

        var cutoff = new CustomPurgeCutoff(keepDays, date);
        return cutoff.Validate(today) is { } error ? (null, error) : (cutoff, null);
    }

    public async Task<string> GetScheduledJobsOverviewAsync()
    {
        var result = await overview.GetOverviewAsync();
        return ToJson(new
        {
            result.SchedulingEnabled,
            result.SettingsError,
            attention = ScheduledJobAttention.From(result).Select(item => new { item.DisplayName, item.Reason }),
            jobs = result.Items.Select(item => new
            {
                item.Name,
                item.DisplayName,
                item.Cron,
                item.IsEnabled,
                nextRun = Format(item.NextRunLocal),
                lastRun = DescribeRun(item.LastRun),
                lastFinishedRun = item.LastFinishedRun?.Id == item.LastRun?.Id ? null : DescribeRun(item.LastFinishedRun),
                isQueued = item.IsQueued ? true : (bool?)null,
                overdueSince = item.OverdueSlotUtc is { } overdue ? Format(overview.ToLocal(overdue)) : null,
            }),
        });
    }

    public async Task<string> GetJobRecentRunsAsync(
        [Description("排程作業的名稱（Name）或顯示名稱，例如 SystemBackup 或「系統備份」")] string jobName,
        [Description("最多幾筆，1～50，預設 10")] int take = 10)
    {
        var job = ResolveJob(overview.Descriptors, jobName);
        if (job is null)
        {
            return UnknownJobMessage(overview.Descriptors, jobName);
        }

        var notes = new List<string>();
        var runs = await overview.GetRecentRunsAsync(job.Name, ClampWithNote(take, 1, MaxRunTake, "筆數", notes));
        return ToJson(new { job.Name, job.DisplayName, note = JoinNotes(notes), runs = runs.Select(DescribeRun) });
    }

    public async Task<string> SetSystemParameterAsync(
        [Description("參數的 key（例如 LogRetentionSettings:AuditLogDays）或名稱（例如「稽核紀錄保留天數」）")] string key,
        [Description("新的值（數字、true／false 或文字，格式同「系統參數」頁）")] string value,
        CancellationToken cancellationToken = default)
    {
        var item = ResolveParameter((await parameters.GetOverviewAsync(cancellationToken)).Items, key);
        if (item is null)
        {
            return UnknownParameter(key);
        }

        var definition = item.Definition;
        if (SystemParameterValueCodec.TryNormalize(definition, value, out var normalized, out var parseError) == false)
        {
            return $"沒有變更：{parseError}";
        }

        if (definition.IsRetention && WouldShortenRetention(item.EffectiveValue, normalized))
        {
            return ShortensRetentionRefusal(definition, item.EffectiveValue, normalized);
        }

        var result = await parameters.SaveAsync(definition.Key, normalized, item.Override?.ConcurrencyStamp, currentUser.CurrentUser.Account, cancellationToken);
        return await FinishParameterChangeAsync(definition, result);
    }

    public async Task<string> ResetSystemParameterAsync(
        [Description("參數的 key 或名稱")] string key,
        CancellationToken cancellationToken = default)
    {
        var item = ResolveParameter((await parameters.GetOverviewAsync(cancellationToken)).Items, key);
        if (item is null)
        {
            return UnknownParameter(key);
        }

        var definition = item.Definition;
        if (item.Override is not { } row)
        {
            return $"「{definition.Label}」目前沒有系統參數覆寫（用的就是設定檔的值 {SystemParameterValueCodec.Display(definition, item.BaseValue)}），沒有變更。";
        }

        if (definition.IsRetention && WouldShortenRetention(item.EffectiveValue, item.BaseValue))
        {
            return ShortensRetentionRefusal(definition, item.EffectiveValue, item.BaseValue);
        }

        var result = await parameters.ResetAsync(definition.Key, row.ConcurrencyStamp, cancellationToken);
        return await FinishParameterChangeAsync(definition, result);
    }

    public async Task<string> TriggerScheduledJobAsync(
        [Description("排程作業的名稱（Name）或顯示名稱，例如 SystemBackup 或「系統備份」")] string jobName)
    {
        var job = ResolveJob(overview.Descriptors, jobName);
        if (job is null)
        {
            return UnknownJobMessage(overview.Descriptors, jobName);
        }

        if (PurgeJobNames.Contains(job.Name))
        {
            return $"「{job.DisplayName}」是會永久刪除資料的清理作業，沒有用這個工具執行；請改用 run_purge_job（核准窗會以紅字再確認一次）。";
        }

        var user = currentUser.CurrentUser;
        if (overview.TryTrigger(job.Name, user.Account, user.Id) == false)
        {
            return $"「{job.DisplayName}」已經在佇列或執行中，沒有重複排入。請等它完成後用 get_job_recent_runs 查看結果。";
        }

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Job.Trigger, "ScheduledJob", job.Name, $"job={job.Name}; via={AuditSource}");
        return $"已排入「{job.DisplayName}」，會在背景執行；稍後可以用 get_job_recent_runs 查看結果。";
    }

    public async Task<string> SetScheduledJobEnabledAsync(
        [Description("排程作業的名稱（Name）或顯示名稱")] string jobName,
        [Description("true＝啟用、false＝停用")] bool enabled)
    {
        var job = ResolveJob(overview.Descriptors, jobName);
        if (job is null)
        {
            return UnknownJobMessage(overview.Descriptors, jobName);
        }

        // 啟用清理類排程＝下一個時段就永久刪除資料（含停用期間累積的），這一版只能停用。
        if (enabled && PurgeJobNames.Contains(job.Name))
        {
            return $"「{job.DisplayName}」是會永久刪除資料的清理作業，沒有用這個工具啟用；請改用 enable_purge_job（核准窗會以紅字再確認一次）。";
        }

        var current = (await overview.GetOverviewAsync()).Items.FirstOrDefault(item => item.Name == job.Name);
        if (current?.IsEnabled == enabled)
        {
            return $"「{job.DisplayName}」原本就是{(enabled ? "啟用" : "停用")}，沒有變更。";
        }

        await overview.SetEnabledAsync(job.Name, enabled, currentUser.CurrentUser.Account);
        await ViewAudit.WriteAsync(audit, currentUser, enabled ? AuditActions.Job.Enable : AuditActions.Job.Disable,
            "ScheduledJob", job.Name, $"job={job.Name}; via={AuditSource}");
        return enabled
            ? $"已啟用「{job.DisplayName}」，從下一個時段開始依排程執行（不會補跑停用期間錯過的時段）。"
            : $"已停用「{job.DisplayName}」，不會再依排程執行（仍可手動立即執行）。";
    }

    public async Task<string> CreateAnnouncementAsync(
        [Description("標題，最多 100 字")] string title,
        [Description("內容，最多 2000 字")] string content,
        [Description("開始顯示時間，" + TimeFormatHint + "；不填＝現在")] string? start = null,
        [Description("結束時間，" + TimeFormatHint + "；不填＝不結束")] string? end = null,
        [Description("對象：all（全體）、role（角色）、team（團隊）；不填＝all")] string? targetKind = null,
        [Description("對象是角色或團隊時的名稱")] string? targetName = null)
    {
        var (startAt, startError) = ParseTime(start, "開始時間");
        var (endAt, endError) = ParseTime(end, "結束時間", endOfDay: true);
        if ((startError ?? endError) is { } timeError)
        {
            return $"沒有新增：{timeError}";
        }

        var (kind, targetId, targetError) = await ResolveAnnouncementTargetAsync(targetKind, targetName);
        if (targetError is not null)
        {
            return $"沒有新增：{targetError}";
        }

        var now = DateTime.Now;
        var model = new AnnouncementAdapterModel
        {
            Title = title ?? string.Empty,
            Content = content ?? string.Empty,
            StartAt = startAt ?? new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind),
            EndAt = endAt,
            TargetKind = kind,
            TargetId = targetId,
        };
        var result = await announcements.AddAsync(model, currentUser.CurrentUser.Account);
        if (result.Success == false)
        {
            return $"沒有新增：{result.Message}";
        }

        await FinishAnnouncementChangeAsync(AuditActions.Announcement.Create, model);
        return $"已新增公告 #{model.Id}「{model.Title.Trim()}」：{Format(model.StartAt)} 起顯示，"
            + $"{(model.EndAt is { } finish ? $"{Format(finish)} 結束" : "不設結束時間")}，對象{DescribeTarget(kind, targetName)}。";
    }

    public async Task<string> UpdateAnnouncementAsync(
        [Description("公告編號（list_announcements 回傳的 id）")] int id,
        [Description("新標題；不填＝不變")] string? title = null,
        [Description("新內容；不填＝不變")] string? content = null,
        [Description("新的開始時間，" + TimeFormatHint + "；不填＝不變")] string? start = null,
        [Description("新的結束時間，" + TimeFormatHint + "；不填＝不變，填 none＝不設結束時間")] string? end = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
        {
            return "請至少指定一個要修改的欄位（標題、內容、開始或結束時間）。";
        }

        var item = (await announcements.GetAllAsync()).FirstOrDefault(row => row.Id == id);
        if (item is null)
        {
            return $"找不到編號 {id} 的公告，沒有變更。可以先用 list_announcements 查看（加 includeEnded=true 會包含已結束的）。";
        }

        var clearEnd = string.Equals(end?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        var (startAt, startError) = ParseTime(start, "開始時間");
        var (endAt, endError) = clearEnd ? (null, null) : ParseTime(end, "結束時間", endOfDay: true);
        if ((startError ?? endError) is { } timeError)
        {
            return $"沒有變更：{timeError}";
        }

        var model = new AnnouncementAdapterModel
        {
            Id = item.Id,
            Title = string.IsNullOrWhiteSpace(title) ? item.Title : title,
            Content = string.IsNullOrWhiteSpace(content) ? item.Content : content,
            StartAt = startAt ?? item.StartAt,
            EndAt = clearEnd ? null : endAt ?? item.EndAt,
            TargetKind = item.TargetKind,
            TargetId = item.TargetId,
            ConcurrencyStamp = item.ConcurrencyStamp,
        };
        var result = await announcements.UpdateAsync(model);
        if (result.Success == false)
        {
            return $"沒有變更：{result.Message}";
        }

        await FinishAnnouncementChangeAsync(AuditActions.Announcement.Update, model);
        return $"已更新公告 #{model.Id}「{model.Title.Trim()}」：{Format(model.StartAt)} 起顯示，"
            + $"{(model.EndAt is { } finish ? $"{Format(finish)} 結束" : "不設結束時間")}。";
    }

    public async Task<string> DeleteAnnouncementsAsync([Description("公告編號清單（list_announcements 回傳的 id），最多 50 個")] int[] ids)
    {
        var (items, error) = AdminAssistantMutation.ValidateIds(ids);
        if (error is not null)
        {
            return error;
        }

        var all = await announcements.GetAllAsync();
        var result = new BatchResult("刪除公告");
        foreach (var id in items)
        {
            if (all.FirstOrDefault(row => row.Id == id) is not { } item)
            {
                result.Add($"#{id}", false, "找不到這則公告");
                continue;
            }

            var outcome = await announcements.DeleteAsync(item.Id, item.ConcurrencyStamp);
            if (outcome.Success)
            {
                await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Announcement.Delete, "Announcement",
                    item.Id.ToString(CultureInfo.InvariantCulture), $"title={item.Title}; via={AuditSource}");
            }

            result.Add($"#{id} {item.Title}", outcome.Success, outcome.Success ? null : outcome.Message);
        }

        if (result.SucceededCount > 0)
        {
            announcementCache.Invalidate();
            signal.PublishAnnouncementsChanged();
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> DeleteBackupsAsync([Description("備份檔名清單（list_backups 回傳的 fileName），最多 50 個")] string[] fileNames)
    {
        var (items, error) = AdminAssistantMutation.ValidateBatch(fileNames);
        if (error is not null)
        {
            return error;
        }

        var listed = backups.List().Select(file => file.FileName).ToHashSet(StringComparer.Ordinal);
        var result = new BatchResult("刪除備份");
        foreach (var name in items)
        {
            if (listed.Contains(name) == false)
            {
                result.Add(name, false, "備份清單中找不到這個檔名（請用 list_backups 回傳的檔名）");
                continue;
            }

            if (backups.Delete(name))
            {
                await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Backup.Delete, "Backup", name, $"file={name}; via={AuditSource}");
                result.Add(name, true, null);
            }
            else
            {
                result.Add(name, false, "找不到這份備份，可能已被刪除。");
            }
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> RunPurgeJobAsync([Description("清理類排程的名稱（Name）或顯示名稱，例如 AuditLogRetention 或「稽核紀錄保留」")] string jobName)
    {
        var job = ResolveJob(overview.Descriptors, jobName);
        if (job is null)
        {
            return UnknownJobMessage(overview.Descriptors, jobName);
        }

        if (PurgeJobNames.Contains(job.Name) == false)
        {
            return $"「{job.DisplayName}」不是清理類排程，沒有執行；請改用 trigger_scheduled_job。";
        }

        var user = currentUser.CurrentUser;
        if (overview.TryTrigger(job.Name, user.Account, user.Id) == false)
        {
            return $"「{job.DisplayName}」已經在佇列或執行中，沒有重複排入。";
        }

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Job.Trigger, "ScheduledJob", job.Name, $"job={job.Name}; via={AuditSource}");
        return $"已排入「{job.DisplayName}」，會在背景依目前的保留天數清除資料；稍後可以用 get_job_recent_runs 查看刪了多少。";
    }

    public async Task<string> EnablePurgeJobAsync([Description("清理類排程的名稱（Name）或顯示名稱")] string jobName)
    {
        var job = ResolveJob(overview.Descriptors, jobName);
        if (job is null)
        {
            return UnknownJobMessage(overview.Descriptors, jobName);
        }

        if (PurgeJobNames.Contains(job.Name) == false)
        {
            return $"「{job.DisplayName}」不是清理類排程，沒有執行；請改用 set_scheduled_job_enabled。";
        }

        var current = (await overview.GetOverviewAsync()).Items.FirstOrDefault(item => item.Name == job.Name);
        if (current?.IsEnabled == true)
        {
            return $"「{job.DisplayName}」原本就是啟用，沒有變更。";
        }

        await overview.SetEnabledAsync(job.Name, true, currentUser.CurrentUser.Account);
        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.Job.Enable, "ScheduledJob", job.Name, $"job={job.Name}; via={AuditSource}");
        return $"已啟用「{job.DisplayName}」，從下一個時段開始依保留天數自動清除資料。";
    }

    public async Task<string> ShortenRetentionAsync(
        [Description("保留天數參數的 key 或名稱，例如 LogRetentionSettings:AuditLogDays 或「稽核紀錄保留天數」")] string key,
        [Description("新的天數；或 default＝改回設定檔的值（那樣會縮短時才用這個工具）")] string value,
        CancellationToken cancellationToken = default)
    {
        var item = ResolveParameter((await parameters.GetOverviewAsync(cancellationToken)).Items, key);
        if (item is null)
        {
            return UnknownParameter(key);
        }

        var definition = item.Definition;
        if (definition.IsRetention == false)
        {
            return $"「{definition.Label}」不是保留天數參數，沒有變更；請改用 set_system_parameter。";
        }

        if (string.Equals(value?.Trim(), "default", StringComparison.OrdinalIgnoreCase))
        {
            if (item.Override is not { } row)
            {
                return $"「{definition.Label}」目前沒有系統參數覆寫，沒有變更。";
            }

            if (DefinitelyShortensRetention(item.EffectiveValue, item.BaseValue) == false)
            {
                return $"改回設定檔的值不會縮短「{definition.Label}」，沒有用這個工具執行；請改用 reset_system_parameter。";
            }

            return await FinishParameterChangeAsync(definition, await parameters.ResetAsync(definition.Key, row.ConcurrencyStamp, cancellationToken));
        }

        if (SystemParameterValueCodec.TryNormalize(definition, value, out var normalized, out var parseError) == false)
        {
            return $"沒有變更：{parseError}";
        }

        if (DefinitelyShortensRetention(item.EffectiveValue, normalized) == false)
        {
            return $"這個修改不會縮短「{definition.Label}」，沒有用這個工具執行；請改用 set_system_parameter。";
        }

        var result = await parameters.SaveAsync(definition.Key, normalized, item.Override?.ConcurrencyStamp, currentUser.CurrentUser.Account, cancellationToken);
        return await FinishParameterChangeAsync(definition, result);
    }

    public async Task<string> CustomPurgeAsync(
        [Description("資料種類，例如 audit-log、exception-log、token-usage、ai-call-log、deleted-user")] string rowKey,
        [Description("只留最近 N 天（1～3650）；與 beforeDate 二選一")] int? keepDays = null,
        [Description("刪除這一天之前的資料（yyyy-MM-dd，不含當天）；與 keepDays 二選一")] string? beforeDate = null,
        CancellationToken cancellationToken = default)
    {
        var (cutoff, error) = ParsePurgeRequest(rowKey, keepDays, beforeDate, DateOnly.FromDateTime(DateTime.Today));
        if (cutoff is null)
        {
            return $"沒有清除：{error}";
        }

        var key = rowKey.Trim();
        var outcome = await retention.PurgeCustomAsync(key, cutoff, cancellationToken);

        // 同「資料保留與清理」頁：刪到資料就寫稽核 —— 中途失敗也一樣（已經刪掉的要留下紀錄）。
        if (CustomPurgeAuditDetail(outcome, cutoff) is { } detail)
        {
            await ViewAudit.WriteAsync(audit, currentUser, outcome.AuditAction, outcome.TargetType, "*", detail);
        }

        return outcome.Failed
            ? $"清除失敗（已刪除 {outcome.Removed} 筆後中斷），詳細原因請查看日誌。"
            : ToJson(new { rowKey = key, condition = cutoff.Describe(), removed = outcome.Removed, left = outcome.Left });
    }

    /// <summary>自訂清除的稽核 detail（同資料保留頁，加 via）；中途失敗時加 <c>failed=true</c>；一筆都沒刪時回 null（不寫）。</summary>
    public static string? CustomPurgeAuditDetail(CustomPurgeOutcome outcome, CustomPurgeCutoff cutoff)
        => outcome.Removed > 0
            ? $"{cutoff.AuditDetail(outcome.Removed)}{(outcome.Failed ? "; failed=true" : string.Empty)}; via={AuditSource}"
            : null;

    /// <summary>
    /// 保留天數會不會縮短（0.9.159 起）：沿用頁面的 <see cref="SystemParameterView.ShortensRetention"/>，
    /// 但任一邊不是整數時保守地當作會縮短 —— 頁面另有確認窗兜底，工具是唯一的防線。新值 0（不清除）不算縮短。
    /// </summary>
    public static bool WouldShortenRetention(string? currentValue, string? newValue)
    {
        var parsable = int.TryParse(currentValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            && int.TryParse(newValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        return parsable == false || SystemParameterView.ShortensRetention(currentValue, newValue);
    }

    /// <summary>
    /// 確實會縮短（0.9.159 起，給 shorten_retention 用）：兩邊都是整數而且新值較短。
    /// 和 <see cref="WouldShortenRetention"/> 方向相反的保守：無法判斷時，可復原的工具要拒絕，不可復原的工具也不能接。
    /// </summary>
    public static bool DefinitelyShortensRetention(string? currentValue, string? newValue)
        => int.TryParse(currentValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            && int.TryParse(newValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            && SystemParameterView.ShortensRetention(currentValue, newValue);

    /// <summary>
    /// 修改系統參數的核准窗「目前狀態」（0.9.160 起）：目前值與來源、設定檔值、要改成什麼；保留類參數再說明會不會縮短。
    /// 參數有 value 就是設定，沒有（或 default）就是改回設定檔值。找不到參數時回 null（不顯示）。
    /// </summary>
    public static async Task<string?> PreviewParameterChangeAsync(
        IServiceProvider services, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var overview = await services.GetRequiredService<SystemParameterManager>().GetOverviewAsync(cancellationToken);
        if (ResolveParameter(overview.Items, ArgumentText(arguments, "key")) is not { } item)
        {
            return null;
        }

        var definition = item.Definition;
        var text = $"{definition.Label}：目前 {SystemParameterValueCodec.Display(definition, item.EffectiveValue)}（{SourceText(item.Source)}）"
            + $"；設定檔值 {SystemParameterValueCodec.Display(definition, item.BaseValue)}";
        var raw = ArgumentText(arguments, "value");
        string? target;
        if (raw is null || string.Equals(raw, "default", StringComparison.OrdinalIgnoreCase))
        {
            target = item.BaseValue;
            text += "；改回設定檔值";
        }
        else if (SystemParameterValueCodec.TryNormalize(definition, raw, out var normalized, out _))
        {
            target = normalized;
            text += $"；改為 {SystemParameterValueCodec.Display(definition, normalized)}";
        }
        else
        {
            return text + $"；新的值「{raw}」看不懂，工具會拒絕。";
        }

        return definition.IsRetention
            ? text + (WouldShortenRetention(item.EffectiveValue, target) ? "。⚠ 會縮短保留期限。" : "。不縮短保留期限。")
            : text + "。";
    }

    private static string SourceText(SystemParameterSource source) => source switch
    {
        SystemParameterSource.Override => "系統參數",
        SystemParameterSource.NotApplied => "設定檔；資料庫的覆寫不合法、沒有套用",
        _ => "設定檔",
    };

    /// <summary>模型給的參數值轉成文字（JSON 字串、數字或一般物件）；沒有或空白時回 null。</summary>
    private static string? ArgumentText(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (arguments.TryGetValue(name, out var value) == false || value is null)
        {
            return null;
        }

        var text = value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
            ? element.GetString()
            : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>以 key（不分大小寫）或名稱找系統參數；找不到回 null。</summary>
    public static SystemParameterOverviewItem? ResolveParameter(IReadOnlyList<SystemParameterOverviewItem> items, string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        return items.FirstOrDefault(item => string.Equals(item.Definition.Key, text, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(item => string.Equals(item.Definition.Label, text, StringComparison.Ordinal));
    }

    private static string UnknownParameter(string? key)
        => $"找不到系統參數「{key}」，沒有變更。可以先用 get_system_parameters 查看可調整的參數與 key。";

    private static string ShortensRetentionRefusal(SystemParameterDefinition definition, string? from, string? to)
        => $"「{definition.Label}」從 {SystemParameterValueCodec.Display(definition, from)} 改為 {SystemParameterValueCodec.Display(definition, to)} 會縮短保留期限，"
            + "下一次清理會永久刪除超出的資料、無法復原，所以沒有用這個工具修改；請改用 shorten_retention（核准窗會以紅字再確認一次）。";

    private async Task<string> FinishParameterChangeAsync(SystemParameterDefinition definition, SystemParameterChangeResult result)
    {
        if (result.Success == false)
        {
            return $"沒有變更：{result.Message}";
        }

        await ViewAudit.WriteAsync(audit, currentUser,
            result.IsReset ? AuditActions.SystemParameter.Reset : AuditActions.SystemParameter.Update,
            "SystemParameter", definition.Key, $"{result.AuditDetail}; via={AuditSource}");
        return $"{result.Message}（生效時機：{definition.EffectNote}）";
    }

    private async Task<(string Kind, int? TargetId, string? Error)> ResolveAnnouncementTargetAsync(string? targetKind, string? targetName)
    {
        var kind = targetKind?.Trim().ToLowerInvariant() switch
        {
            null or "" or "all" or "全體" or "全部" => AnnouncementTargetKinds.All,
            "role" or "角色" => AnnouncementTargetKinds.Role,
            "team" or "團隊" => AnnouncementTargetKinds.Team,
            _ => null,
        };
        if (kind is null)
        {
            return (string.Empty, null, $"對象「{targetKind}」無法辨識（可用 all、role、team）。");
        }

        if (kind == AnnouncementTargetKinds.All)
        {
            return (kind, null, null);
        }

        var (roles, teams) = await announcements.GetTargetOptionsAsync();
        var options = kind == AnnouncementTargetKinds.Role ? roles : teams;
        var kindText = kind == AnnouncementTargetKinds.Role ? "角色" : "團隊";
        var name = targetName?.Trim() ?? string.Empty;
        var matched = options.Where(option => string.Equals(option.Name, name, StringComparison.Ordinal))
            .Concat(options.Where(option => string.Equals(option.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Select(option => (int?)option.Id)
            .FirstOrDefault();
        return matched is { } id
            ? (kind, id, null)
            : (kind, null, $"找不到{kindText}「{targetName}」。現有{kindText}：{string.Join("、", options.Select(option => option.Name))}。");
    }

    private static string DescribeTarget(string kind, string? targetName) => kind switch
    {
        AnnouncementTargetKinds.Role => $"角色「{targetName?.Trim()}」",
        AnnouncementTargetKinds.Team => $"團隊「{targetName?.Trim()}」",
        _ => "全體",
    };

    /// <summary>同公告頁：寫稽核（detail 加 via），快取失效並通知所有開著的畫面重新顯示。</summary>
    private async Task FinishAnnouncementChangeAsync(string action, AnnouncementAdapterModel model)
    {
        await ViewAudit.WriteAsync(audit, currentUser, action, "Announcement", model.Id.ToString(CultureInfo.InvariantCulture),
            $"title={model.Title}; target={model.TargetKind}:{model.TargetId}; via={AuditSource}");
        announcementCache.Invalidate();
        signal.PublishAnnouncementsChanged();
    }

    /// <summary>以名稱或顯示名稱（不分大小寫、去前後空白）找排程作業；找不到回 null。</summary>
    public static ScheduledJobDescriptor? ResolveJob(IReadOnlyList<ScheduledJobDescriptor> descriptors, string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        return descriptors.FirstOrDefault(item =>
            string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.DisplayName, text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>找不到排程時回給模型的說明，列出可用的名稱。</summary>
    public static string UnknownJobMessage(IReadOnlyList<ScheduledJobDescriptor> descriptors, string? input)
        => $"找不到排程作業「{input}」。可用的排程：{string.Join("、", descriptors.Select(item => $"{item.Name}（{item.DisplayName}）"))}。";

    private object? DescribeRun(JobRunAdapterModel? run) => run is null ? null : new
    {
        run.Trigger,
        run.TriggeredByAccount,
        run.Status,
        startedAt = Format(overview.ToLocal(run.StartedAtUtc)),
        finishedAt = run.FinishedAtUtc is { } finished ? Format(overview.ToLocal(finished)) : null,
        run.DurationMs,
        message = run.Message is null ? null : Truncate(run.Message, RunMessageMaxLength),
        run.TraceId,
    };

    private static AiToolDefinition Define(
        string name, string displayName, string description, Func<OpsAssistantTools, Delegate> method, string permissionPage = MagicObjectHelper.角色_排程作業)
        => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            PermissionPage = permissionPage,
            PermissionAction = PermissionActions.View,
            Create = services => AIFunctionFactory.Create(method(services.GetRequiredService<OpsAssistantTools>()), name, description),
        };

    private static AiToolDefinition DefineIrreversible(
        string name, string displayName, string permissionPage, string permissionAction, string description, string approvalNote,
        Func<OpsAssistantTools, Delegate> method)
        => DefineMutating(name, displayName, permissionPage, permissionAction, description, approvalNote, method) with { IsIrreversible = true };

    private static AiToolDefinition DefineMutating(
        string name, string displayName, string permissionPage, string permissionAction, string description, string approvalNote,
        Func<OpsAssistantTools, Delegate> method)
        => Define(name, displayName, description, method, permissionPage) with
        {
            PermissionAction = permissionAction,
            IsMutating = true,
            ApprovalNote = approvalNote,
        };
}
