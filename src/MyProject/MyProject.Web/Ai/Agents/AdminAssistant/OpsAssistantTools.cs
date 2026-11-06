using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Share.Helpers;
using MyProject.Web.Backup;
using MyProject.Web.Configuration;
using MyProject.Web.Configuration.Parameters;
using MyProject.Web.Dashboard;
using MyProject.Web.DataRetention;
using MyProject.Web.Scheduling;
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

    public OpsAssistantTools(
        ScheduledJobOverviewService overview,
        SystemParameterManager parameters,
        AnnouncementService announcements,
        BackupStore backups,
        IOptionsMonitor<BackupSettings> backupSettings,
        DataRetentionOverviewService retention)
    {
        this.overview = overview;
        this.parameters = parameters;
        this.announcements = announcements;
        this.backups = backups;
        this.backupSettings = backupSettings;
        this.retention = retention;
    }

    public static AiToolDefinition SystemParametersTool { get; } = Define(
        "get_system_parameters", "系統參數總覽",
        "列出可在「系統參數」頁調整的設定（不含任何金鑰或密碼）：分組、名稱、目前值、設定檔值、來源（設定檔或覆寫）、問題、生效時機、是否為保留天數。"
        + "可只看某個分組，例如「資料保留」「AI 模型」「AI 用量」。",
        tools => tools.GetSystemParametersAsync, MagicObjectHelper.角色_系統參數);

    public static AiToolDefinition AnnouncementsTool { get; } = Define(
        "list_announcements", "公告清單",
        $"列出公告：標題、內容（截斷 {AnnouncementContentMaxLength} 字）、開始與結束時間、對象（全體、角色或團隊）、建立者。預設只列尚未結束的。",
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
}
