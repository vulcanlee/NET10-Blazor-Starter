using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Models.Systems;
using MyProject.Web.Email;

namespace MyProject.Tests;

/// <summary>記憶體交接區（背景寄信器的行為見 <see cref="EmailOutboxTests"/>，0.9.167 起）。</summary>
public sealed class EmailQueueTests
{
    /// <summary>
    /// 守住 FullMode = Wait：Drop 系列模式下 TryWrite 永遠回 true，
    /// 信被丟掉呼叫端也不會知道。
    /// </summary>
    [Fact]
    public void TryEnqueue_WhenFull_ShouldReturnFalse()
    {
        var queue = new ChannelEmailQueue(NullLogger<ChannelEmailQueue>.Instance, capacity: 2);

        Assert.True(queue.TryEnqueue(CreateMessage("a")));
        Assert.True(queue.TryEnqueue(CreateMessage("b")));
        Assert.False(queue.TryEnqueue(CreateMessage("c")));
    }

    private static EmailMessage CreateMessage(string subject) =>
        new("alice@example.com", subject, "<p>x</p>", "x", EmailKinds.Test);
}
