using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// AI 對話紀錄頁的 Web API（0.10.5 起，WASM 遷移 M6）：只限管理員（<c>[AdminOnly]</c>），
/// 查詢（含內文搜尋）、選項、深連結、明細、完整 JSON、PDF（單筆與合併）、CSV、刪除、批次清除、清空；
/// 稽核的動作、目標、摘要與詳細內容和 Server 版頁面相同。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AiCallLogApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private const string BaseUrl = "/api/v1/ai-call-logs";
    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public AiCallLogApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Search_ShouldFilterAndSortLikeTheScreen_AndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("查詢");
        await SeedAsync(operation, elapsed: 300, minutesAgo: 3);
        await SeedAsync(operation, elapsed: 100, minutesAgo: 2);
        await SeedAsync(operation, elapsed: 200, minutesAgo: 1, success: false);

        var newest = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search", new AiCallLogSearchRequestDto { Operation = operation });
        var ascending = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search",
            new AiCallLogSearchRequestDto { Operation = operation, SortBy = "ElapsedMilliseconds", SortDescending = false });
        var descending = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search",
            new AiCallLogSearchRequestDto { Operation = operation, SortBy = "ElapsedMilliseconds", SortDescending = true });
        var failed = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search",
            new AiCallLogSearchRequestDto { Operation = operation, Success = false, RecordSearch = true });

        Assert.Equal([200L, 100L, 300L], newest.Items.Select(x => x.ElapsedMilliseconds));
        // 排序修正：Server 版比對 "ascend"/"descend"，排序從來沒生效；WASM 版依點選方向排序。
        Assert.Equal([100L, 200L, 300L], ascending.Items.Select(x => x.ElapsedMilliseconds));
        Assert.Equal([300L, 200L, 100L], descending.Items.Select(x => x.ElapsedMilliseconds));
        Assert.Equal(200L, Assert.Single(failed.Items).ElapsedMilliseconds);
        Assert.Null(newest.ContentSearch);
        Assert.Equal(3, newest.TotalCount);

        var audit = Assert.Single(await AuditsAsync(AuditActions.AiCallLog.Search, admin.Id));
        Assert.Equal("AiCallLog", audit.TargetType);
        Assert.Equal(
            $"start=（空）; end=（空）; operation={operation}; success=false; model=（空）; hasAccount=False; hasKeyword=False; hasContent=False",
            audit.Detail);
    }

    [Fact]
    public async Task Search_WithAContentKeyword_ShouldScanTheContentFiles_ThenPageByTheMatchedIds()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var operation = Unique("內文");
        var matched = await SeedAsync(operation, response: "答案裡有 Needle 這個字", minutesAgo: 2);
        await SeedAsync(operation, response: "沒有關鍵字", minutesAgo: 1);

        var scanned = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search",
            new AiCallLogSearchRequestDto { Operation = operation, ContentKeyword = " needle " });
        var none = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search",
            new AiCallLogSearchRequestDto { Operation = operation, ContentIds = [] });

        Assert.Equal([matched], scanned.ContentSearch!.Ids);
        Assert.Equal(2, scanned.ContentSearch.Scanned);
        Assert.False(scanned.ContentSearch.Truncated);
        Assert.Equal([matched], scanned.Items.Select(x => x.Id));
        Assert.Equal(0, none.TotalCount);
        Assert.Equal(AiCallLogService.ContentScanLimit, AiCallLogSearchRequestDto.MaxContentIds);
    }

    [Fact]
    public async Task Search_WithInvalidPagingOrTooManyIds_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{BaseUrl}/search", new AiCallLogSearchRequestDto { PageSize = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{BaseUrl}/search",
            new AiCallLogSearchRequestDto { ContentIds = Enumerable.Range(1, AiCallLogSearchRequestDto.MaxContentIds + 1).ToList() })).StatusCode);
    }

    [Fact]
    public async Task LocalTimes_ShouldBeSentWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var operation = Unique("時差");
        var id = await SeedAsync(operation, minutesAgo: 1, conversationId: Guid.NewGuid());

        var texts = new[]
        {
            await (await client.PostAsJsonAsync($"{BaseUrl}/search", new AiCallLogSearchRequestDto { Operation = operation })).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"{BaseUrl}/{id}")).Content.ReadAsStringAsync(),
        };

        var offset = new System.Text.RegularExpressions.Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)");
        var envelope = new System.Text.RegularExpressions.Regex("\"timestamp\":\"[^\"]*\"");
        Assert.All(texts, text => Assert.False(offset.IsMatch(envelope.Replace(text, string.Empty)), text));

        var page = await SendAsync<AiCallLogPageDto>(client, HttpMethod.Post, $"{BaseUrl}/search", new AiCallLogSearchRequestDto { Operation = operation });
        Assert.Equal(DateTimeKind.Unspecified, page.Items.Single().OccurredAt.Kind);
    }

    [Fact]
    public async Task Options_ShouldListOperationsModelsAndSettings()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var operation = Unique("選項");
        await SeedAsync(operation, minutesAgo: 1, model: "gpt-options");

        var options = await SendAsync<AiCallLogOptionsDto>(client, HttpMethod.Get, $"{BaseUrl}/options");

        Assert.Contains(operation, options.Operations);
        Assert.Contains("gpt-options", options.Models);
        Assert.True(options.Enabled);
        Assert.True(options.RetentionDays > 0);
        Assert.Equal(AiCallLogService.ContentScanLimit, options.ContentScanLimit);
    }

    [Fact]
    public async Task ByCall_ShouldFindTheRecordId_Or404()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var callId = Guid.NewGuid();
        var id = await SeedAsync(Unique("深連結"), minutesAgo: 1, callId: callId);

        Assert.Equal(id, await SendAsync<int>(client, HttpMethod.Get, $"{BaseUrl}/by-call/{callId}"));

        var missing = await client.GetAsync($"{BaseUrl}/by-call/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("找不到這次呼叫的對話紀錄", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_ShouldReturnMetadataTheFiveViewsAndSiblings_AndWriteOneViewAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("明細");
        var conversationId = Guid.NewGuid();
        var callId = Guid.NewGuid();
        var longPrompt = new string('長', AiCallLogDetailDto.MaxTextCharacters + 5);
        var first = await SeedAsync(operation, minutesAgo: 2, conversationId: conversationId);
        var id = await SeedAsync(operation, minutesAgo: 1, conversationId: conversationId, callId: callId,
            request: JsonSerializer.Serialize(new { model = "gpt-test", messages = new object[] { new { role = "system", content = longPrompt }, new { role = "user", content = "問題" } } }),
            response: "## 回答");
        await using (var context = await CreateContextAsync())
        {
            context.TokenUsageLog.Add(new TokenUsageLog
            {
                OccurredAt = DateTime.Now,
                Operation = operation,
                CallKind = "Chat",
                Provider = "OpenAI",
                Model = "gpt-test",
                InputCount = 1200,
                OutputCount = 300,
                TotalCount = 1500,
                Success = true,
                CallId = callId,
            });
            await context.SaveChangesAsync();
        }

        var detail = await SendAsync<AiCallLogDetailDto>(client, HttpMethod.Get, $"{BaseUrl}/{id}");

        Assert.Equal(id, detail.Item.Id);
        Assert.True(detail.HasContent);
        Assert.Equal("https://api.example.test/v1/chat/completions", detail.Endpoint);
        var messages = detail.Messages!;
        Assert.Equal(["system", "user"], messages.Select(x => x.Role));
        Assert.Equal(AiCallLogDetailDto.MaxTextCharacters, messages[0].Content.Text.Length);
        Assert.Equal(longPrompt.Length, messages[0].Content.Length);
        Assert.Equal("問題", messages[1].Content.Text);
        Assert.Equal("## 回答", detail.Response!.Text);
        Assert.StartsWith("{", detail.RawRequest!.Text);
        Assert.Contains("\n", detail.RawRequest.Text);
        Assert.Null(detail.RawResponse);
        Assert.StartsWith("輸入 1,200／輸出 300／合計 1,500，", detail.UsageText);
        Assert.Equal([first, id], detail.Conversation.Select(x => x.Id));
        Assert.Empty(detail.Run);

        var audit = Assert.Single(await AuditsAsync(AuditActions.AiCallLog.View, admin.Id));
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Null(audit.Detail);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BaseUrl}/999999999")).StatusCode);
    }

    [Fact]
    public async Task Text_ShouldReturnTheWholeBlock_ForCopyingATruncatedSection()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var longPrompt = new string('長', AiCallLogDetailDto.MaxTextCharacters + 5);
        var id = await SeedAsync(Unique("複製"), minutesAgo: 1,
            request: JsonSerializer.Serialize(new { messages = new object[] { new { role = "user", content = longPrompt } } }), response: "回答");

        var message = await SendAsync<AiCallLogTextDto>(client, HttpMethod.Get, $"{BaseUrl}/{id}/text?part=message-0");
        var response = await SendAsync<AiCallLogTextDto>(client, HttpMethod.Get, $"{BaseUrl}/{id}/text?part=response");
        var raw = await SendAsync<AiCallLogTextDto>(client, HttpMethod.Get, $"{BaseUrl}/{id}/text?part=raw-request");

        Assert.Equal(longPrompt, message.Text);
        Assert.Equal(longPrompt.Length, message.Length);
        Assert.Equal("回答", response.Text);
        Assert.Contains(longPrompt, raw.Text);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{BaseUrl}/{id}/text?part=message-9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BaseUrl}/999999999/text?part=response")).StatusCode);
        // 與 Server 版的「複製」相同，不另寫稽核。
        Assert.Empty(await AuditsAsync(AuditActions.AiCallLog.View, admin.Id));
    }

    [Fact]
    public async Task Raw_ShouldDownloadTheContentFileWithoutABom_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("下載");
        var callId = Guid.NewGuid();
        var id = await SeedAsync(operation, minutesAgo: 1, callId: callId, response: "中文回答");

        var response = await client.PostAsync($"{BaseUrl}/{id}/raw", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"MyProject.Web-ai-call-{callId:N}.json", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEqual(0xEF, bytes[0]);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("中文回答", json);
        Assert.Equal(callId, JsonSerializer.Deserialize<AiCallLogEntry>(json)!.CallId);

        var audit = Assert.Single(await AuditsAsync(AuditActions.AiCallLog.Export, admin.Id));
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal($"匯出 AI 對話紀錄（json）：{operation}", audit.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{BaseUrl}/999999999/raw", null)).StatusCode);
    }

    [Fact]
    public async Task Pdf_ShouldExportOneRecord_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("PDF");
        var id = await SeedAsync(operation, minutesAgo: 1, response: "回答");

        var response = await client.PostAsync($"{BaseUrl}/{id}/pdf", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches($@"^MyProject\.Web-ai-call-{id}-\d{{8}}-\d{{6}}\.pdf$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await response.Content.ReadAsByteArrayAsync())[..4]));
        var audit = Assert.Single(await AuditsAsync(AuditActions.AiCallLog.Export, admin.Id));
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal($"匯出 AI 對話紀錄（pdf）：{operation}", audit.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{BaseUrl}/999999999/pdf", null)).StatusCode);
    }

    [Fact]
    public async Task MergedPdf_ShouldExportSelectedOrQueriedRecords_AuditTheIds_AndRejectMoreThan50()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("合併");
        var older = await SeedAsync(operation, minutesAgo: 2);
        var newer = await SeedAsync(operation, minutesAgo: 1);

        var selected = await client.PostAsJsonAsync($"{BaseUrl}/merged-pdf", new AiCallLogMergedPdfRequestDto { Source = "selected", Ids = [newer, older] });
        var queried = await client.PostAsJsonAsync($"{BaseUrl}/merged-pdf",
            new AiCallLogMergedPdfRequestDto { Source = "query", Query = new AiCallLogSearchRequestDto { Operation = operation } });
        var tooMany = await client.PostAsJsonAsync($"{BaseUrl}/merged-pdf",
            new AiCallLogMergedPdfRequestDto { Source = "selected", Ids = Enumerable.Range(1, 51).ToList() });
        var unknown = await client.PostAsJsonAsync($"{BaseUrl}/merged-pdf", new AiCallLogMergedPdfRequestDto { Source = "selected", Ids = [999999999] });

        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal("application/pdf", selected.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^MyProject\.Web-ai-calls-2-\d{8}-\d{6}\.pdf$", selected.Content.Headers.ContentDisposition?.FileNameStar ?? selected.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(HttpStatusCode.OK, queried.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("共 51 筆，合併匯出一次最多 50 筆；請縮小範圍。", await tooMany.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("找不到要匯出的紀錄（可能已被清除），請重新整理。", await unknown.Content.ReadAsStringAsync());

        var audits = await AuditsAsync(AuditActions.AiCallLog.Export, admin.Id);
        Assert.Equal(["format=pdf-merged; source=selected; rows=2", "format=pdf-merged; source=query; rows=2"], audits.Select(x => x.Detail));
        Assert.All(audits, x => Assert.Equal("*", x.TargetId));
        using var scope = factory.Services.CreateScope();
        var content = await scope.ServiceProvider.GetRequiredService<AuditLogQueryService>().GetContentAsync(audits[0].Id);
        Assert.Equal($"ids={older},{newer}", content);
    }

    [Fact]
    public async Task MergedPdf_FromAQueryWithMoreThan50Rows_ShouldReturnTheSameMessageAsTheScreen()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var operation = Unique("過多");
        for (var index = 0; index < 51; index++)
        {
            await SeedAsync(operation, minutesAgo: 1);
        }

        var response = await client.PostAsJsonAsync($"{BaseUrl}/merged-pdf",
            new AiCallLogMergedPdfRequestDto { Source = "query", Query = new AiCallLogSearchRequestDto { Operation = operation } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("共 51 筆，合併匯出一次最多 50 筆；請縮小日期或其他條件，或勾選要匯出的紀錄。", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_ShouldReturnACsvInTheScreenOrder_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("匯出");
        await SeedAsync(operation, elapsed: 900, minutesAgo: 2);
        await SeedAsync(operation, elapsed: 100, minutesAgo: 1);

        var response = await client.PostAsJsonAsync($"{BaseUrl}/export",
            new AiCallLogSearchRequestDto { Operation = operation, SortBy = "ElapsedMilliseconds", SortDescending = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^MyProject\.Web-ai-call-logs-\d{8}-\d{6}\.csv$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("送出時間（本地）", lines[0]);
        Assert.Contains(",\"100\",", lines[1]);
        Assert.Contains(",\"900\",", lines[2]);
        Assert.Equal("format=csv; rows=2", Assert.Single(await AuditsAsync(AuditActions.AiCallLog.Export, admin.Id)).Detail);
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheRecord_AndWriteADeleteAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("刪除");
        var id = await SeedAsync(operation, minutesAgo: 1);

        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{id}");

        await using var context = await CreateContextAsync();
        Assert.False(await context.AiCallLog.AnyAsync(x => x.Id == id));
        var audit = Assert.Single(await AuditsAsync(AuditActions.AiCallLog.Delete, admin.Id));
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal($"刪除 AI 對話紀錄：{operation}", audit.Detail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{BaseUrl}/{id}")).StatusCode);
    }

    [Fact]
    public async Task PurgeAndClearAll_ShouldWorkLikeTheScreen_AndAuditThemselves()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var operation = Unique("清除");
        await SeedAsync(operation, occurredAt: new DateTime(2001, 1, 1, 9, 0, 0));

        var purged = await client.PostAsJsonAsync($"{BaseUrl}/purge", new AiCallLogPurgeRequestDto { BeforeDate = new DateTime(2001, 1, 2) });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Contains("已清除 1 筆紀錄。", await purged.Content.ReadAsStringAsync());
        Assert.Equal(
            "清除 2001-01-02 之前的 AI 對話紀錄：已清除 1 筆紀錄。",
            Assert.Single(await AuditsAsync(AuditActions.AiCallLog.Purge, admin.Id)).Detail);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{BaseUrl}/purge", new AiCallLogPurgeRequestDto())).StatusCode);

        await SeedAsync(operation, minutesAgo: 1);
        await SendAsync<object>(client, HttpMethod.Post, $"{BaseUrl}/clear-all");
        await using var context = await CreateContextAsync();
        Assert.Equal(0, await context.AiCallLog.CountAsync());
        var clear = Assert.Single(await AuditsAsync(AuditActions.AiCallLog.ClearAll, admin.Id));
        Assert.StartsWith("清空全部 AI 對話紀錄：已清空 ", clear.Detail);
        Assert.Equal("*", clear.TargetId);
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, $"{BaseUrl}/search", new AiCallLogSearchRequestDto()),
            (HttpMethod.Get, $"{BaseUrl}/options", null),
            (HttpMethod.Get, $"{BaseUrl}/by-call/{Guid.NewGuid()}", null),
            (HttpMethod.Get, $"{BaseUrl}/1", null),
            (HttpMethod.Get, $"{BaseUrl}/1/text?part=response", null),
            (HttpMethod.Post, $"{BaseUrl}/1/raw", null),
            (HttpMethod.Post, $"{BaseUrl}/1/pdf", null),
            (HttpMethod.Post, $"{BaseUrl}/merged-pdf", new AiCallLogMergedPdfRequestDto { Source = "selected", Ids = [1] }),
            (HttpMethod.Post, $"{BaseUrl}/export", new AiCallLogSearchRequestDto()),
            (HttpMethod.Delete, $"{BaseUrl}/1", null),
            (HttpMethod.Post, $"{BaseUrl}/purge", new AiCallLogPurgeRequestDto { BeforeDate = new DateTime(2001, 1, 2) }),
            (HttpMethod.Post, $"{BaseUrl}/clear-all", null),
        };

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        await using var context = await CreateContextAsync();
        Assert.Equal(calls.Length, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    /// <summary>經 <see cref="AiCallLogService.RecordAsync"/> 建一筆（內容檔＋資料列），回傳紀錄 Id。</summary>
    private async Task<int> SeedAsync(
        string operation,
        long elapsed = 500,
        int minutesAgo = 1,
        bool success = true,
        string model = "gpt-test",
        string response = "回答",
        string? request = null,
        Guid? callId = null,
        Guid? conversationId = null,
        DateTime? occurredAt = null)
    {
        var id = callId ?? Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AiCallLogService>().RecordAsync(new AiCallLogEntry
            {
                CallId = id,
                OccurredAt = occurredAt ?? DateTime.Now.AddMinutes(-minutesAgo),
                Operation = operation,
                Provider = "OpenAI",
                RequestedModel = model,
                Model = model,
                Account = "support",
                UserId = 1,
                Endpoint = "https://api.example.test/v1/chat/completions",
                RelatedInfo = $"{operation} 關聯",
                ConversationId = conversationId,
                RequestBody = request ?? JsonSerializer.Serialize(new { model, messages = new object[] { new { role = "user", content = "問題" } } }),
                HttpStatus = success ? 200 : 500,
                ResponseBody = null,
                ResponseText = success ? response : string.Empty,
                FinishReason = success ? "stop" : null,
                Success = success,
                FailureReason = success ? null : "HttpError",
                ElapsedMilliseconds = elapsed,
            });
        }

        await using var context = await CreateContextAsync();
        return await context.AiCallLog.Where(x => x.CallId == id).Select(x => x.Id).SingleAsync();
    }

    private async Task<List<AuditLog>> AuditsAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.Where(x => x.Action == action && x.ActorUserId == actorId).OrderBy(x => x.Id).ToListAsync();
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
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
