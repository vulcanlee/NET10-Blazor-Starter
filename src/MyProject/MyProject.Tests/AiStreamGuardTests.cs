using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// AI 串流端點的保護層（0.10.5 起，M6 獨立審查）：序列最後一定有一筆結果；
/// 使用者中斷時服務丟的取消例外當成「已取消」（稽核照寫），其他例外當成「未預期的錯誤」（不寫稽核）。
/// </summary>
public sealed class AiStreamGuardTests
{
    [Fact]
    public async Task ShouldPassTextThroughAndStopAtTheResult()
    {
        var guard = new AiStreamGuard();

        var updates = await CollectAsync(guard, Source(throwAfterText: null), CancellationToken.None);

        Assert.Equal(new[] { "一", "二" }, updates.Where(x => x.Result is null).Select(x => x.TextDelta));
        Assert.True(updates[^1].Result!.Success);
        Assert.False(guard.Unexpected);
    }

    [Fact]
    public async Task WhenTheCallerCancelsAndTheServiceThrows_ShouldEndWithACanceledResult()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var guard = new AiStreamGuard();

        var updates = await CollectAsync(guard, Source(throwAfterText: new OperationCanceledException()), cancel.Token);

        Assert.Equal(AiAnalysisFailureReason.Canceled, updates[^1].Result!.Reason);
        Assert.False(guard.Unexpected);
    }

    [Fact]
    public async Task WhenTheServiceThrowsSomethingElse_ShouldEndWithAnUnexpectedResult()
    {
        var guard = new AiStreamGuard();

        var updates = await CollectAsync(guard, Source(throwAfterText: new InvalidOperationException("壞了")), CancellationToken.None);

        Assert.Equal("一", updates[0].TextDelta);
        Assert.Equal(AiAnalysisFailureReason.Unexpected, updates[^1].Result!.Reason);
        Assert.Equal(AiStreamGuard.UnexpectedMessage, updates[^1].Result!.ErrorMessage);
        Assert.True(guard.Unexpected);
    }

    [Fact]
    public async Task WhenTheServiceEndsWithoutAResult_ShouldStillEndWithOne()
    {
        var guard = new AiStreamGuard();

        var updates = await CollectAsync(guard, Empty(), CancellationToken.None);

        Assert.Single(updates);
        Assert.Equal(AiAnalysisFailureReason.Unexpected, updates[0].Result!.Reason);
    }

    private static async Task<List<AiStreamUpdate>> CollectAsync(AiStreamGuard guard, IAsyncEnumerable<AiStreamUpdate> source, CancellationToken cancellationToken)
    {
        var list = new List<AiStreamUpdate>();
        await foreach (var update in guard.RunAsync(source, NullLogger.Instance, "Test", cancellationToken))
        {
            list.Add(update);
        }

        return list;
    }

    private static async IAsyncEnumerable<AiStreamUpdate> Source(Exception? throwAfterText)
    {
        await Task.Yield();
        yield return new AiStreamUpdate { TextDelta = "一" };
        if (throwAfterText is not null)
        {
            throw throwAfterText;
        }

        yield return new AiStreamUpdate { TextDelta = "二" };
        yield return new AiStreamUpdate { Result = new AiAnalysisResult { Success = true, Markdown = "一二" } };
    }

    private static async IAsyncEnumerable<AiStreamUpdate> Empty()
    {
        await Task.Yield();
        yield break;
    }
}
