using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 系統管理助理的定時巡檢（0.9.161 起，預設每天 07:30）：以系統管理助理跑一次巡檢（只用唯讀工具），
/// 判讀報告 → 存進「巡檢報告」→ 有「注意」「異常」或無法判讀時，通知全體啟用中的管理員（站內＋Email，摘要＋連結）。
///
/// <list type="bullet">
/// <item>AI 設定不完整：回成功並寫「略過」，不通知 —— 沒有設定 AI 的衍生專案才不會每天收到失敗信。</item>
/// <item>AI 呼叫失敗或沒有產出內容：回失敗，由排程框架自動通知管理員（JobFailed），不存報告。</item>
/// <item>沒有登入的使用者：執行身分與權限見 <see cref="AdminPatrolRunner"/>、<see cref="ScheduledPatrolToolGuard"/>。</item>
/// </list>
/// 執行時間只能改設定檔的 <c>ScheduledJobSettings:Jobs:AdminPatrol:Cron</c>；停用與立即執行在「排程作業」頁。
/// </summary>
public sealed class AdminPatrolJob : IScheduledJob
{
    public const string JobName = "AdminPatrol";

    private readonly IAdminPatrolRunner runner;
    private readonly AdminPatrolReportService reportService;
    private readonly INotificationSender notificationSender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AdminPatrolJob> logger;

    public AdminPatrolJob(
        IAdminPatrolRunner runner,
        AdminPatrolReportService reportService,
        INotificationSender notificationSender,
        TimeProvider timeProvider,
        ILogger<AdminPatrolJob> logger)
    {
        this.runner = runner;
        this.reportService = reportService;
        this.notificationSender = notificationSender;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        if (runner.UnavailableReason is { } unavailable)
        {
            logger.LogInformation("Admin patrol skipped because AI is not configured.");
            return ScheduledJobResult.Success($"AI 設定不完整，略過巡檢：{unavailable}");
        }

        var reportId = Guid.NewGuid();
        var result = await runner.RunAsync(reportId, cancellationToken);
        if (result.Success == false)
        {
            return ScheduledJobResult.Failure($"巡檢失敗：{result.ErrorMessage}");
        }

        if (string.IsNullOrWhiteSpace(result.Markdown))
        {
            return ScheduledJobResult.Failure("AI 沒有產出巡檢報告內容。");
        }

        var assessment = AdminPatrolReportParser.Parse(result.Markdown, result.IsTruncatedByLength);
        var createdAt = timeProvider.GetLocalNow().DateTime;
        var saved = await reportService.SaveAsync(new AdminPatrolReportDraft(
            reportId,
            createdAt,
            assessment.Overall,
            assessment.AttentionSectionNames,
            context.Trigger,
            context.TriggeredByAccount,
            context.RunId,
            result.ModelName,
            result.IsTruncatedByLength,
            result.Markdown));
        if (saved is null)
        {
            return ScheduledJobResult.Failure("巡檢報告存檔失敗，詳細內容請看「系統例外紀錄」。");
        }

        var attention = string.IsNullOrEmpty(assessment.AttentionSectionNames) ? string.Empty : $"（{assessment.AttentionSectionNames}）";
        logger.LogInformation(
            "Admin patrol completed. ReportId={ReportId}, Status={Status}, NeedsAttention={NeedsAttention}",
            reportId, assessment.Overall, assessment.NeedsAttention);
        if (assessment.NeedsAttention == false)
        {
            return ScheduledJobResult.Success($"巡檢完成：{assessment.Overall}。");
        }

        // SendAsync 不丟例外、失敗時回 Failed。需要注意卻沒通知到人，就回失敗讓排程框架另外通知管理員（JobFailed）。
        var sent = await notificationSender.SendAsync(AdminPatrolNotification.Build(reportId, createdAt, assessment), cancellationToken);
        if (sent.Failed)
        {
            return ScheduledJobResult.Failure($"巡檢完成：{assessment.Overall}{attention}，但通知發送失敗；報告已存在系統管理助理頁的「巡檢報告」。");
        }

        return ScheduledJobResult.Success($"巡檢完成：{assessment.Overall}{attention}；已通知管理員。");
    }
}
