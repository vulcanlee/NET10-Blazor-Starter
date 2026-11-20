using Microsoft.Extensions.Options;
using MyProject.Web.Scheduling;

namespace MyProject.Web.Configuration.Validation;

/// <summary>
/// <c>ScheduledJobSettings</c>（0.9.96 起）：作業名稱必須是已註冊的作業（拼錯的話覆寫會靜默失效）、
/// cron 必須是 5 欄位標準格式而且**真的會觸發**（<c>0 0 30 2 *</c> 解析得過但永遠不會到）、保留天數在範圍內。
///
/// 0.10.39 起：
/// <list type="bullet">
/// <item><see cref="RetiredJobNames"/>：已移除的作業名稱略過不驗 —— 衍生專案的 appsettings.Production.json 還留著舊作業的覆寫時，
/// 升級後不會因為「不是已註冊的排程作業」而啟動失敗。</item>
/// <item>時間由 <see cref="IScheduledJobCronOverride"/> 接手的作業（例如系統巡檢報告，時間在系統參數頁設定）不可以在這裡覆寫。</item>
/// </list>
/// </summary>
public sealed class ScheduledJobSettingsValidator : IValidateOptions<ScheduledJobSettings>
{
    /// <summary>
    /// 已移除的作業名稱。設定檔留著它們的覆寫只會被忽略。
    /// <c>AdminPatrol</c>：系統管理助理巡檢，0.10.39 起由系統巡檢報告的日報取代。
    /// </summary>
    public static readonly IReadOnlySet<string> RetiredJobNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AdminPatrol" };

    private readonly HashSet<string> knownJobNames;
    private readonly HashSet<string> overriddenJobNames;

    public ScheduledJobSettingsValidator(IEnumerable<ScheduledJobDescriptor> descriptors, IEnumerable<IScheduledJobCronOverride>? cronOverrides = null)
    {
        knownJobNames = descriptors.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        overriddenJobNames = (cronOverrides ?? []).SelectMany(x => x.JobNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public ValidateOptionsResult Validate(string? name, ScheduledJobSettings options)
    {
        var errors = new OptionsErrors();
        errors.RequireRange($"{ScheduledJobSettings.SectionName}:{nameof(options.JobRunRetentionDays)}", options.JobRunRetentionDays, 0, 36500);

        foreach (var (jobName, jobOverride) in options.Jobs)
        {
            var key = $"{ScheduledJobSettings.SectionName}:{nameof(options.Jobs)}:{jobName}";
            if (RetiredJobNames.Contains(jobName))
            {
                continue;
            }

            if (!knownJobNames.Contains(jobName))
            {
                errors.Add(key, $"不是已註冊的排程作業。可用的名稱：{string.Join("、", knownJobNames.Order(StringComparer.Ordinal))}。");
                continue;
            }

            if (string.IsNullOrWhiteSpace(jobOverride.Cron))
            {
                continue;
            }

            if (overriddenJobNames.Contains(jobName))
            {
                errors.Add($"{key}:Cron", "這個作業的執行時間在「系統參數」頁設定，不能在設定檔覆寫；請刪除這一行。");
                continue;
            }

            if (!ScheduleCalculator.TryParse(jobOverride.Cron, out var cron))
            {
                errors.Add($"{key}:Cron", $"不是合法的 5 欄位 cron（分 時 日 月 星期），目前是「{jobOverride.Cron}」。例：0 3 * * * 是每天 03:00。");
            }
            else if (ScheduleCalculator.NextOccurrenceUtc(cron!, DateTime.UtcNow, TimeZoneInfo.Local) is null)
            {
                errors.Add($"{key}:Cron", $"永遠不會觸發，目前是「{jobOverride.Cron}」。請確認日期存在（例如 2 月沒有 30 日）。");
            }
        }

        return errors.ToResult();
    }
}
