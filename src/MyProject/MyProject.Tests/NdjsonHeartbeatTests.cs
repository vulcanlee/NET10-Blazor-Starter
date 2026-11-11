using MyProject.Web.Streaming;

namespace MyProject.Tests;

/// <summary>
/// NDJSON 串流的心跳（0.10.7 起，WASM 遷移 M8）：資料源太久沒有新項目時插入 ping，項目順序不變，資料源的例外照樣丟出。
/// </summary>
public sealed class NdjsonHeartbeatTests
{
    [Fact]
    public async Task WhenTheSourceIsSlow_ShouldInsertPings_AndKeepTheOrder()
    {
        var items = new List<string>();
        await foreach (var item in NdjsonHeartbeat.WithHeartbeat(Slow(), TimeSpan.FromMilliseconds(50), () => "ping"))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { "一", "二" }, items.Where(x => x != "ping"));
        Assert.Contains("ping", items.SkipWhile(x => x != "一").TakeWhile(x => x != "二"));
    }

    [Fact]
    public async Task WhenTheSourceIsFast_ShouldNotInsertPings()
    {
        var items = new List<string>();
        await foreach (var item in NdjsonHeartbeat.WithHeartbeat(Fast(), TimeSpan.FromSeconds(10), () => "ping"))
        {
            items.Add(item);
        }

        Assert.Equal(new[] { "一", "二", "三" }, items);
    }

    [Fact]
    public async Task WhenTheSourceThrows_ShouldThrowTheSameException()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in NdjsonHeartbeat.WithHeartbeat(Broken(), TimeSpan.FromSeconds(10), () => "ping"))
            {
            }
        });

        Assert.Equal("壞了", error.Message);
    }

    private static async IAsyncEnumerable<string> Slow()
    {
        yield return "一";
        await Task.Delay(400);
        yield return "二";
    }

    private static async IAsyncEnumerable<string> Fast()
    {
        await Task.Yield();
        yield return "一";
        yield return "二";
        yield return "三";
    }

    private static async IAsyncEnumerable<string> Broken()
    {
        await Task.Yield();
        yield return "一";
        throw new InvalidOperationException("壞了");
    }
}
