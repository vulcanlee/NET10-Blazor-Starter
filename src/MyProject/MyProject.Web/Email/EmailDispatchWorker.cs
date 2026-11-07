using MyProject.Models.Systems;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Email;

/// <summary>
/// 背景寄信器（0.9.167 起改為「先存再寄」）：
/// <list type="number">
/// <item>從記憶體交接區 <see cref="ChannelEmailQueue"/> 取出信，交給 <see cref="EmailOutboxProcessor.PersistAsync"/> 寫進資料庫；</item>
/// <item>再由 <see cref="EmailOutboxProcessor.SendDueAsync"/> 寄出到期的「待寄」，暫時性失敗會重試。</item>
/// </list>
/// 沒有新信時最多等 <see cref="PollInterval"/> 就醒來看有沒有到期的重試。
/// 關機時把交接區剩下的信存進資料庫（不寄），下次啟動接著寄；只有程式崩潰時交接區裡那幾毫秒的信會遺失。
///
/// <para>與 <see cref="ExceptionLogWriter"/> 不同，這裡**可以也應該**使用 <see cref="ILogger"/>：
/// 它不在例外記錄管線內，寄送失敗記成 Error 正好會進「系統例外紀錄」（來源＝背景作業）。
/// 例外情境固定是 <c>EmailDispatch</c>，例外告警服務會略過這個情境，不會形成迴圈。</para>
/// </summary>
public sealed class EmailDispatchWorker : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly ChannelEmailQueue queue;
    private readonly EmailOutboxProcessor processor;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly ILogger<EmailDispatchWorker> logger;

    public EmailDispatchWorker(
        ChannelEmailQueue queue,
        EmailOutboxProcessor processor,
        ExceptionContextAccessor contextAccessor,
        ILogger<EmailDispatchWorker> logger)
    {
        this.queue = queue;
        this.processor = processor;
        this.contextAccessor = contextAccessor;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Email dispatch worker started.");
        contextAccessor.Set(new ExceptionContext(ExceptionSources.Background, "EmailDispatch", null, null));

        try
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                try
                {
                    await PersistQueuedAsync(stoppingToken);

                    // 每寄一封之間就把交接區的新信存進資料庫；一批寄滿表示可能還有，不等待直接寄下一批。
                    int sent;
                    do
                    {
                        sent = await processor.SendDueAsync(stoppingToken, () => PersistQueuedAsync(stoppingToken));
                    }
                    while (sent == EmailOutboxProcessor.BatchSize && stoppingToken.IsCancellationRequested == false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || stoppingToken.IsCancellationRequested == false)
                {
                    // 一輪出錯（例如資料庫暫時被鎖）不可讓背景服務停掉，下一輪再試。
                    logger.LogError(ex, "Email dispatch round failed.");
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(PollInterval);
                try
                {
                    await queue.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested == false)
                {
                    // 等滿 PollInterval：醒來看有沒有到期的重試。
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常關機。
        }
        finally
        {
            await DrainOnShutdownAsync();
            contextAccessor.Clear();
        }

        logger.LogInformation("Email dispatch worker stopped.");
    }

    private async Task PersistQueuedAsync(CancellationToken stoppingToken)
    {
        while (queue.Reader.TryRead(out var message))
        {
            await processor.PersistAsync(message, fallbackSend: true, stoppingToken);
        }
    }

    /// <summary>
    /// 關機時先關閉交接區（之後的入列回 false），再把剩下的信存進資料庫（不寄），下次啟動接著寄。
    /// 資料庫被鎖時每封可能要等，超過主機的關機時限就會被截斷，剩下的信會遺失。
    /// </summary>
    internal async Task DrainOnShutdownAsync()
    {
        queue.Complete();
        var count = 0;
        while (queue.Reader.TryRead(out var message))
        {
            await processor.PersistAsync(message, fallbackSend: false, CancellationToken.None);
            count++;
        }

        if (count > 0)
        {
            logger.LogInformation("Queued emails stored for the next start. Count={Count}", count);
        }
    }
}
