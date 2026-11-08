using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Business.Services.Other.AuditIntegrity;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 稽核紀錄完整性檢查（0.9.175 起）：每天 03:30（避開 02:00 的備份與 03:00 的清理）重算全部簽章，
/// 並更新資料庫以外的檢查點（<see cref="AuditChainCheckpointStore"/>）。
///
/// 發現問題時回傳 <see cref="ScheduledJobResult.Failure"/>：排程框架會通知全體管理員並寄信（不另發通知，避免收到兩則）。
/// 金鑰更換、鏈頭重建、資料庫倒回這類一次性事件只在看到它的那一次報；被改、被刪的紀錄還在就每天都報。
/// 結果另寫一筆 <c>Audit.Verify</c>，寫在檢查之後，所以這一筆本身由下一次檢查驗證。
/// </summary>
public sealed class AuditIntegrityVerifyJob : IScheduledJob
{
    public const string JobName = "AuditIntegrityVerify";

    /// <summary>失敗訊息最多列出幾個問題；完整清單到稽核紀錄頁按「驗證完整性」看。</summary>
    private const int ListedInMessage = 3;

    private readonly AuditIntegrityVerifier verifier;
    private readonly IAuditLogService auditLogService;
    private readonly ILogger<AuditIntegrityVerifyJob> logger;

    public AuditIntegrityVerifyJob(
        AuditIntegrityVerifier verifier,
        IAuditLogService auditLogService,
        ILogger<AuditIntegrityVerifyJob> logger)
    {
        this.verifier = verifier;
        this.auditLogService = auditLogService;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var report = await verifier.VerifyAsync(updateCheckpoint: true, cancellationToken);
        logger.LogInformation(
            "Audit integrity verify job completed. SignedRows={SignedRows}, Issues={IssueCount}", report.SignedRows, report.IssueCount);

        await auditLogService.WriteAsync(
            AuditActions.Audit.Verify, success: report.HasIssues == false,
            targetType: "AuditLog", targetId: "*",
            detail: $"result={(report.HasIssues ? "failed" : "ok")}; signed={report.SignedRows}; issues={report.IssueCount}; "
                + $"legacy={report.LegacyRows}; keyMismatch={report.KeyMismatchRows}; trigger={context.Trigger}");

        if (report.HasIssues == false)
        {
            return ScheduledJobResult.Success(report.Summary());
        }

        var listed = string.Join("；", report.Issues.Take(ListedInMessage).Select(x => x.Message.TrimEnd('。')));
        var more = report.IssueCount > ListedInMessage ? $"……等 {report.IssueCount} 個" : string.Empty;
        return ScheduledJobResult.Failure($"{report.Summary()}{listed}{more}。請到「稽核紀錄」頁按「驗證完整性」查看完整清單。");
    }
}
