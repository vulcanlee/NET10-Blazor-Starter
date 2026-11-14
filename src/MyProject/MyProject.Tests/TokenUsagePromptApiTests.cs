using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// Token 用量與 AI 提示詞頁的 Web API（0.10.5 起，WASM 遷移 M6）：只限管理員（<c>[AdminOnly]</c>），
/// 稽核寫法與 Server 版頁面相同（每個動作一筆）；本地時間不帶時差送出；Token 用量的排序修正（匯出與畫面同序）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class TokenUsagePromptApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public TokenUsagePromptApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    // ───────────────────────────── Token 用量 ─────────────────────────────

    [Fact]
    public async Task Dashboard_ShouldReturnPageTotalsGroupsTrendAndCards_AndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("儀表");
        var today = DateTime.Today;
        await SeedUsageAsync(tag, today.AddHours(9), total: 100, costTwd: 1.5);
        await SeedUsageAsync(tag, today.AddDays(-3).AddHours(9), total: 200, costTwd: 2.5);
        await SeedUsageAsync(tag, today.AddDays(-40).AddHours(9), total: 400, costTwd: null);

        var data = await SendAsync<TokenUsageDashboardDto>(client, HttpMethod.Post, "/api/v1/token-usage/dashboard",
            new TokenUsageSearchRequestDto { Operation = tag, PageSize = 2 });

        Assert.Equal(3, data.Page.TotalCount);
        Assert.Equal(2, data.Page.Items.Count);
        Assert.Equal(today.AddHours(9), data.Page.Items[0].OccurredAt);
        Assert.Equal(3, data.Summary.CallCount);
        Assert.Equal(700, data.Summary.TotalCount);
        Assert.Equal(1, data.Summary.UnpricedCount);
        Assert.Equal(tag, Assert.Single(data.ByOperation).Key);
        Assert.Single(data.ByModel);
        // 起訖日都沒填時，每日趨勢退回近 30 天（伺服器的今天），已補零。
        Assert.Equal(30, data.Daily.Count);
        Assert.Equal(today, data.Daily[^1].Date);
        Assert.Equal(today.AddDays(-29), data.Daily[0].Date);
        Assert.False(data.IsTrendTruncated);
        Assert.Equal([1, 7, 30], data.RangeCards.Select(x => x.Days));
        Assert.Equal([1, 2, 2], data.RangeCards.Select(x => x.CallCount));
        Assert.Equal([today, today.AddDays(-6), today.AddDays(-29)], data.RangeCards.Select(x => x.Date));
        Assert.All(data.RangeCards, card => Assert.Equal(today, card.End));
        Assert.Equal(4.0, data.RangeCards[1].CostTwd, 6);
        Assert.Equal(2, data.Quota.Count);
        Assert.StartsWith("全系統每日：今日 NT$ ", data.Quota[0].Text);
        Assert.False(string.IsNullOrEmpty(data.PerUserDailyLimit));
        Assert.Equal(0, await CountAuditAsync(AuditActions.TokenUsage.Search, admin.Id));

        await SendAsync<TokenUsageDashboardDto>(client, HttpMethod.Post, "/api/v1/token-usage/dashboard",
            new TokenUsageSearchRequestDto { Operation = tag, Account = "某人", RecordSearch = true });

        var audit = await SingleAuditAsync(AuditActions.TokenUsage.Search, admin.Id);
        Assert.Equal("TokenUsage", audit.TargetType);
        Assert.Contains($"operation={tag}", audit.Detail);
        Assert.Contains("hasAccount=True", audit.Detail);
    }

    [Fact]
    public async Task Dashboard_WithARangeLongerThan180Days_ShouldKeepTheLast180()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("截斷");
        await SeedUsageAsync(tag, DateTime.Today.AddHours(1), total: 10, costTwd: 0.1);

        var data = await SendAsync<TokenUsageDashboardDto>(client, HttpMethod.Post, "/api/v1/token-usage/dashboard",
            new TokenUsageSearchRequestDto { Operation = tag, StartDate = DateTime.Today.AddDays(-200), EndDate = DateTime.Today });

        Assert.True(data.IsTrendTruncated);
        Assert.Equal(180, data.Daily.Count);
        Assert.Equal(DateTime.Today, data.Daily[^1].Date);
    }

    /// <summary>排序修正：Server 版比對 "ascend"/"descend" 永遠不生效；API 依 SortBy／SortDescending 排序，CSV 與畫面同序。</summary>
    [Fact]
    public async Task Sorting_ShouldFollowTheRequest_AndTheCsvShouldUseTheSameOrder()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("排序");
        var now = DateTime.Today.AddHours(8);
        await SeedUsageAsync(tag, now.AddMinutes(1), total: 10, costTwd: 3, model: "m-b");
        await SeedUsageAsync(tag, now.AddMinutes(2), total: 20, costTwd: 1, model: "m-a");
        await SeedUsageAsync(tag, now.AddMinutes(3), total: 30, costTwd: 2, model: "m-c");

        async Task<IEnumerable<double?>> CostsAsync(string? sortBy, bool descending)
            => (await SendAsync<TokenUsageDashboardDto>(client, HttpMethod.Post, "/api/v1/token-usage/dashboard",
                new TokenUsageSearchRequestDto { Operation = tag, SortBy = sortBy, SortDescending = descending })).Page.Items.Select(x => x.CostTwd);

        Assert.Equal([1d, 2d, 3d], await CostsAsync("CostTwd", descending: false));
        Assert.Equal([3d, 2d, 1d], await CostsAsync("costtwd", descending: true));
        Assert.Equal([2d, 1d, 3d], await CostsAsync("OccurredAt", descending: true));
        Assert.Equal([3d, 1d, 2d], await CostsAsync("OccurredAt", descending: false));
        // 沒指定或不認得的欄位：預設時間由新到舊。
        Assert.Equal([2d, 1d, 3d], await CostsAsync("Model", descending: false));

        var csv = await client.PostAsJsonAsync("/api/v1/token-usage/export", new TokenUsageSearchRequestDto { Operation = tag, SortBy = "CostTwd" });
        var lines = Encoding.UTF8.GetString((await csv.Content.ReadAsByteArrayAsync())[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["m-a", "m-c", "m-b"], lines.Skip(1).Select(line => line.Split(',')[5].Trim('"')));
    }

    /// <summary>本地時間以「不帶時差」送出：帶了 +08:00，WASM 會換成瀏覽器的時區。</summary>
    [Fact]
    public async Task LocalTimes_ShouldBeSentWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("時差");
        var id = await SeedUsageAsync(tag, DateTime.Now, total: 5, costTwd: 0.01);
        await SendAsync<PromptTemplateSaveResultDto>(client, HttpMethod.Post, "/api/v1/prompt-templates/AdminAssistantOps/versions",
            new PromptTemplateSaveRequestDto { Content = "時差測試", ExpectedActiveVersion = await ActiveVersionAsync(client, PromptTemplateKeys.AdminAssistantOps) });

        var offset = new Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)");
        var texts = new[]
        {
            await (await client.PostAsJsonAsync("/api/v1/token-usage/dashboard", new TokenUsageSearchRequestDto { Operation = tag })).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/v1/token-usage/{id}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/v1/prompt-templates")).Content.ReadAsStringAsync(),
        };

        // ApiResult 外層的 timestamp 是回應時間、不顯示在畫面上，不在檢查範圍。
        var envelope = new Regex("\"timestamp\":\"[^\"]*\"");
        Assert.All(texts, text => Assert.False(offset.IsMatch(envelope.Replace(text, string.Empty)), text));
        Assert.Contains("\"createdAt\":\"", texts[2]);
    }

    [Fact]
    public async Task Options_ShouldListTheValuesInTheTable()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("選項");
        await SeedUsageAsync(tag, DateTime.Now, total: 1, costTwd: null, model: $"{tag}-model");

        var options = await SendAsync<TokenUsageOptionsDto>(client, HttpMethod.Get, "/api/v1/token-usage/options");

        Assert.Contains(tag, options.Operations);
        Assert.Contains($"{tag}-model", options.Models);
        Assert.Contains("Chat", options.CallKinds);
    }

    [Fact]
    public async Task Detail_ShouldFlattenTheRawUsageAndRateSnapshot_AndWriteOneViewAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("明細");
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TokenUsageLogService>().RecordAsync(new TokenUsageEntry
            {
                Operation = tag,
                CallKind = TokenUsageCallKinds.Chat,
                Provider = "OpenAI",
                Model = "gpt-test",
                InputCount = 1200,
                OutputCount = 30,
                TotalCount = 1230,
                Success = true,
                CallId = Guid.NewGuid(),
                RawUsageJson = """{"prompt_tokens":1200,"prompt_tokens_details":{"cached_tokens":null}}""",
            });
        }

        int id;
        await using (var context = await CreateContextAsync())
        {
            var row = await context.TokenUsageLog.SingleAsync(x => x.Operation == tag);
            row.CostRateSnapshot = """{"input":0.5,"output":2}""";
            await context.SaveChangesAsync();
            id = row.Id;
        }

        var detail = await SendAsync<TokenUsageDetailDto>(client, HttpMethod.Get, $"/api/v1/token-usage/{id}");

        Assert.Equal(id, detail.Item.Id);
        Assert.Equal(tag, detail.Item.Operation);
        Assert.Equal([("prompt_tokens", "1.2K"), ("prompt_tokens_details.cached_tokens", "—")], detail.RawRows.Select(x => (x.Name, x.Value)));
        Assert.Equal(string.Empty, detail.RawMessage);
        Assert.Equal([("input", "0.5"), ("output", "2")], detail.CostRateRows.Select(x => (x.Name, x.Value)));
        Assert.False(detail.HasConversation);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/token-usage/999999999")).StatusCode);
        var audit = await SingleAuditAsync(AuditActions.TokenUsage.View, admin.Id);
        Assert.Equal(id.ToString(), audit.TargetId);
    }

    [Fact]
    public async Task Detail_WithoutARawFile_ShouldExplainWhy()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var id = await SeedUsageAsync(Unique("無檔"), DateTime.Now, total: 1, costTwd: null);

        var detail = await SendAsync<TokenUsageDetailDto>(client, HttpMethod.Get, $"/api/v1/token-usage/{id}");

        Assert.Empty(detail.RawRows);
        Assert.Equal("原始明細不存在（可能已被清除，或當初這次呼叫沒有回傳用量）。", detail.RawMessage);
        Assert.Empty(detail.CostRateRows);
    }

    [Fact]
    public async Task ExportCsv_ShouldReturnAllRowsWithBom_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("匯出");
        await SeedUsageAsync(tag, DateTime.Now.AddMinutes(-2), total: 1, costTwd: 0.5);
        await SeedUsageAsync(tag, DateTime.Now.AddMinutes(-1), total: 2, costTwd: null);

        var response = await client.PostAsJsonAsync("/api/v1/token-usage/export", new TokenUsageSearchRequestDto { Operation = tag, PageSize = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^MyProject\.Web-llm-usage-\d{8}-\d{6}\.csv$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("時間,使用者,作業,型別,供應商,模型", lines[0]);
        var audit = await SingleAuditAsync(AuditActions.TokenUsage.Export, admin.Id);
        Assert.Equal("*", audit.TargetId);
        Assert.Equal("format=csv; rows=2", audit.Detail);
    }

    [Fact]
    public async Task ExportPdf_ShouldBuildTheWholeReportOrOneTab_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("報表");
        await SeedUsageAsync(tag, DateTime.Now.AddMinutes(-1), total: 10, costTwd: 0.5);

        var all = await client.PostAsJsonAsync("/api/v1/token-usage/export-pdf", new TokenUsagePdfExportRequestDto { Operation = tag });
        var model = await client.PostAsJsonAsync("/api/v1/token-usage/export-pdf", new TokenUsagePdfExportRequestDto { Operation = tag, Tab = "model" });
        var invalid = await client.PostAsJsonAsync("/api/v1/token-usage/export-pdf", new TokenUsagePdfExportRequestDto { Operation = tag, Tab = "nope" });

        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.Equal("application/pdf", all.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await all.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Matches(@"^MyProject\.Web-llm-usage-\d{8}-\d{6}\.pdf$", FileName(all));
        Assert.Matches(@"^MyProject\.Web-llm-usage-model-\d{8}-\d{6}\.pdf$", FileName(model));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await using var context = await CreateContextAsync();
        var details = await context.AuditLog.Where(x => x.Action == AuditActions.TokenUsage.Export && x.ActorUserId == admin.Id)
            .OrderBy(x => x.Id).Select(x => x.Detail).ToListAsync();
        Assert.Equal(["format=pdf; scope=All; rows=1", "format=pdf; scope=Model; rows=0"], details);
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheRow_AndAuditEachDeletion()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = await SeedUsageAsync(Unique("刪除"), DateTime.Now, total: 1, costTwd: null);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/token-usage/{id}")).StatusCode);
        var again = await client.DeleteAsync($"/api/v1/token-usage/{id}");

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("找不到要刪除的用量紀錄。", (await again.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        var audit = await SingleAuditAsync(AuditActions.TokenUsage.Delete, admin.Id);
        Assert.Equal(id.ToString(), audit.TargetId);
        Assert.Equal("刪除一筆用量紀錄", audit.Detail);
    }

    [Fact]
    public async Task PurgeAndClearAll_ShouldWorkLikeTheScreen_AndAuditThemselves()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("清除");
        var before = DateTime.Today.AddDays(-500);
        await SeedUsageAsync(tag, before.AddDays(-1), total: 1, costTwd: null);
        await SeedUsageAsync(tag, before.AddDays(-2), total: 1, costTwd: null);
        await SeedUsageAsync(tag, DateTime.Now, total: 1, costTwd: null);

        var purge = await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/token-usage/purge", new TokenUsagePurgeRequestDto { BeforeDate = before });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/token-usage/purge", new TokenUsagePurgeRequestDto())).StatusCode);

        Assert.Equal("已清除 2 筆紀錄。", purge.Message);
        await using (var context = await CreateContextAsync())
        {
            Assert.Equal(1, await context.TokenUsageLog.CountAsync(x => x.Operation == tag));
        }

        var purgeAudit = await SingleAuditAsync(AuditActions.TokenUsage.Purge, admin.Id);
        Assert.Equal($"清除 {before:yyyy-MM-dd} 之前的用量紀錄：已清除 2 筆紀錄。", purgeAudit.Detail);

        var clear = await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/token-usage/clear-all");
        Assert.StartsWith("已清空 ", clear.Message);
        await using (var context = await CreateContextAsync())
        {
            Assert.Equal(0, await context.TokenUsageLog.CountAsync());
        }

        var clearAudit = await SingleAuditAsync(AuditActions.TokenUsage.ClearAll, admin.Id);
        Assert.Equal($"清空全部用量紀錄：{clear.Message}", clearAudit.Detail);
    }

    // ───────────────────────────── AI 提示詞 ─────────────────────────────

    [Fact]
    public async Task PromptTemplates_ShouldListEveryKindWithTitleDefaultAndLockedRules()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var templates = await SendAsync<List<PromptTemplateDto>>(client, HttpMethod.Get, "/api/v1/prompt-templates");

        Assert.Equal(PromptTemplateKeys.All, templates.Select(x => x.Key));
        Assert.Equal("日誌分析", templates[0].Title);
        Assert.Equal("管理助理／系統設定與維運", templates[^1].Title);
        Assert.All(templates, template =>
        {
            Assert.Equal(AiPromptGuardrails.DefaultInstructions(template.Key), template.DefaultInstructions);
            Assert.Equal(AiPromptGuardrails.For(template.Key), template.LockedRules);
        });
    }

    [Fact]
    public async Task SaveVersion_ShouldUseTheSignedInAccount_DetectConflicts_AndAuditOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        const string key = PromptTemplateKeys.AdminAssistantAccounts;
        var expected = await ActiveVersionAsync(client, key);

        var saved = await SendAsync<PromptTemplateSaveResultDto>(client, HttpMethod.Post, $"/api/v1/prompt-templates/{key}/versions",
            new PromptTemplateSaveRequestDto { Content = "  新的分析指示  ", Note = "測試", ExpectedActiveVersion = expected });
        var stale = await client.PostAsJsonAsync($"/api/v1/prompt-templates/{key}/versions",
            new PromptTemplateSaveRequestDto { Content = "晚一步的修改", ExpectedActiveVersion = expected });
        var empty = await client.PostAsJsonAsync($"/api/v1/prompt-templates/{key}/versions",
            new PromptTemplateSaveRequestDto { Content = "  ", ExpectedActiveVersion = saved.Version });
        var unknown = await client.PostAsJsonAsync("/api/v1/prompt-templates/NoSuchKey/versions", new PromptTemplateSaveRequestDto { Content = "x" });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(PromptTemplateService.ConflictMessage, (await stale.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var version = (await SendAsync<List<PromptTemplateDto>>(client, HttpMethod.Get, "/api/v1/prompt-templates"))
            .Single(x => x.Key == key).Versions.Single(x => x.Version == saved.Version);
        Assert.True(version.IsActive);
        Assert.Equal("新的分析指示", version.Content);
        Assert.Equal(admin.Account, version.CreatedByAccount);
        Assert.Equal("測試", version.Note);
        var audit = await SingleAuditAsync(AuditActions.Prompt.Update, admin.Id);
        Assert.Equal("PromptTemplate", audit.TargetType);
        Assert.Equal(key, audit.TargetId);
        Assert.Equal($"version={saved.Version}", audit.Detail);
    }

    [Fact]
    public async Task Activate_ShouldSwitchToAVersionOrTheDefault_AndAuditEachSwitch()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        const string key = PromptTemplateKeys.AdminAssistantMonitoring;
        var first = await SendAsync<PromptTemplateSaveResultDto>(client, HttpMethod.Post, $"/api/v1/prompt-templates/{key}/versions",
            new PromptTemplateSaveRequestDto { Content = "第一版", ExpectedActiveVersion = await ActiveVersionAsync(client, key) });
        await SendAsync<PromptTemplateSaveResultDto>(client, HttpMethod.Post, $"/api/v1/prompt-templates/{key}/versions",
            new PromptTemplateSaveRequestDto { Content = "第二版", ExpectedActiveVersion = first.Version });

        await SendAsync<object>(client, HttpMethod.Post, $"/api/v1/prompt-templates/{key}/activate", new PromptTemplateActivateRequestDto { Version = first.Version });
        Assert.Equal(first.Version, await ActiveVersionAsync(client, key));
        await SendAsync<object>(client, HttpMethod.Post, $"/api/v1/prompt-templates/{key}/activate", new PromptTemplateActivateRequestDto { Version = null });
        Assert.Null(await ActiveVersionAsync(client, key));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/prompt-templates/{key}/activate", new PromptTemplateActivateRequestDto { Version = 9999 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/prompt-templates/NoSuchKey/activate", new PromptTemplateActivateRequestDto())).StatusCode);

        await using var context = await CreateContextAsync();
        var details = await context.AuditLog.Where(x => x.Action == AuditActions.Prompt.Activate && x.ActorUserId == admin.Id)
            .OrderBy(x => x.Id).Select(x => x.Detail).ToListAsync();
        Assert.Equal([$"version={first.Version}", "version=default"], details);
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/v1/token-usage/dashboard", new TokenUsageSearchRequestDto()),
            (HttpMethod.Get, "/api/v1/token-usage/options", null),
            (HttpMethod.Get, "/api/v1/token-usage/1", null),
            (HttpMethod.Post, "/api/v1/token-usage/export", new TokenUsageSearchRequestDto()),
            (HttpMethod.Post, "/api/v1/token-usage/export-pdf", new TokenUsagePdfExportRequestDto()),
            (HttpMethod.Delete, "/api/v1/token-usage/1", null),
            (HttpMethod.Post, "/api/v1/token-usage/purge", new TokenUsagePurgeRequestDto { BeforeDate = DateTime.Today }),
            (HttpMethod.Post, "/api/v1/token-usage/clear-all", null),
            (HttpMethod.Get, "/api/v1/prompt-templates", null),
            (HttpMethod.Post, "/api/v1/prompt-templates/LogAnalysis/versions", new PromptTemplateSaveRequestDto { Content = "x" }),
            (HttpMethod.Post, "/api/v1/prompt-templates/LogAnalysis/activate", new PromptTemplateActivateRequestDto()),
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

    private static string? FileName(HttpResponseMessage response)
        => response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;

    private async Task<int> SeedUsageAsync(string operation, DateTime occurredAt, int total, double? costTwd, string model = "gpt-test")
    {
        await using var context = await CreateContextAsync();
        var row = new TokenUsageLog
        {
            OccurredAt = occurredAt,
            Operation = operation,
            CallKind = TokenUsageCallKinds.Chat,
            Provider = "OpenAI",
            Model = model,
            Account = "support",
            InputCount = total,
            OutputCount = 0,
            TotalCount = total,
            CostTwd = costTwd,
            CostUsd = costTwd / 32,
            CostPriceKey = costTwd is null ? null : model,
            ElapsedMilliseconds = 10,
            Success = true,
        };
        context.TokenUsageLog.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<int?> ActiveVersionAsync(HttpClient client, string key)
        => (await SendAsync<List<PromptTemplateDto>>(client, HttpMethod.Get, "/api/v1/prompt-templates"))
            .Single(x => x.Key == key).Versions.FirstOrDefault(x => x.IsActive)?.Version;

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private async Task<AuditLog> SingleAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.SingleAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
        => (await SendForResultAsync<T>(client, method, url, body)).Data!;

    /// <summary>送出並要求 200，回傳整個信封（要看成功訊息時用）。</summary>
    private static async Task<ApiResult<T>> SendForResultAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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
