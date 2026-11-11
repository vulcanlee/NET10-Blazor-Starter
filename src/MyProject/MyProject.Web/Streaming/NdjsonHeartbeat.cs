using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MyProject.Web.Streaming;

/// <summary>
/// NDJSON 串流的心跳（0.10.7 起，WASM 遷移 M8）：資料源超過 <c>interval</c> 沒有新項目時插入一筆 ping。
/// Agent 的工具可能跑很久、推論模型可能很久不出字、通知推播大部分時間沒事 —— 一直沒有資料流過的連線，
/// 會被中間的代理伺服器或負載平衡器當成閒置而切斷。ping 讓連線保持活著，瀏覽器端收到後忽略即可。
/// <para>
/// 資料源在背景讀進佇列（順序不變）；資料源丟的例外照樣從這裡丟出。這裡不處理取消：
/// 取消由資料源自己依 <c>RequestAborted</c> 處理（與 <see cref="NdjsonResult{T}"/> 的約定相同）。
/// </para>
/// </summary>
public static class NdjsonHeartbeat
{
    public static async IAsyncEnumerable<T> WithHeartbeat<T>(
        IAsyncEnumerable<T> source, TimeSpan interval, Func<T> ping, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in source)
                {
                    await channel.Writer.WriteAsync(item);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        var reader = channel.Reader;
        Task<bool>? waiting = null;
        while (true)
        {
            waiting ??= reader.WaitToReadAsync(CancellationToken.None).AsTask();
            var finished = await Task.WhenAny(waiting, Task.Delay(interval, cancellationToken)) == waiting;
            if (!finished)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ping();
                continue;
            }

            var hasMore = await waiting;
            waiting = null;
            if (!hasMore)
            {
                break;
            }

            while (reader.TryRead(out var item))
            {
                yield return item;
            }
        }

        await pump;
    }
}
