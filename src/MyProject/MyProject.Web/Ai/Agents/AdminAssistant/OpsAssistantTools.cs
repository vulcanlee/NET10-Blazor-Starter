using System.ComponentModel;
using Microsoft.Extensions.AI;
using MyProject.Models.AdapterModel;
using MyProject.Share.Helpers;
using MyProject.Web.Dashboard;
using MyProject.Web.Scheduling;
using static MyProject.Web.Ai.Agents.AdminAssistant.AdminAssistantToolText;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理「系統設定與維運」的唯讀工具（0.9.155 起，里程碑 1 先做排程作業）。
/// 排程頁讀取時不寫稽核，這裡也不寫（工具外殼仍會寫 <c>Ai.ToolInvoked</c>）。
/// </summary>
public sealed class OpsAssistantTools
{
    public const int MaxRunTake = 50;
    public const int RunMessageMaxLength = 500;

    private readonly ScheduledJobOverviewService overview;

    public OpsAssistantTools(ScheduledJobOverviewService overview)
    {
        this.overview = overview;
    }

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

    public static IReadOnlyList<AiToolDefinition> ReadTools { get; } = [JobsOverviewTool, JobRecentRunsTool];

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

        var runs = await overview.GetRecentRunsAsync(job.Name, Clamp(take, 1, MaxRunTake));
        return ToJson(new { job.Name, job.DisplayName, runs = runs.Select(DescribeRun) });
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

    private static AiToolDefinition Define(string name, string displayName, string description, Func<OpsAssistantTools, Delegate> method)
        => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            PermissionPage = MagicObjectHelper.角色_排程作業,
            PermissionAction = PermissionActions.View,
            Create = services => AIFunctionFactory.Create(method(services.GetRequiredService<OpsAssistantTools>()), name, description),
        };
}
