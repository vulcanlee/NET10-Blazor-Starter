using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Abstractions;
using MyProject.Dtos.Ai;
using MyProject.Web.Streaming;

namespace MyProject.Tests;

/// <summary>
/// NDJSON 串流回應（0.10.5 起，WASM 遷移 M6）：每個項目一行、立刻送出；
/// 使用者中斷連線之後不再寫出，但序列照樣讀完 —— 呼叫端在最後做的收尾（稽核、記帳）一定會執行。
/// </summary>
public sealed class NdjsonResultTests
{
    [Fact]
    public async Task ShouldWriteOneJsonLinePerItemWithTheNdjsonContentType()
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;

        await new NdjsonResult<AiStreamFrameDto>(Frames()).ExecuteResultAsync(Context(http));

        Assert.Equal(NdjsonResult<AiStreamFrameDto>.ContentType, http.Response.ContentType);
        Assert.Equal("no-cache, no-transform", http.Response.Headers.CacheControl.ToString());
        var lines = Encoding.UTF8.GetString(body.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("delta", JsonSerializer.Deserialize<AiStreamFrameDto>(lines[1], JsonSerializerOptions.Web)!.Kind);
        Assert.Equal("中文", JsonSerializer.Deserialize<AiStreamFrameDto>(lines[1], JsonSerializerOptions.Web)!.Text);
    }

    [Fact]
    public async Task WhenTheClientIsGone_ShouldStopWritingButStillRunTheProducerToTheEnd()
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        http.RequestAborted = aborted.Token;
        var finished = false;

        await new NdjsonResult<AiStreamFrameDto>(Produce()).ExecuteResultAsync(Context(http));

        Assert.True(finished);
        Assert.Equal(0, body.Length);

        async IAsyncEnumerable<AiStreamFrameDto> Produce()
        {
            await foreach (var frame in Frames())
            {
                yield return frame;
            }

            finished = true;
        }
    }

    private static ActionContext Context(HttpContext http) => new(http, new RouteData(), new ActionDescriptor());

    private static async IAsyncEnumerable<AiStreamFrameDto> Frames()
    {
        await Task.Yield();
        yield return new AiStreamFrameDto { Kind = AiStreamFrameKinds.Start };
        yield return new AiStreamFrameDto { Kind = AiStreamFrameKinds.Delta, Text = "中文" };
        yield return new AiStreamFrameDto { Kind = AiStreamFrameKinds.Result, Result = new AiResultDto { Success = true } };
    }
}
