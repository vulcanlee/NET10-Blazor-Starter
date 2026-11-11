using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// 系統例外紀錄頁與 AI 例外分析的 Web API（0.10.5 起，WASM 遷移 M6）：只限管理員（<c>[AdminOnly]</c>），
/// 查詢、明細、複製、匯出、刪除、清除、清空，以及 AI 分析（NDJSON 串流、多輪追問）與 PDF 報告。稽核寫法與 Server 版頁面相同。
/// AI 服務換成假的：記下收到的對話，回固定的內容（或卡住直到被取消）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ExceptionLogApiTests : IClassFixture<ApiTestApplicationFactoryWithFakeExceptionAi>
{
    private const string Password = "Passw0rd-1";
    private const string Base = "/api/v1/exception-logs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiTestApplicationFactoryWithFakeExceptionAi factory;

    public ExceptionLogApiTests(ApiTestApplicationFactoryWithFakeExceptionAi factory)
    {
        this.factory = factory;
        factory.Ai.Reset();
    }

    // ───────────────────────────── 清單、明細、複製、匯出 ─────────────────────────────

    [Fact]
    public async Task Search_ShouldFilterLikeTheScreen_AndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("查詢");
        await SeedAsync(tag, "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 3);
        await SeedAsync(tag, "System.ArgumentException", ExceptionSources.WebApi, minutesAgo: 2);

        var all = await SendAsync<PagedResult<ExceptionLogItemDto>>(client, HttpMethod.Post, $"{Base}/search", new ExceptionLogSearchRequestDto { Keyword = tag });
        var ui = await SendAsync<PagedResult<ExceptionLogItemDto>>(client, HttpMethod.Post, $"{Base}/search",
            new ExceptionLogSearchRequestDto { Keyword = tag, Source = ExceptionSources.Ui, RecordSearch = true });

        Assert.Equal(["System.ArgumentException", "System.InvalidOperationException"], all.Items.Select(x => x.ExceptionType));
        Assert.Equal(2, all.TotalCount);
        Assert.Equal("System.InvalidOperationException", Assert.Single(ui.Items).ExceptionType);
        var audit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.Search, admin.Id));
        Assert.Equal("start=（空）; end=（空）; source=畫面; hasAccount=False; hasKeyword=True", audit.Detail);
        Assert.Equal("ExceptionLog", audit.TargetType);
    }

    /// <summary>
    /// 排序修正：Server 版比對 "ascend"/"descend"，AntDesign 給的是 Ascending/Descending，排序從來沒生效（匯出與複製也是）。
    /// API 照畫面送的欄位與方向排序，匯出、複製與清單同序。
    /// </summary>
    [Fact]
    public async Task Sorting_ShouldApplyToTheListExportAndCopy_InTheSameOrder()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("排序");
        var many = await SeedAsync(tag, "A.ManyException", ExceptionSources.Ui, minutesAgo: 5, occurrences: 3);
        var once = await SeedAsync(tag, "A.OnceException", ExceptionSources.Ui, minutesAgo: 1);
        var request = new ExceptionLogSearchRequestDto { Keyword = tag, SortBy = "OccurrenceCount", SortDescending = false };

        var page = await SendAsync<PagedResult<ExceptionLogItemDto>>(client, HttpMethod.Post, $"{Base}/search", request);
        var csv = await (await client.PostAsJsonAsync($"{Base}/export", request)).Content.ReadAsStringAsync();
        var copy = await SendAsync<ExceptionLogCopyTextDto>(client, HttpMethod.Post, $"{Base}/copy-text", request);
        var descending = await SendAsync<PagedResult<ExceptionLogItemDto>>(client, HttpMethod.Post, $"{Base}/search",
            new ExceptionLogSearchRequestDto { Keyword = tag, SortBy = "OccurrenceCount", SortDescending = true });

        Assert.Equal([once, many], page.Items.Select(x => x.Id));
        Assert.Equal([many, once], descending.Items.Select(x => x.Id));
        Assert.True(csv.IndexOf("A.OnceException", StringComparison.Ordinal) < csv.IndexOf("A.ManyException", StringComparison.Ordinal));
        Assert.True(copy.Text.IndexOf("A.OnceException", StringComparison.Ordinal) < copy.Text.IndexOf("A.ManyException", StringComparison.Ordinal));
    }

    /// <summary>本地時間以「不帶時差」送出：帶了 +08:00 的話 WASM 會換成瀏覽器的時區。</summary>
    [Fact]
    public async Task LocalTimes_ShouldBeSentWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("時差");
        var id = await SeedAsync(tag, "System.TimeZoneException", ExceptionSources.Ui, minutesAgo: 1);

        var texts = new[]
        {
            await (await client.PostAsJsonAsync($"{Base}/search", new ExceptionLogSearchRequestDto { Keyword = tag })).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"{Base}/{id}")).Content.ReadAsStringAsync(),
        };

        var zoned = new System.Text.RegularExpressions.Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)");
        var envelope = new System.Text.RegularExpressions.Regex("\"timestamp\":\"[^\"]*\"");
        Assert.All(texts, text => Assert.False(zoned.IsMatch(envelope.Replace(text, string.Empty)), text));
        Assert.Contains("lastOccurredAt", texts[0]);
    }

    [Fact]
    public async Task Search_WithAnInvalidPageSize_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base}/search", new ExceptionLogSearchRequestDto { PageSize = 0 })).StatusCode);
    }

    [Fact]
    public async Task Options_ShouldListThePurgeDaysAndTheAiSettings()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var options = await SendAsync<ExceptionLogOptionsDto>(client, HttpMethod.Get, $"{Base}/options");

        Assert.Equal(LogRetentionSettings.DefaultExceptionLogDays, options.PurgeDays);
        Assert.Equal(ExceptionLogService.MaxRows, options.MaxRows);
        Assert.True(options.AiAvailable);
        Assert.Equal(string.Empty, options.AiUnavailableReason);
        Assert.Equal(FakeExceptionAiService.FollowUpLimit, options.MaxFollowUpRounds);
    }

    [Fact]
    public async Task Detail_ShouldReturnTheRowAndItsStackTrace_AndWriteOneViewAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("明細");
        var id = await SeedAsync(tag, "System.NullReferenceException", ExceptionSources.Ui, minutesAgo: 1);
        var missing = await SeedWithoutStackAsync(tag);

        var detail = await SendAsync<ExceptionLogDetailDto>(client, HttpMethod.Get, $"{Base}/{id}");
        var withoutStack = await SendAsync<ExceptionLogDetailDto>(client, HttpMethod.Get, $"{Base}/{missing}");

        Assert.Equal("System.NullReferenceException", detail.ExceptionType);
        Assert.Equal($"堆疊 {tag}", detail.StackTrace);
        Assert.Equal("bob", detail.Account);
        Assert.Null(withoutStack.StackTrace);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/999999999")).StatusCode);
        var audits = await AuditsAsync(AuditActions.ExceptionLog.View, admin.Id);
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, x => x.TargetId == id.ToString() && x.Detail == "type=System.NullReferenceException");
    }

    /// <summary>點列複製只讀堆疊、不寫稽核（與 Server 版相同；Server 版點列複製不記查看）。</summary>
    [Fact]
    public async Task StackTrace_ShouldReturnTheRawStack_WithoutAuditing()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("複製一筆");
        var id = await SeedAsync(tag, "System.FormatException", ExceptionSources.Ui, minutesAgo: 1);

        var stack = await SendAsync<ExceptionLogStackTraceDto>(client, HttpMethod.Get, $"{Base}/{id}/stack-trace");

        Assert.Equal($"堆疊 {tag}", stack.StackTrace);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/999999999/stack-trace")).StatusCode);
        Assert.Equal(0, await CountAnyAuditAsync(admin.Id));
    }

    [Fact]
    public async Task CopyText_ShouldJoinEveryRowWithItsStack_WithoutAuditing()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("複製全部");
        await SeedAsync(tag, "System.FirstException", ExceptionSources.Ui, minutesAgo: 2);
        await SeedAsync(tag, "System.SecondException", ExceptionSources.Ui, minutesAgo: 1);

        var copy = await SendAsync<ExceptionLogCopyTextDto>(client, HttpMethod.Post, $"{Base}/copy-text", new ExceptionLogSearchRequestDto { Keyword = tag });

        Assert.Equal(2, copy.Count);
        Assert.StartsWith("例外類型：System.SecondException\n", copy.Text);
        Assert.Contains("使用者：bob（UserId=7）\n", copy.Text);
        Assert.Contains($"----- 完整堆疊開始 -----\n堆疊 {tag}\n----- 完整堆疊結束 -----\n", copy.Text);
        Assert.Contains("\n" + new string('=', 50) + "\n\n例外類型：System.FirstException", copy.Text);
        Assert.Equal(0, await CountAnyAuditAsync(admin.Id));
    }

    [Fact]
    public async Task Export_ShouldReturnACsvWithBom_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("匯出");
        await SeedAsync(tag, "System.IOException", ExceptionSources.Ui, minutesAgo: 1);

        var response = await client.PostAsJsonAsync($"{Base}/export", new ExceptionLogSearchRequestDto { Keyword = tag });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^MyProject\.Web-exceptions-\d{8}-\d{6}\.csv$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("最後發生,次數,例外類型,訊息,來源,頁面,操作,記錄器,使用者,首次發生,最後追蹤碼", lines[0]);
        var audit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.Export, admin.Id));
        Assert.Equal("format=csv; rows=1", audit.Detail);
        Assert.Equal("*", audit.TargetId);
    }

    // ───────────────────────────── 刪除、清除、清空 ─────────────────────────────

    [Fact]
    public async Task Delete_ShouldRemoveTheRow_AndAuditItWithTheShortType()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = await SeedAsync(Unique("刪除"), "System.Data.DataException", ExceptionSources.Ui, minutesAgo: 1);

        await SendAsync<object>(client, HttpMethod.Delete, $"{Base}/{id}");

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Base}/{id}")).StatusCode);
        var audit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.Delete, admin.Id));
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal("type=DataException", audit.Detail);
        await using var context = await CreateContextAsync();
        Assert.False(await context.ExceptionLog.AnyAsync(x => x.Id == id));
    }

    [Fact]
    public async Task PurgeAndClearAll_ShouldWorkLikeTheScreen_AndAuditThemselves()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var old = await SeedAsync(Unique("清除"), "System.OldException", ExceptionSources.Ui, minutesAgo: 60 * 24 * 400);

        var purge = await RawAsync(client, HttpMethod.Post, $"{Base}/purge");

        Assert.Equal("已清除 1 筆紀錄。", purge.Message);
        var purgeAudit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.Purge, admin.Id));
        Assert.Equal($"清除 {LogRetentionSettings.DefaultExceptionLogDays} 天未再發生的例外紀錄：已清除 1 筆紀錄。", purgeAudit.Detail);
        Assert.Equal("*", purgeAudit.TargetId);
        await using (var context = await CreateContextAsync())
        {
            Assert.False(await context.ExceptionLog.AnyAsync(x => x.Id == old));
        }

        await SeedAsync(Unique("清空"), "System.AnyException", ExceptionSources.Ui, minutesAgo: 1);
        await RawAsync(client, HttpMethod.Post, $"{Base}/clear-all");

        var clearAudit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.ClearAll, admin.Id));
        Assert.Equal("清空全部例外紀錄：", clearAudit.Detail);
        await using var after = await CreateContextAsync();
        Assert.Equal(0, await after.ExceptionLog.CountAsync());
    }

    // ───────────────────────────── AI 例外分析 ─────────────────────────────

    [Fact]
    public async Task Ai_FirstAnalysis_ShouldStreamTheReply_RebuildTheDetailOnTheServer_AndAuditItOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("AI");
        var id = await SeedAsync(tag, "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);
        var conversationId = Guid.NewGuid();

        var frames = await StreamAsync(client, $"{Base}/{id}/ai", new ExceptionAiRequestDto { ConversationId = conversationId });

        Assert.Equal(AiStreamFrameKinds.Start, frames[0].Kind);
        Assert.Equal(["第一段", "第二段"], frames.Where(x => x.Kind == AiStreamFrameKinds.Delta).Select(x => x.Text));
        var result = Assert.IsType<AiResultDto>(frames[^1].Result);
        Assert.True(result.Success);
        Assert.Equal("第一段第二段", result.Markdown);
        Assert.Equal("fake-model", result.ModelName);

        // 第一則由伺服器用資料庫與堆疊檔組（含堆疊、不含帳號），system 提示由服務自己加，畫面送不進來。
        var sent = Assert.Single(factory.Ai.Conversations);
        var first = Assert.Single(sent);
        Assert.Equal(AiChatRoles.User, first.Role);
        Assert.StartsWith("以下是系統例外紀錄中的一筆例外", first.Content);
        Assert.Contains($"堆疊 {tag}", first.Content);
        Assert.DoesNotContain("bob", first.Content);
        var context = Assert.Single(factory.Ai.Contexts);
        Assert.Equal(new AiExceptionCallContext(id, "System.InvalidOperationException", conversationId), context);

        var audit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.AiAnalyze, admin.Id));
        Assert.True(audit.Success);
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal($"初始分析；對話 1 則、{first.Content.Length} 字元；模型 fake-model；用量 輸入 100　輸出 20　合計 120；耗時 1500 ms", audit.Detail);
    }

    [Fact]
    public async Task Ai_FollowUp_ShouldSendTheWholeConversation_AndAuditTheRound()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = await SeedAsync(Unique("追問"), "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);
        var messages = new List<AiChatMessageDto>
        {
            new() { Role = "assistant", Content = "報告" },
            new() { Role = "user", Content = "為什麼？" },
        };

        var frames = await StreamAsync(client, $"{Base}/{id}/ai", new ExceptionAiRequestDto { ConversationId = Guid.NewGuid(), Messages = messages });

        Assert.True(frames[^1].Result!.Success);
        var sent = Assert.Single(factory.Ai.Conversations);
        Assert.Equal([AiChatRoles.User, AiChatRoles.Assistant, AiChatRoles.User], sent.Select(x => x.Role));
        Assert.Equal(["報告", "為什麼？"], sent.Skip(1).Select(x => x.Content));
        var audit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.AiFollowUp, admin.Id));
        Assert.StartsWith($"追問第 1 輪；對話 3 則、{sent.Sum(x => x.Content.Length)} 字元；模型 fake-model", audit.Detail);
    }

    [Theory]
    [InlineData("user", "assistant")]
    [InlineData("assistant", "assistant")]
    [InlineData("assistant")]
    [InlineData("system", "user")]
    [InlineData("assistant", "user", "assistant", "user", "assistant", "user")]
    public async Task Ai_WithAMalformedConversation_ShouldReturn400_WithoutCallingTheAi(params string[] roles)
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = await SeedAsync(Unique("格式"), "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);
        var request = new ExceptionAiRequestDto
        {
            ConversationId = Guid.NewGuid(),
            Messages = roles.Select(role => new AiChatMessageDto { Role = role, Content = "內容" }).ToList(),
        };

        var response = await client.PostAsJsonAsync($"{Base}/{id}/ai", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<ApiResult<object>>())!.Success);
        Assert.Empty(factory.Ai.Conversations);
        Assert.Equal(0, await CountAnyAuditAsync(admin.Id));
    }

    [Fact]
    public async Task Ai_WithATooLongQuestionOrAMissingConversationId_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var id = await SeedAsync(Unique("太長"), "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);

        var tooLong = await client.PostAsJsonAsync($"{Base}/{id}/ai", new ExceptionAiRequestDto
        {
            ConversationId = Guid.NewGuid(),
            Messages = [new() { Role = "assistant", Content = "報告" }, new() { Role = "user", Content = new string('問', 2001) }],
        });
        var blank = await client.PostAsJsonAsync($"{Base}/{id}/ai", new ExceptionAiRequestDto
        {
            ConversationId = Guid.NewGuid(),
            Messages = [new() { Role = "assistant", Content = "報告" }, new() { Role = "user", Content = "   " }],
        });
        var hugeReply = await client.PostAsJsonAsync($"{Base}/{id}/ai", new ExceptionAiRequestDto
        {
            ConversationId = Guid.NewGuid(),
            Messages = [new() { Role = "assistant", Content = new string('答', (512 * 1024) + 1) }, new() { Role = "user", Content = "問" }],
        });
        var noConversation = await client.PostAsJsonAsync($"{Base}/{id}/ai", new ExceptionAiRequestDto());

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hugeReply.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noConversation.StatusCode);
        Assert.Empty(factory.Ai.Conversations);
    }

    [Fact]
    public async Task Ai_ForAMissingRow_ShouldReturn404()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.PostAsJsonAsync($"{Base}/999999999/ai", new ExceptionAiRequestDto { ConversationId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Ai.Conversations);
    }

    /// <summary>使用者中途關窗或按「停止產生」：伺服器取消 AI 呼叫，稽核照樣在最後寫一筆（失敗原因 Canceled），與 Server 版相同。</summary>
    [Fact]
    public async Task Ai_WhenTheClientDisconnects_ShouldCancelTheCall_AndStillWriteTheAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = await SeedAsync(Unique("取消"), "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);
        factory.Ai.BlockUntilCanceled = true;

        // 保險：TestServer 若沒有邊寫邊送，讀取會一直等；最多等 15 秒就中斷（測試失敗而不是卡住）。
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{id}/ai")
            {
                Content = JsonContent.Create(new ExceptionAiRequestDto { ConversationId = Guid.NewGuid() }),
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            // 等到第一段文字（JSON 會把中文跳脫成 \uXXXX，所以解析後再比對）。
            while (await reader.ReadLineAsync(cts.Token) is { } line
                && JsonSerializer.Deserialize<AiStreamFrameDto>(line, Json)?.Text != "前半段")
            {
            }

            await cts.CancelAsync();
        }

        Assert.True(await factory.Ai.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var audit = await WaitForAuditAsync(AuditActions.ExceptionLog.AiAnalyze, admin.Id);
        Assert.False(audit.Success);
        Assert.EndsWith("；失敗原因 Canceled", audit.Detail);
    }

    [Fact]
    public async Task AiPdf_ShouldAddTheDetailOnTheServer_AndAuditTheExport()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = await SeedAsync(Unique("PDF"), "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);
        var request = new ExceptionAiPdfRequestDto
        {
            Messages =
            [
                new() { Role = "assistant", Content = "# 報告\n內容" },
                new() { Role = "user", Content = "為什麼？" },
                new() { Role = "assistant", Content = "因為", Interrupted = true },
            ],
            ModelName = "fake-model",
            Usage = new AiUsageDto { InputCount = 10, OutputCount = 2, TotalCount = 12 },
        };

        var response = await client.PostAsJsonAsync($"{Base}/{id}/ai/pdf", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^MyProject\.Web-ai-exception-report-\d{8}-\d{6}\.pdf$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("%PDF"u8.ToArray(), (await response.Content.ReadAsByteArrayAsync())[..4]);

        var audit = Assert.Single(await AuditsAsync(AuditActions.ExceptionLog.AiExportPdf, admin.Id));
        Assert.True(audit.Success);
        Assert.Matches(@"^追問第 1 輪；對話 4 則、\d+ 字元$", audit.Detail);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Base}/999999999/ai/pdf", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Base}/{id}/ai/pdf", new ExceptionAiPdfRequestDto())).StatusCode);
    }

    /// <summary>對話途中管理員調低了追問上限：已經產生的對話仍要能匯出（輪數上限管的是 AI 費用，匯出不花費用）。</summary>
    [Fact]
    public async Task AiPdf_WithMoreRoundsThanTheCurrentLimit_ShouldStillExport()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var id = await SeedAsync(Unique("PDF輪數"), "System.InvalidOperationException", ExceptionSources.Ui, minutesAgo: 1);
        var messages = new List<AiChatMessageDto> { new() { Role = "assistant", Content = "報告" } };
        for (var round = 1; round <= FakeExceptionAiService.FollowUpLimit + 1; round++)
        {
            messages.Add(new() { Role = "user", Content = $"第 {round} 問" });
            messages.Add(new() { Role = "assistant", Content = $"第 {round} 答" });
        }

        var response = await client.PostAsJsonAsync($"{Base}/{id}/ai/pdf", new ExceptionAiPdfRequestDto { Messages = messages, ModelName = "fake-model" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, $"{Base}/search", new ExceptionLogSearchRequestDto()),
            (HttpMethod.Get, $"{Base}/options", null),
            (HttpMethod.Get, $"{Base}/1", null),
            (HttpMethod.Get, $"{Base}/1/stack-trace", null),
            (HttpMethod.Post, $"{Base}/copy-text", new ExceptionLogSearchRequestDto()),
            (HttpMethod.Post, $"{Base}/export", new ExceptionLogSearchRequestDto()),
            (HttpMethod.Delete, $"{Base}/1", null),
            (HttpMethod.Post, $"{Base}/purge", null),
            (HttpMethod.Post, $"{Base}/clear-all", null),
            (HttpMethod.Post, $"{Base}/1/ai", new ExceptionAiRequestDto { ConversationId = Guid.NewGuid() }),
            (HttpMethod.Post, $"{Base}/1/ai/pdf", new ExceptionAiPdfRequestDto()),
        };

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        Assert.Empty(factory.Ai.Conversations);
        await using var context = await CreateContextAsync();
        Assert.Equal(calls.Length, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    /// <summary>經真的記錄管線寫一筆（含堆疊檔）；<paramref name="occurrences"/> 大於 1 時再記幾次同一筆，累加次數。</summary>
    private async Task<int> SeedAsync(string tag, string type, string source, int minutesAgo, int occurrences = 1)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ExceptionLogService>();
        ExceptionRecordOutcome? outcome = null;
        for (var i = 0; i < occurrences; i++)
        {
            outcome = await service.RecordAsync(new ExceptionLogEntry
            {
                ExceptionType = type,
                Message = $"{tag} 訊息",
                StackTrace = $"堆疊 {tag}",
                Source = source,
                Page = "/projects",
                Operation = "Save",
                LoggerName = "Tests",
                Account = "bob",
                UserId = 7,
                OccurredAt = DateTime.Now.AddMinutes(-minutesAgo),
                TraceId = "ABCD1234",
            });
        }

        return outcome!.Id;
    }

    private async Task<int> SeedWithoutStackAsync(string tag)
    {
        await using var context = await CreateContextAsync();
        var row = new ExceptionLog
        {
            Signature = Guid.NewGuid().ToString("N"),
            ExceptionType = "System.MissingStackException",
            Message = $"{tag} 沒有堆疊",
            Source = ExceptionSources.Ui,
            OccurrenceCount = 1,
            FirstOccurredAt = DateTime.Now,
            LastOccurredAt = DateTime.Now,
        };
        context.ExceptionLog.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<List<AiStreamFrameDto>> StreamAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode} {text}");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<AiStreamFrameDto>(line, Json)!).ToList();
    }

    private async Task<List<AuditLog>> AuditsAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.Where(x => x.Action == action && x.ActorUserId == actorId).ToListAsync();
    }

    private async Task<AuditLog> WaitForAuditAsync(string action, int actorId)
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await AuditsAsync(action, actorId)).SingleOrDefault() is { } audit)
            {
                return audit;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"等不到稽核 {action}。");
    }

    /// <summary>這個人寫下的例外紀錄稽核筆數（登入本身也會寫稽核，不算在內）。</summary>
    private async Task<int> CountAnyAuditAsync(int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.ActorUserId == actorId && x.Action.StartsWith("ExceptionLog."));
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
        => (await RawAsync<T>(client, method, url, body)).Data!;

    private static Task<ApiResult<object>> RawAsync(HttpClient client, HttpMethod method, string url, object? body = null)
        => RawAsync<object>(client, method, url, body);

    private static async Task<ApiResult<T>> RawAsync<T>(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, Json)!;
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = Unique("m6-");
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);
        return user;
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}

/// <summary>整合測試主機：記錄瀏覽類稽核（查詢、查看），AI 例外分析換成 <see cref="FakeExceptionAiService"/>。</summary>
public sealed class ApiTestApplicationFactoryWithFakeExceptionAi : ApiTestApplicationFactory
{
    public FakeExceptionAiService Ai { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditLogSettings:RecordBrowsing"] = "true",
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiExceptionAnalysisService>();
            services.AddSingleton<IAiExceptionAnalysisService>(Ai);
        });
    }
}

/// <summary>假的 AI 例外分析：記下每次收到的對話與情境；回兩段文字與固定結果，或（<see cref="BlockUntilCanceled"/>）送一段後卡住直到被取消。</summary>
public sealed class FakeExceptionAiService : IAiExceptionAnalysisService
{
    public const int FollowUpLimit = 2;

    public List<IReadOnlyList<AiChatMessage>> Conversations { get; } = [];

    public List<AiExceptionCallContext?> Contexts { get; } = [];

    public bool BlockUntilCanceled { get; set; }

    public TaskCompletionSource<bool> Canceled { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsAvailable => true;

    public string UnavailableReason => string.Empty;

    public int MaxFollowUpRounds => FollowUpLimit;

    public void Reset()
    {
        Conversations.Clear();
        Contexts.Clear();
        BlockUntilCanceled = false;
        Canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task<AiAnalysisResult> AskAsync(IReadOnlyList<AiChatMessage> conversation, AiExceptionCallContext? callContext = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("API 只用串流版。");

    public async IAsyncEnumerable<AiStreamUpdate> AskStreamingAsync(
        IReadOnlyList<AiChatMessage> conversation,
        AiExceptionCallContext? callContext = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Conversations.Add(conversation.ToList());
        Contexts.Add(callContext);

        if (BlockUntilCanceled)
        {
            yield return new AiStreamUpdate { TextDelta = "前半段" };
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult(true);
            }

            yield return new AiStreamUpdate
            {
                Result = AiAnalysisResult.Failure(AiAnalysisFailureReason.Canceled, "已取消本次 AI 分析。") with { Markdown = "前半段", IsInterrupted = true },
            };
            yield break;
        }

        yield return new AiStreamUpdate { TextDelta = "第一段" };
        yield return new AiStreamUpdate { TextDelta = "第二段" };
        yield return new AiStreamUpdate
        {
            Result = new AiAnalysisResult
            {
                Success = true,
                Markdown = "第一段第二段",
                ModelName = "fake-model",
                Usage = new AiTokenUsage { InputCount = 100, OutputCount = 20, TotalCount = 120 },
                Elapsed = TimeSpan.FromMilliseconds(1500),
            },
        };
    }
}
