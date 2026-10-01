using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 系統例外紀錄與稽核紀錄的自動保存期限（0.9.78 起，LOG-13）：啟動時執行一次，之後每日一次。
///
/// 天數讀 <see cref="LogRetentionSettings"/>，0＝不自動清理。每次真的刪到資料時寫一筆稽核
/// （<c>ExceptionLog.AutoPurge</c>／<c>Audit.AutoPurge</c>，操作者為系統），空跑不寫。
///
/// ⚠️ 兩張表的時間基準不同：例外紀錄存本地時間、稽核存 UTC，門檻必須各自用對的時鐘算。
/// 兩項各自 try/catch：一項失敗不影響另一項，明天再試。
/// </summary>
public sealed class LogRetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly IOptionsMonitor<LogRetentionSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LogRetentionWorker> logger;

    public LogRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor,
        IOptionsMonitor<LogRetentionSettings> options,
        TimeProvider timeProvider,
        ILogger<LogRetentionWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.contextAccessor = contextAccessor;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync();

            using var timer = new PeriodicTimer(Interval, timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 服務關閉。
        }
    }

    internal async Task RunOnceAsync()
    {
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, "LogRetention", null, null));
        try
        {
            using var scope = scopeFactory.CreateScope();
            var settings = options.CurrentValue;
            var auditLogService = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

            if (settings.ExceptionLogDays > 0)
            {
                await PurgeExceptionLogsAsync(scope.ServiceProvider, auditLogService, settings.ExceptionLogDays);
            }

            if (settings.AuditLogDays > 0)
            {
                await PurgeAuditLogsAsync(scope.ServiceProvider, auditLogService, settings.AuditLogDays);
            }
        }
        catch (Exception ex)
        {
            // 清理失敗不可以讓背景服務停掉；明天再試。
            logger.LogError(ex, "Log retention failed.");
        }
        finally
        {
            contextAccessor.Clear();
        }
    }

    private async Task PurgeExceptionLogsAsync(IServiceProvider services, IAuditLogService auditLogService, int days)
    {
        try
        {
            var threshold = timeProvider.GetLocalNow().DateTime.AddDays(-days);
            var removed = await services.GetRequiredService<ExceptionLogService>().PurgeBeforeAsync(threshold);

            logger.LogInformation("Exception log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);
            if (removed > 0)
            {
                await auditLogService.WriteAsync(
                    AuditActions.ExceptionLog.AutoPurge, success: true,
                    targetType: "ExceptionLog", targetId: "*", detail: $"rows={removed}; days={days}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception log retention failed. RetentionDays={RetentionDays}", days);
        }
    }

    private async Task PurgeAuditLogsAsync(IServiceProvider services, IAuditLogService auditLogService, int days)
    {
        try
        {
            var threshold = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
            var removed = await services.GetRequiredService<AuditLogQueryService>().PurgeBeforeAsync(threshold);

            logger.LogInformation("Audit log retention completed. Rows={Rows}, RetentionDays={RetentionDays}", removed, days);

            // 寫在刪除之後，這一筆會留在表裡：稽核軌跡被清理這件事本身也必須查得到。
            if (removed > 0)
            {
                await auditLogService.WriteAsync(
                    AuditActions.Audit.AutoPurge, success: true,
                    targetType: "AuditLog", targetId: "*", detail: $"rows={removed}; days={days}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit log retention failed. RetentionDays={RetentionDays}", days);
        }
    }
}
