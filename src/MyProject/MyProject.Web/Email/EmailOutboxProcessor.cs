using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Email;

/// <summary>
/// 寄信佇列的兩段工作（0.9.167 起），由 <see cref="EmailDispatchWorker"/> 呼叫：
/// <list type="number">
/// <item><see cref="PersistAsync"/>：把記憶體交接區取出的信寫成一列 <see cref="EmailOutbox"/>（內文加密），
/// 寫不進資料庫時退回直接寄一次，資料庫出問題時告警信仍寄得出去。</item>
/// <item><see cref="SendDueAsync"/>：寄出「待寄且到期」的信；暫時性失敗依 <see cref="RetryDelays"/> 排下一次，
/// 重試用完或永久性失敗就記「失敗」。寄出或放棄後清除內文。</item>
/// </list>
/// 呼叫端（<see cref="IEmailQueue.TryEnqueue"/>）不碰資料庫：例外告警信是在例外記錄管線的鎖定區段內入列的。
/// </summary>
public sealed class EmailOutboxProcessor
{
    /// <summary>重試間隔，取前 <c>MaxRetries</c> 段。</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60),
    ];

    /// <summary>每一輪最多寄幾封；寄不完的下一輪接著寄。</summary>
    public const int BatchSize = 20;

    public const int MaxErrorLength = 500;

    /// <summary>
    /// 寄送前先「佔住」這一列：把下次嘗試時間推到現在＋租約。寄出後存檔失敗（例如資料庫被鎖），或 IIS 回收時新舊程序並存，
    /// 別的處理器在租約內都拿不到它，不會馬上重寄。租約要比單封上限（<c>TimeoutSeconds</c> × 2，最多 600 秒）長。
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(15);

    private const int SaveAttempts = 3;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly EmailBodyProtector protector;
    private readonly IOptionsMonitor<EmailSettings> emailOptions;
    private readonly IOptionsMonitor<EmailDeliverySettings> deliveryOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EmailOutboxProcessor> logger;
    private readonly IOptionsMonitor<SlowOperationSettings>? slowOptions;
    private readonly LoggingPipelineMonitor? monitor;

    public EmailOutboxProcessor(
        IDbContextFactory<BackendDBContext> contextFactory,
        IServiceScopeFactory scopeFactory,
        EmailBodyProtector protector,
        IOptionsMonitor<EmailSettings> emailOptions,
        IOptionsMonitor<EmailDeliverySettings> deliveryOptions,
        TimeProvider timeProvider,
        ILogger<EmailOutboxProcessor> logger,
        IOptionsMonitor<SlowOperationSettings>? slowOptions = null,
        LoggingPipelineMonitor? monitor = null)
    {
        this.contextFactory = contextFactory;
        this.scopeFactory = scopeFactory;
        this.protector = protector;
        this.emailOptions = emailOptions;
        this.deliveryOptions = deliveryOptions;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.slowOptions = slowOptions;
        this.monitor = monitor;
    }

    /// <summary>
    /// 寫成一列「待寄」（寄信停用時記「未寄出」、不存內文）。
    /// <paramref name="fallbackSend"/>：寫入失敗時直接寄一次（關機時傳 false，只記錄不寄）。
    /// </summary>
    public async Task PersistAsync(EmailMessage message, bool fallbackSend, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var disabled = IsDisabled();
        try
        {
            // 寫入不可取消：信已經從交接區取出，取消就等於遺失（關機時也一樣要存進來）。
            await using var context = await contextFactory.CreateDbContextAsync(CancellationToken.None);
            context.EmailOutbox.Add(new EmailOutbox
            {
                CreatedAtUtc = now,
                Kind = Truncate(message.Kind, 32),
                To = Truncate(message.To, 320),
                Subject = Truncate(message.Subject, 300),
                Status = disabled ? EmailOutboxStatus.Skipped : EmailOutboxStatus.Pending,
                ProtectedBody = disabled ? null : protector.Protect(message.HtmlBody, message.TextBody),
                NextAttemptAtUtc = disabled ? null : now,
            });
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store email in the outbox. Kind={Kind}, FallbackSend={FallbackSend}", message.Kind, fallbackSend);
            if (fallbackSend && disabled == false)
            {
                try
                {
                    await SendCoreAsync(message, cancellationToken);
                }
                catch (Exception sendEx) when (sendEx is not OperationCanceledException || cancellationToken.IsCancellationRequested == false)
                {
                    logger.LogError(sendEx, "Direct email dispatch failed after the outbox write failed. Kind={Kind}", message.Kind);
                    RecordAlertFailure(message.Kind);
                }
            }
        }
    }

    /// <summary>
    /// 寄出到期的「待寄」，回傳這一輪取出了幾封（等於 <see cref="BatchSize"/> 時表示可能還有，呼叫端不必等待直接再叫一次）。
    /// <paramref name="betweenSends"/>：每寄完一封就呼叫一次（背景寄信器用來把交接區的新信存進資料庫，郵件伺服器很慢時交接區才不會滿）。
    /// </summary>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken, Func<Task>? betweenSends = null)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        List<int> dueIds;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            dueIds = await context.EmailOutbox.AsNoTracking()
                .Where(x => x.Status == EmailOutboxStatus.Pending && x.NextAttemptAtUtc <= now)
                .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.Id)
                .Select(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in dueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendOneAsync(id, cancellationToken);
            if (betweenSends is not null)
            {
                await betweenSends();
            }
        }

        return dueIds.Count;
    }

    private async Task SendOneAsync(int id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await context.EmailOutbox.SingleOrDefaultAsync(
            x => x.Id == id && x.Status == EmailOutboxStatus.Pending && x.NextAttemptAtUtc <= now, cancellationToken);
        if (row is null)
        {
            return;
        }

        // 存進資料庫之後才改成寄信停用：NullEmailSender 會回成功，不可記成「已寄出」。
        if (IsDisabled())
        {
            row.Status = EmailOutboxStatus.Skipped;
            row.NextAttemptAtUtc = null;
            row.ProtectedBody = null;
            await SaveAsync(context, row.Kind);
            return;
        }

        // 先佔住再寄：條件更新只有一個處理器會成功（見 ClaimLease）。
        var original = row.NextAttemptAtUtc;
        var leaseUntil = now + ClaimLease;
        var claimed = await context.EmailOutbox
            .Where(x => x.Id == id && x.Status == EmailOutboxStatus.Pending && x.NextAttemptAtUtc == original)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextAttemptAtUtc, leaseUntil)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
        if (claimed == 0)
        {
            return;
        }

        // 佔住後重讀：讓追蹤中的值（次數、租約）和資料庫一致，之後只寫寄送結果。
        await context.Entry(row).ReloadAsync(cancellationToken);
        try
        {
            var (html, text) = row.ProtectedBody is { } body
                ? protector.Unprotect(body)
                : throw new System.Security.Cryptography.CryptographicException("Email body is missing.");
            await SendCoreAsync(new EmailMessage(row.To, row.Subject, html, text, row.Kind), cancellationToken);

            row.Status = EmailOutboxStatus.Sent;
            row.SentAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            row.NextAttemptAtUtc = null;
            row.ProtectedBody = null;
            row.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 關機：這一次不算，下次啟動再寄。
            row.Attempts--;
            row.NextAttemptAtUtc = original;
            await SaveAsync(context, row.Kind);
            throw;
        }
        catch (Exception ex)
        {
            var retries = Math.Min(deliveryOptions.CurrentValue.MaxRetries, RetryDelays.Count);
            var retry = EmailFailureClassifier.IsTransient(ex) && row.Attempts <= retries;
            row.LastError = Truncate($"{ex.GetType().Name}: {ex.Message}", MaxErrorLength);
            if (retry)
            {
                row.NextAttemptAtUtc = timeProvider.GetUtcNow().UtcDateTime + RetryDelays[row.Attempts - 1];
                logger.LogWarning(ex, "Email dispatch failed; will retry. Kind={Kind}, Attempts={Attempts}, Provider={Provider}",
                    row.Kind, row.Attempts, emailOptions.CurrentValue.Provider);
            }
            else
            {
                row.Status = EmailOutboxStatus.Failed;
                row.NextAttemptAtUtc = null;
                row.ProtectedBody = null;
                logger.LogError(ex, "Email dispatch failed. Kind={Kind}, Attempts={Attempts}, Provider={Provider}",
                    row.Kind, row.Attempts, emailOptions.CurrentValue.Provider);
                RecordAlertFailure(row.Kind);
            }
        }

        await SaveAsync(context, row.Kind);
    }

    /// <summary>
    /// 寄送結果存檔，資料庫暫時被鎖時重試幾次。最後仍失敗只記錯誤：這一列維持「待寄」、下次嘗試是租約到期時，
    /// 已經寄出的信在租約到期後會再寄一次（比遺失好）。
    /// </summary>
    private async Task SaveAsync(BackendDBContext context, string kind)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await context.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (Exception ex) when (attempt < SaveAttempts)
            {
                logger.LogWarning(ex, "Failed to save email dispatch result; retrying. Kind={Kind}, Attempt={Attempt}", kind, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to save email dispatch result. Kind={Kind}", kind);
                return;
            }
        }
    }

    private bool IsDisabled() => emailOptions.CurrentValue.TryGetProvider(out var provider) && provider == EmailProvider.None;

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = emailOptions.CurrentValue;

        // 單封信的上限：SmtpClient.Timeout 只管單一次網路操作，連線＋登入＋寄送加總要另外設。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds * 2));

        using var scope = scopeFactory.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var stopwatch = Stopwatch.StartNew();
        await sender.SendAsync(message, timeout.Token);

        // 慢寄信（LOG-21）：只記信件種類，收件者與內容一律不記。
        var threshold = slowOptions?.CurrentValue.ExternalCallMs ?? 0;
        if (SlowOperationSettings.IsSlow(stopwatch.Elapsed, threshold))
        {
            logger.LogWarning(
                "Slow email dispatch. Kind={Kind}, ElapsedMilliseconds={ElapsedMilliseconds}, ThresholdMilliseconds={ThresholdMilliseconds}",
                message.Kind, stopwatch.ElapsedMilliseconds, threshold);
        }
    }

    // 告警信寄不出去不會再觸發告警（避免迴圈），只能在系統健康監控看到（LOG-22）。
    private void RecordAlertFailure(string kind)
    {
        if (kind == EmailKinds.ExceptionAlert)
        {
            monitor?.RecordAlertSendFailure();
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
