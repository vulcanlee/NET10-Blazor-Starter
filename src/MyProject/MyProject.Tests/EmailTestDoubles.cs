using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>把收到的信記下來；可設定成每封都拋例外，用來驗證失敗路徑。</summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    private readonly Func<EmailMessage, Exception?> failWith;

    public CapturingEmailSender(Func<EmailMessage, Exception?>? failWith = null)
    {
        this.failWith = failWith ?? (_ => null);
    }

    public List<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var exception = failWith(message);
        if (exception is not null)
        {
            throw exception;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>收集入列的信，不做任何寄送。</summary>
internal sealed class CapturingEmailQueue : IEmailQueue
{
    public List<EmailMessage> Enqueued { get; } = new();

    public bool TryEnqueue(EmailMessage message)
    {
        Enqueued.Add(message);
        return true;
    }
}

/// <summary>記錄稽核呼叫，不寫資料庫。</summary>
internal sealed class RecordingAuditLogService : IAuditLogService
{
    public List<(string Action, bool Success, int? ActorUserId, string? ActorAccount, string? Detail)> Entries { get; } = new();

    public Task WriteAsync(
        string action, bool success = true, int? actorUserId = null, string? actorAccount = null,
        string? targetType = null, string? targetId = null, string? detail = null)
    {
        Entries.Add((action, success, actorUserId, actorAccount, detail));
        return Task.CompletedTask;
    }
}
