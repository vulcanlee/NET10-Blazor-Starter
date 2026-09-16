using System.Threading.Channels;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 系統例外紀錄的背景寫入器 —— 本專案<b>第一個</b> <see cref="BackgroundService"/>。
///
/// 為什麼要有它：寫資料庫不能發生在 <c>logger.LogError()</c> 的呼叫執行緒上。
/// AI 主機逾時那種「短時間重複數百次」的情境，若每次都同步寫一次 DB，
/// 會直接拖垮正在等待的使用者。改成「入列即返回、背景慢慢寫」。
///
/// 單一消費者還順帶解決並發 upsert 競爭：同一個簽章不會有兩個執行緒同時想新增。
///
/// ⚠️ <b>本類別絕不可使用 <see cref="ILogger"/>。</b>它位在例外記錄管線的最末端，
/// 走 ILogger 會讓自己的失敗再被收進管線，形成無限遞迴。需要留話時改用
/// NLog 的 InternalLogger（Program.cs 啟動時已設定好輸出檔）。
/// </summary>
public sealed class ExceptionLogWriter : BackgroundService
{
    private readonly ChannelReader<ExceptionLogEntry> reader;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ExceptionContextAccessor contextAccessor;

    public ExceptionLogWriter(
        ChannelReader<ExceptionLogEntry> reader,
        IServiceScopeFactory scopeFactory,
        ExceptionContextAccessor contextAccessor)
    {
        this.reader = reader;
        this.scopeFactory = scopeFactory;
        this.contextAccessor = contextAccessor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NLog.Common.InternalLogger.Info("ExceptionLogWriter started.");

        try
        {
            await foreach (var entry in reader.ReadAllAsync(stoppingToken))
            {
                await WriteOneAsync(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常關機。
        }
        catch (Exception ex)
        {
            NLog.Common.InternalLogger.Error(ex, "ExceptionLogWriter stopped unexpectedly.");
        }
        finally
        {
            NLog.Common.InternalLogger.Info(
                "ExceptionLogWriter stopped. DroppedEntries={0}", ExceptionLogProvider.DroppedCount);
        }
    }

    private async Task WriteOneAsync(ExceptionLogEntry entry)
    {
        // 抑制旗標涵蓋整個寫入過程：期間任何 LogError 都不會再被收進管線。
        using var suppression = contextAccessor.Suppress();

        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ExceptionLogService>();
            await service.RecordAsync(entry);
        }
        catch (Exception ex)
        {
            // 絕不使用 ILogger（見類別註解）。
            NLog.Common.InternalLogger.Warn(ex, "Failed to persist an exception log entry.");
        }
    }
}
