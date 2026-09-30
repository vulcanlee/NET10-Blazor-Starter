using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// AI 對話紀錄的自動過期（0.9.72 起）：啟動時執行一次，之後每日一次，
/// 刪除超過 <see cref="AiCallLogSettings.RetentionDays"/> 的紀錄與內容檔。
///
/// ⚠️ <c>Enabled=false</c> 時照樣執行 —— 停用只代表「不再記」，已存在的內容仍必須過期。
/// 啟動時的第一次執行發生在 <c>app.Run()</c> 之後，此時 migration 已經跑完。
/// </summary>
public sealed class AiCallLogRetentionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly IOptionsMonitor<AiCallLogSettings> options;
    private readonly ILogger<AiCallLogRetentionWorker> logger;

    public AiCallLogRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor,
        IOptionsMonitor<AiCallLogSettings> options,
        ILogger<AiCallLogRetentionWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.contextAccessor = contextAccessor;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync();

            using var timer = new PeriodicTimer(Interval);
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
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, "AiCallLogRetention", null, null));
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<AiCallLogService>();
            var removed = await service.PurgeExpiredAsync();

            logger.LogInformation(
                "AI call log retention completed. Rows={Rows}, RetentionDays={RetentionDays}",
                removed,
                options.CurrentValue.RetentionDays);
        }
        catch (Exception ex)
        {
            // 清除失敗不可以讓背景服務停掉；明天再試。
            logger.LogError(ex, "AI call log retention failed.");
        }
        finally
        {
            contextAccessor.Clear();
        }
    }
}
