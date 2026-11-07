using System.Net.Sockets;
using System.Security.Cryptography;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Email;

namespace MyProject.Tests;

/// <summary>
/// 寄信佇列（0.9.167 起）：背景寄信器先把信存進 <c>EmailOutbox</c>（內文加密）再寄；
/// 暫時性失敗依 1、5、15 分鐘重試，永久性失敗或重試用完記「失敗」；寄出或放棄後清除內文。
/// </summary>
public sealed class EmailOutboxTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ManualTimeProvider clock = new(Start);
    private readonly EmailSettings emailSettings = new() { Provider = nameof(EmailProvider.Pickup) };
    private readonly EmailDeliverySettings deliverySettings = new();
    private readonly EmailBodyProtector protector = new(new EphemeralDataProtectionProvider());

    public EmailOutboxTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private EmailOutboxProcessor Processor(IEmailSender sender, EmailBodyProtector? bodyProtector = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(sender);
        return new EmailOutboxProcessor(
            factory,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            bodyProtector ?? protector,
            new StaticOptionsMonitor<EmailSettings>(emailSettings),
            new StaticOptionsMonitor<EmailDeliverySettings>(deliverySettings),
            clock,
            NullLogger<EmailOutboxProcessor>.Instance);
    }

    private static EmailMessage Message(string subject = "重設密碼") =>
        new("alice@example.com", subject, "<p>https://example.com/reset?token=SECRET</p>", "https://example.com/reset?token=SECRET", EmailKinds.PasswordReset);

    private EmailOutbox Row()
    {
        using var context = factory.CreateDbContext();
        return context.EmailOutbox.AsNoTracking().Single();
    }

    [Fact]
    public async Task Persist_StoresPendingRow_WithEncryptedBody()
    {
        await Processor(new CapturingEmailSender()).PersistAsync(Message(), fallbackSend: true, CancellationToken.None);

        var row = Row();
        Assert.Equal(EmailOutboxStatus.Pending, row.Status);
        Assert.Equal("alice@example.com", row.To);
        Assert.Equal("重設密碼", row.Subject);
        Assert.Equal(Start.UtcDateTime, row.NextAttemptAtUtc);
        Assert.NotNull(row.ProtectedBody);
        Assert.DoesNotContain("SECRET", row.ProtectedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendDue_Success_MarksSent_AndClearsBody()
    {
        var sender = new CapturingEmailSender();
        var processor = Processor(sender);
        await processor.PersistAsync(Message(), fallbackSend: true, CancellationToken.None);

        await processor.SendDueAsync(CancellationToken.None);

        var sent = Assert.Single(sender.Sent);
        Assert.Contains("SECRET", sent.TextBody, StringComparison.Ordinal);
        var row = Row();
        Assert.Equal(EmailOutboxStatus.Sent, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.ProtectedBody);
        Assert.Null(row.NextAttemptAtUtc);
        Assert.Equal(Start.UtcDateTime, row.SentAtUtc);
    }

    [Fact]
    public async Task TransientFailure_RetriesAt1_5_15Minutes_ThenFails()
    {
        var sender = new CapturingEmailSender(_ => new SocketException());
        var processor = Processor(sender);
        await processor.PersistAsync(Message(), fallbackSend: true, CancellationToken.None);

        foreach (var (delay, attempts) in new[] { (1, 1), (5, 2), (15, 3) })
        {
            await processor.SendDueAsync(CancellationToken.None);
            var row = Row();
            Assert.Equal(EmailOutboxStatus.Pending, row.Status);
            Assert.Equal(attempts, row.Attempts);
            Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(delay), row.NextAttemptAtUtc);
            Assert.StartsWith(nameof(SocketException), row.LastError, StringComparison.Ordinal);

            await processor.SendDueAsync(CancellationToken.None);
            Assert.Equal(attempts, Row().Attempts); // 還沒到期，不寄
            clock.Advance(TimeSpan.FromMinutes(delay));
        }

        await processor.SendDueAsync(CancellationToken.None);

        var failed = Row();
        Assert.Equal(EmailOutboxStatus.Failed, failed.Status);
        Assert.Equal(4, failed.Attempts);
        Assert.Null(failed.ProtectedBody);
        Assert.Null(failed.NextAttemptAtUtc);
    }

    [Fact]
    public async Task PermanentFailure_FailsImmediately()
    {
        var sender = new CapturingEmailSender(_ => new AuthenticationException("bad password"));
        var processor = Processor(sender);
        await processor.PersistAsync(Message(), fallbackSend: true, CancellationToken.None);

        await processor.SendDueAsync(CancellationToken.None);

        var row = Row();
        Assert.Equal(EmailOutboxStatus.Failed, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.ProtectedBody);
    }

    [Fact]
    public async Task ZeroRetries_FailsOnFirstTransientFailure()
    {
        deliverySettings.MaxRetries = 0;
        var processor = Processor(new CapturingEmailSender(_ => new IOException("connection reset")));
        await processor.PersistAsync(Message(), fallbackSend: true, CancellationToken.None);

        await processor.SendDueAsync(CancellationToken.None);

        Assert.Equal(EmailOutboxStatus.Failed, Row().Status);
    }

    [Fact]
    public async Task ProviderNone_RecordsSkipped_WithoutBody()
    {
        emailSettings.Provider = nameof(EmailProvider.None);
        var sender = new CapturingEmailSender();
        var processor = Processor(sender);

        await processor.PersistAsync(Message(), fallbackSend: true, CancellationToken.None);
        await processor.SendDueAsync(CancellationToken.None);

        var row = Row();
        Assert.Equal(EmailOutboxStatus.Skipped, row.Status);
        Assert.Null(row.ProtectedBody);
        Assert.Null(row.NextAttemptAtUtc);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task UndecryptableBody_FailsPermanently()
    {
        await Processor(new CapturingEmailSender()).PersistAsync(Message(), fallbackSend: true, CancellationToken.None);
        var sender = new CapturingEmailSender();

        // 換一個金鑰環（等於金鑰遺失）：解不開，記失敗、不重試。
        await Processor(sender, new EmailBodyProtector(new EphemeralDataProtectionProvider())).SendDueAsync(CancellationToken.None);

        var row = Row();
        Assert.Equal(EmailOutboxStatus.Failed, row.Status);
        Assert.StartsWith(nameof(CryptographicException), row.LastError, StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task PersistFails_FallsBackToDirectSend()
    {
        var sender = new CapturingEmailSender();
        connection.Close(); // 資料庫寫不進去

        await Processor(sender).PersistAsync(Message(), fallbackSend: true, CancellationToken.None);

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task PersistFails_OnShutdown_DoesNotSend()
    {
        var sender = new CapturingEmailSender();
        connection.Close();

        await Processor(sender).PersistAsync(Message(), fallbackSend: false, CancellationToken.None);

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Worker_PersistsThenSends_AndPicksUpPendingRowsAfterRestart()
    {
        // 上一次執行留下的「待寄」。
        await Processor(new CapturingEmailSender()).PersistAsync(Message("上次留下的"), fallbackSend: true, CancellationToken.None);
        var sender = new CapturingEmailSender();
        var queue = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        var worker = new EmailDispatchWorker(queue, Processor(sender), new ExceptionContextAccessor(), NullLogger<EmailDispatchWorker>.Instance);

        queue.TryEnqueue(Message("新的"));
        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sender.Sent.Count == 2);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["上次留下的", "新的"], sender.Sent.Select(x => x.Subject).Order());
        using var context = factory.CreateDbContext();
        Assert.All(context.EmailOutbox.AsNoTracking().ToList(), x => Assert.Equal(EmailOutboxStatus.Sent, x.Status));
    }

    [Fact]
    public async Task Worker_OnShutdown_StoresQueuedMessagesWithoutSending()
    {
        var sender = new CapturingEmailSender();
        var queue = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        var worker = new EmailDispatchWorker(queue, Processor(sender), new ExceptionContextAccessor(), NullLogger<EmailDispatchWorker>.Instance);

        queue.TryEnqueue(Message("關機前入列"));
        await worker.DrainOnShutdownAsync();

        Assert.Empty(sender.Sent);
        Assert.Equal(EmailOutboxStatus.Pending, Row().Status);
    }

    [Fact]
    public async Task Purge_SkipsPendingRows_AndStatsMatch()
    {
        var old = Start.UtcDateTime.AddDays(-100);
        using (var context = factory.CreateDbContext())
        {
            foreach (var status in new[] { EmailOutboxStatus.Sent, EmailOutboxStatus.Failed, EmailOutboxStatus.Skipped, EmailOutboxStatus.Pending })
            {
                context.EmailOutbox.Add(new EmailOutbox { CreatedAtUtc = old, Kind = EmailKinds.Test, To = "a@example.com", Subject = status, Status = status });
            }

            context.EmailOutbox.Add(new EmailOutbox { CreatedAtUtc = Start.UtcDateTime, Kind = EmailKinds.Test, To = "a@example.com", Subject = "新的", Status = EmailOutboxStatus.Sent });
            context.SaveChanges();
        }

        var service = new MyProject.Business.Services.DataAccess.EmailOutboxQueryService(factory, clock, NullLogger<MyProject.Business.Services.DataAccess.EmailOutboxQueryService>.Instance);
        var cutoff = Start.UtcDateTime.AddDays(-90);

        Assert.Equal(3, (await service.GetRetentionStatsAsync(cutoff)).Expired);
        Assert.Equal(3, await service.PurgeBeforeAsync(cutoff, CancellationToken.None));

        using var verify = factory.CreateDbContext();
        Assert.Equal(["Pending", "新的"], verify.EmailOutbox.AsNoTracking().Select(x => x.Subject).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetPage_FiltersByStatusAndKeyword_NewestFirst()
    {
        using (var context = factory.CreateDbContext())
        {
            context.EmailOutbox.AddRange(
                new EmailOutbox { CreatedAtUtc = Start.UtcDateTime.AddMinutes(-2), Kind = EmailKinds.Test, To = "alice@example.com", Subject = "舊", Status = EmailOutboxStatus.Failed },
                new EmailOutbox { CreatedAtUtc = Start.UtcDateTime.AddMinutes(-1), Kind = EmailKinds.Test, To = "alice@example.com", Subject = "新", Status = EmailOutboxStatus.Failed },
                new EmailOutbox { CreatedAtUtc = Start.UtcDateTime, Kind = EmailKinds.Test, To = "bob@example.com", Subject = "別人", Status = EmailOutboxStatus.Failed },
                new EmailOutbox { CreatedAtUtc = Start.UtcDateTime, Kind = EmailKinds.Test, To = "alice@example.com", Subject = "寄出", Status = EmailOutboxStatus.Sent });
            context.SaveChanges();
        }

        var service = new MyProject.Business.Services.DataAccess.EmailOutboxQueryService(factory, clock, NullLogger<MyProject.Business.Services.DataAccess.EmailOutboxQueryService>.Instance);
        var (items, total) = await service.GetPageAsync(new MyProject.Business.Services.DataAccess.EmailLogQuery(Status: EmailOutboxStatus.Failed, Keyword: "alice"));

        Assert.Equal(2, total);
        Assert.Equal(["新", "舊"], items.Select(x => x.Subject));
        Assert.Equal(Start.UtcDateTime.AddMinutes(-1).AddHours(8), items[0].CreatedAt); // 假時鐘的本地時區是 UTC+8
    }

    [Fact]
    public async Task SendDue_ClaimsRowBeforeSending_SoAConcurrentRunSkipsIt()
    {
        await Processor(new CapturingEmailSender()).PersistAsync(Message(), fallbackSend: true, CancellationToken.None);
        int? nestedSent = null;
        EmailOutbox? duringSend = null;
        var sender = new CallbackEmailSender(async () =>
        {
            // 寄送途中（例如 IIS 回收時新舊程序並存）另一個處理器看到的：這一列已經被佔住，不會再寄一次。
            duringSend = Row();
            nestedSent = await Processor(new CapturingEmailSender()).SendDueAsync(CancellationToken.None);
        });

        await Processor(sender).SendDueAsync(CancellationToken.None);

        Assert.Equal(0, nestedSent);
        Assert.Equal(1, duringSend!.Attempts);
        Assert.Equal(Start.UtcDateTime + EmailOutboxProcessor.ClaimLease, duringSend.NextAttemptAtUtc);
        Assert.Equal(EmailOutboxStatus.Sent, Row().Status);
    }

    [Fact]
    public async Task ProviderSwitchedToNoneAfterPersist_RecordsSkipped_NotSent()
    {
        var sender = new CapturingEmailSender();
        var processor = Processor(sender);
        await processor.PersistAsync(Message(), fallbackSend: true, CancellationToken.None);
        emailSettings.Provider = nameof(EmailProvider.None);

        await processor.SendDueAsync(CancellationToken.None);

        var row = Row();
        Assert.Equal(EmailOutboxStatus.Skipped, row.Status);
        Assert.Null(row.ProtectedBody);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Persist_IgnoresCancellation_SoATakenMessageIsNotLost()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Processor(new CapturingEmailSender()).PersistAsync(Message(), fallbackSend: false, cancelled.Token);

        Assert.Equal(EmailOutboxStatus.Pending, Row().Status);
    }

    [Fact]
    public async Task SendDue_RunsBetweenSendsCallback_AfterEachMessage()
    {
        var processor = Processor(new CapturingEmailSender());
        await processor.PersistAsync(Message("一"), fallbackSend: true, CancellationToken.None);
        await processor.PersistAsync(Message("二"), fallbackSend: true, CancellationToken.None);
        var calls = 0;

        var sent = await processor.SendDueAsync(CancellationToken.None, () => { calls++; return Task.CompletedTask; });

        Assert.Equal(2, sent);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Queue_AfterComplete_RejectsNewMessages()
    {
        var queue = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance);
        queue.Complete();

        Assert.False(queue.TryEnqueue(Message()));
    }

    private sealed class CallbackEmailSender(Func<Task> onSend) : IEmailSender
    {
        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => await onSend();
    }

    public static TheoryData<Exception, bool> Classifications => new()
    {
        { new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.MailboxBusy, "try later"), true },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "no such user"), false },
        { new SmtpProtocolException("unexpected"), true },
        { new SocketException(), true },
        { new IOException("reset"), true },
        { new TimeoutException(), true },
        { new OperationCanceledException(), true },
        { new AuthenticationException("bad password"), false },
        { new SslHandshakeException("tls"), false },
        { new ParseException("bad address", 0, 0), false },
        { new UnauthorizedAccessException(), false },
        { new CryptographicException(), false },
        { new InvalidOperationException("unknown"), true },
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void Classifier_SeparatesTransientFromPermanent(Exception exception, bool transient)
        => Assert.Equal(transient, EmailFailureClassifier.IsTransient(exception));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "背景寄信器在 5 秒內沒有處理完。");
            await Task.Delay(20);
        }
    }
}
