using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// AI 對話紀錄的服務層（0.9.72 起）。
///
/// 重點：
/// 1. 記錄時內文寫進內容檔，資料表只留可篩選的中繼資料；停用時什麼都不記。
/// 2. 刪除資料列一定要同時刪掉內容檔，不可留下孤兒檔；自動過期會清掉舊月份目錄。
/// 3. 明細以 CallId 帶出 Token 用量、以 ConversationId 帶出同一對話。
/// 4. <c>RecordAsync</c> <b>絕不拋出</b>。
/// </summary>
public sealed class AiCallLogServiceTests
{
    [Fact]
    public async Task RecordAsync_ShouldInsertRowAndWriteContentFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entry = CreateEntry(requestBody: RequestWithMessages("系統提示", "使用者內容"), responseText: "AI 回應");

        await fixture.CreateService().RecordAsync(entry);

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(entry.CallId, row.CallId);
        Assert.Equal(entry.RequestBody.Length, row.RequestCharacters);
        Assert.Equal("AI 回應".Length, row.ResponseCharacters);
        Assert.NotNull(row.ContentFile);

        var json = await File.ReadAllTextAsync(fixture.FullPath(row.ContentFile!));
        var roundTrip = JsonSerializer.Deserialize<AiCallLogEntry>(json);
        Assert.NotNull(roundTrip);
        Assert.Equal(entry.RequestBody, roundTrip!.RequestBody);
        Assert.Equal("AI 回應", roundTrip.ResponseText);

        // 中文不可被跳脫成 \uXXXX：檔案要能直接閱讀。
        Assert.Contains("AI 回應", json);
    }

    [Fact]
    public async Task RecordAsync_ShouldDoNothing_WhenDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.CreateService(enabled: false).RecordAsync(CreateEntry());

        Assert.Empty(await fixture.ListAsync());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.RootPath));
    }

    [Fact]
    public async Task RecordAsync_ShouldStillInsertRow_WhenContentFileCannotBeWritten()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.CreateService(rootPath: string.Empty).RecordAsync(CreateEntry());

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Null(row.ContentFile);
    }

    [Fact]
    public async Task RecordAsync_ShouldTruncateRelatedInfo()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.CreateService().RecordAsync(CreateEntry(relatedInfo: new string('例', 800)));

        var row = Assert.Single(await fixture.ListAsync());
        Assert.Equal(AiCallLogService.MaxRelatedInfoLength, row.RelatedInfo!.Length);
    }

    [Fact]
    public async Task RecordAsync_ShouldNotThrow_WhenDatabaseIsUnavailable()
    {
        var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeConnectionAsync();

        await service.RecordAsync(CreateEntry());

        // 資料列建不起來，剛寫的內容檔也要一起清掉。
        Assert.Empty(Directory.EnumerateFiles(fixture.RootPath, "*.json", SearchOption.AllDirectories));
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task GetAsync_ShouldApplyFilters()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry(operation: TokenUsageOperations.AiLogAnalysis, account: "chen", relatedInfo: "例外紀錄 #12（NullReferenceException）", model: "gpt-a"));
        await service.RecordAsync(CreateEntry(operation: TokenUsageOperations.AiExceptionAnalysis, account: "wang", success: false, model: "gpt-b"));
        await service.RecordAsync(CreateEntry(operation: TokenUsageOperations.SystemHealthCheck, account: null, occurredAt: DateTime.Now.AddDays(-10)));

        Assert.Single((await service.GetAsync(new AiCallLogQuery { Operation = TokenUsageOperations.AiLogAnalysis })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Account = "che" })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Success = false })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Model = "gpt-b" })).Result);
        Assert.Single((await service.GetAsync(new AiCallLogQuery { Keyword = "#12" })).Result);
        Assert.Equal(2, (await service.GetAsync(new AiCallLogQuery { StartDate = DateTime.Today, EndDate = DateTime.Today })).Count);
    }

    [Fact]
    public async Task GetAsync_ShouldSortByOccurredAtDescending_AndPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var now = DateTime.Now;
        for (var index = 0; index < 5; index++)
        {
            await service.RecordAsync(CreateEntry(occurredAt: now.AddMinutes(-index), relatedInfo: $"#{index}"));
        }

        var page = await service.GetAsync(new AiCallLogQuery { CurrentPage = 2, PageSize = 2 });

        Assert.Equal(5, page.Count);
        Assert.Equal(["#2", "#3"], page.Result.Select(x => x.RelatedInfo).ToList());
    }

    // ---------- 內文搜尋（0.9.169 起） ----------

    /// <summary>比對畫面看得到的文字：每則訊息與 AI 回答，不分大小寫；JSON 欄位名稱不算。</summary>
    [Fact]
    public async Task FindContentMatchesAsync_ShouldMatchMessagesAndResponse_CaseInsensitively()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var now = DateTime.Now;
        var inPrompt = CreateEntry(requestBody: RequestWithMessages("系統提示", "請分析 NullReferenceException"), occurredAt: now.AddMinutes(-3));
        var inResponse = CreateEntry(responseText: "原因是 nullreferenceexception 沒有處理", occurredAt: now.AddMinutes(-2));
        var other = CreateEntry(requestBody: RequestWithMessages("系統提示", "別的內容"), responseText: "無關", occurredAt: now.AddMinutes(-1));
        foreach (var entry in new[] { inPrompt, inResponse, other })
        {
            await service.RecordAsync(entry);
        }

        var found = await service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "  NullReferenceException " });
        var fieldName = await service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "messages" });

        Assert.Equal(await IdsOf(service, inResponse, inPrompt), found.Ids);
        Assert.Equal(3, found.Scanned);
        Assert.False(found.Truncated);
        Assert.Empty(fieldName.Ids);
    }

    /// <summary>失敗而沒有回答時，比對錯誤 body（使用者在「原始 Response」看得到的內容）。</summary>
    [Fact]
    public async Task FindContentMatchesAsync_ShouldMatchErrorBody_WhenThereIsNoResponseText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var failed = CreateEntry(success: false, responseText: string.Empty, responseBody: """{"error":{"message":"Rate limit exceeded"}}""");
        var succeeded = CreateEntry(responseText: "ok", responseBody: """{"note":"rate limit exceeded"}""");
        await service.RecordAsync(failed);
        await service.RecordAsync(succeeded);

        var found = await service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "rate limit" });

        Assert.Equal(await IdsOf(service, failed), found.Ids);
    }

    /// <summary>
    /// 審查發現（0.9.169）：請求格式解析不出訊息時改比對原始請求，請求內容才不會整段漏掉；
    /// 成功但只有工具呼叫（沒有回答文字）的回應不可退回比對原始回應，否則搜 function 這類字幾乎每筆都中。
    /// </summary>
    [Fact]
    public async Task FindContentMatchesAsync_ShouldFallBackToRawRequest_AndOnlyUseRawResponseWhenFailed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var unparsable = CreateEntry(requestBody: "不是 JSON 的請求：關鍵字在這裡");
        var toolCallOnly = CreateEntry(responseText: string.Empty, responseBody: """{"output":[{"type":"function_call","name":"search_projects"}]}""");
        await service.RecordAsync(unparsable);
        await service.RecordAsync(toolCallOnly);

        var inRequest = await service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "關鍵字在這裡" });
        var functionWord = await service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "function_call" });

        Assert.Equal(await IdsOf(service, unparsable), inRequest.Ids);
        Assert.Empty(functionWord.Ids);
    }

    /// <summary>審查發現（0.9.169）：使用者離開頁面時要能停下掃描。</summary>
    [Fact]
    public async Task FindContentMatchesAsync_ShouldStop_WhenCanceled()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry(responseText: "關鍵字"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "關鍵字" }, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task FindContentMatchesAsync_ShouldApplyFilters_AndSkipMissingFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var kept = CreateEntry(operation: TokenUsageOperations.AiLogAnalysis, responseText: "關鍵字");
        var otherOperation = CreateEntry(operation: TokenUsageOperations.AiExceptionAnalysis, responseText: "關鍵字");
        var missing = CreateEntry(operation: TokenUsageOperations.AiLogAnalysis, responseText: "關鍵字");
        foreach (var entry in new[] { kept, otherOperation, missing })
        {
            await service.RecordAsync(entry);
        }

        var missingRow = (await fixture.ListAsync()).Single(x => x.CallId == missing.CallId);
        File.Delete(fixture.FullPath(missingRow.ContentFile!));

        var found = await service.FindContentMatchesAsync(new AiCallLogQuery { Operation = TokenUsageOperations.AiLogAnalysis, ContentKeyword = "關鍵字" });

        Assert.Equal(await IdsOf(service, kept), found.Ids);
        Assert.Equal(2, found.Scanned);
    }

    /// <summary>超過掃描上限時只掃最新的幾筆，並告訴畫面較舊的沒掃到。</summary>
    [Fact]
    public async Task FindContentMatchesAsync_ShouldScanOnlyTheNewest_WhenOverTheLimit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var now = DateTime.Now;
        var entries = Enumerable.Range(0, 4).Select(index => CreateEntry(responseText: "關鍵字", occurredAt: now.AddMinutes(-index))).ToList();
        foreach (var entry in entries)
        {
            await service.RecordAsync(entry);
        }

        var found = await service.FindContentMatchesAsync(new AiCallLogQuery { ContentKeyword = "關鍵字" }, scanLimit: 2);

        Assert.Equal(await IdsOf(service, entries[0], entries[1]), found.Ids);
        Assert.Equal(2, found.Scanned);
        Assert.True(found.Truncated);
        Assert.Equal(1000, AiCallLogService.ContentScanLimit);
    }

    /// <summary>內文搜尋的結果以 Id 清單交回查詢：換頁、排序、匯出 CSV 都不必重新掃描。</summary>
    [Fact]
    public async Task GetAsync_ShouldLimitToIds_AndStillSortAndPage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var now = DateTime.Now;
        for (var index = 0; index < 5; index++)
        {
            await service.RecordAsync(CreateEntry(occurredAt: now.AddMinutes(-index), relatedInfo: $"#{index}"));
        }

        var rows = await fixture.ListAsync();
        var ids = rows.Where(x => x.RelatedInfo is "#1" or "#3" or "#4").Select(x => x.Id).ToList();

        var page = await service.GetAsync(new AiCallLogQuery { Ids = ids, CurrentPage = 1, PageSize = 2 });
        var none = await service.GetAsync(new AiCallLogQuery { Ids = [] });

        Assert.Equal(3, page.Count);
        Assert.Equal(["#1", "#3"], page.Result.Select(x => x.RelatedInfo).ToList());
        Assert.Equal(0, none.Count);
    }

    // ---------- 合併匯出（0.9.169 起） ----------

    [Fact]
    public async Task GetExportItemsAsync_ShouldOrderOldestFirst_WithMessagesUsageAndMissingContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var now = DateTime.Now;
        var newer = CreateEntry(requestBody: RequestWithMessages("sys", "第二則"), responseText: "回答二", occurredAt: now);
        var older = CreateEntry(requestBody: RequestWithMessages("sys", "第一則"), responseText: "回答一", occurredAt: now.AddMinutes(-5));
        var missing = CreateEntry(occurredAt: now.AddMinutes(-1));
        foreach (var entry in new[] { newer, older, missing })
        {
            await service.RecordAsync(entry);
        }

        await fixture.AddUsageAsync(older.CallId);
        var rows = await fixture.ListAsync();
        File.Delete(fixture.FullPath(rows.Single(x => x.CallId == missing.CallId).ContentFile!));

        var items = await service.GetExportItemsAsync(rows.Select(x => x.Id).ToList());

        Assert.Equal([older.CallId, missing.CallId, newer.CallId], items.Select(x => x.Item.CallId).ToList());
        Assert.Equal("第一則", items[0].Messages[1].Content);
        Assert.Equal("回答一", items[0].ResponseText);
        Assert.NotNull(items[0].Usage);
        Assert.False(items[0].ContentMissing);
        Assert.True(items[1].ContentMissing);
        Assert.Empty(items[1].Messages);
        Assert.Null(items[2].Usage);
    }

    private static async Task<List<int>> IdsOf(AiCallLogService service, params AiCallLogEntry[] entries)
    {
        var ids = new List<int>();
        foreach (var entry in entries)
        {
            ids.Add((await service.FindIdByCallIdAsync(entry.CallId))!.Value);
        }

        return ids;
    }

    [Fact]
    public async Task GetDetailAsync_ShouldParseMessages_AndLinkUsageAndConversation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var conversationId = Guid.NewGuid();
        var first = CreateEntry(requestBody: RequestWithMessages("sys", "明細"), conversationId: conversationId, occurredAt: DateTime.Now.AddMinutes(-1));
        var second = CreateEntry(conversationId: conversationId);
        await service.RecordAsync(first);
        await service.RecordAsync(second);
        await service.RecordAsync(CreateEntry());
        await fixture.AddUsageAsync(first.CallId);

        var id = await service.FindIdByCallIdAsync(first.CallId);
        var detail = await service.GetDetailAsync(id!.Value);

        Assert.NotNull(detail);
        Assert.NotNull(detail!.Content);
        Assert.Equal(["system", "user"], detail.Messages!.Select(x => x.Role).ToList());
        Assert.Equal("明細", detail.Messages![1].Content);
        Assert.NotNull(detail.Usage);
        Assert.Equal(first.CallId, detail.Usage!.CallId);
        Assert.Equal([first.CallId, second.CallId], detail.Conversation.Select(x => x.CallId).ToList());
    }

    /// <summary>同一次執行（0.9.142 起）：Agent 工具呼叫迴圈的多次模型呼叫共用 RunId，明細要列出同一次執行的全部。</summary>
    [Fact]
    public async Task GetDetailAsync_ShouldListCallsOfTheSameRun()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var runId = Guid.NewGuid();
        var first = CreateEntry(occurredAt: DateTime.Now.AddSeconds(-5), runId: runId);
        var second = CreateEntry(runId: runId);
        await service.RecordAsync(first);
        await service.RecordAsync(second);
        await service.RecordAsync(CreateEntry());

        var detail = await service.GetDetailAsync((await service.FindIdByCallIdAsync(second.CallId))!.Value);

        Assert.Equal(runId, detail!.Item.RunId);
        Assert.Equal([first.CallId, second.CallId], detail.Run.Select(x => x.CallId).ToList());
    }

    [Fact]
    public async Task GetDetailAsync_ShouldReturnNullContent_WhenFileIsMissing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var entry = CreateEntry();
        await service.RecordAsync(entry);
        var row = Assert.Single(await fixture.ListAsync());
        File.Delete(fixture.FullPath(row.ContentFile!));

        var detail = await service.GetDetailAsync(row.Id);

        Assert.NotNull(detail);
        Assert.Null(detail!.Content);
        Assert.Null(detail.Messages);
        Assert.False(await service.ExistsAsync(Guid.NewGuid()));
        Assert.True(await service.ExistsAsync(entry.CallId));
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRowAndFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry());
        var row = Assert.Single(await fixture.ListAsync());

        var result = await service.DeleteAsync(row.Id);

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.False(File.Exists(fixture.FullPath(row.ContentFile!)));
    }

    [Fact]
    public async Task PurgeBeforeAsync_ShouldRemoveOnlyOlderRowsAndTheirFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-5), relatedInfo: "old"));
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today, relatedInfo: "new"));
        var oldFile = (await fixture.ListAsync()).Single(x => x.RelatedInfo == "old").ContentFile!;

        var result = await service.PurgeBeforeAsync(DateTime.Today.AddDays(-1));

        Assert.True(result.Success);
        Assert.Equal("new", Assert.Single(await fixture.ListAsync()).RelatedInfo);
        Assert.False(File.Exists(fixture.FullPath(oldFile)));
    }

    [Fact]
    public async Task ClearAllAsync_ShouldEmptyTableAndDirectory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        await service.RecordAsync(CreateEntry());
        await service.RecordAsync(CreateEntry());

        var result = await service.ClearAllAsync();

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.RootPath));
        Assert.True(Directory.Exists(fixture.RootPath));
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldRespectRetentionDays_AndSweepOldMonthFolders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(retentionDays: 30);
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-40), relatedInfo: "expired"));
        await service.RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-2), relatedInfo: "kept"));

        // 「檔已寫、資料列沒建起來」留下的孤兒檔，在很舊的月份目錄裡。
        var orphanFolder = Path.Combine(fixture.RootPath, DateTime.Today.AddMonths(-6).ToString("yyyyMM"));
        Directory.CreateDirectory(orphanFolder);
        await File.WriteAllTextAsync(Path.Combine(orphanFolder, "orphan.json"), "{}");

        var removed = await service.PurgeExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Equal("kept", Assert.Single(await fixture.ListAsync()).RelatedInfo);
        Assert.False(Directory.Exists(orphanFolder));
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldStillRun_WhenRecordingIsDisabled()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateService().RecordAsync(CreateEntry(occurredAt: DateTime.Today.AddDays(-200)));

        var removed = await fixture.CreateService(enabled: false, retentionDays: 90).PurgeExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Empty(await fixture.ListAsync());
    }

    private static string RequestWithMessages(string system, string user)
        => JsonSerializer.Serialize(new
        {
            model = "gpt-a",
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });

    private static AiCallLogEntry CreateEntry(
        string operation = TokenUsageOperations.AiLogAnalysis,
        string? account = "support",
        bool success = true,
        string model = "gpt-a",
        string? relatedInfo = null,
        Guid? conversationId = null,
        DateTime? occurredAt = null,
        string? requestBody = null,
        string responseText = "ok",
        Guid? runId = null,
        string responseBody = "{}")
        => new()
        {
            RunId = runId,
            CallId = Guid.NewGuid(),
            OccurredAt = occurredAt ?? DateTime.Now,
            Operation = operation,
            Provider = "AzureOpenAI",
            RequestedModel = model,
            Model = model,
            Account = account,
            UserId = account is null ? null : 7,
            Endpoint = "https://example.invalid/openai/v1/chat/completions",
            RelatedInfo = relatedInfo,
            ConversationId = conversationId,
            RequestBody = requestBody ?? RequestWithMessages("s", "u"),
            HttpStatus = 200,
            ResponseBody = responseBody,
            ResponseText = responseText,
            FinishReason = "stop",
            Success = success,
            FailureReason = success ? null : "UpstreamError",
            ElapsedMilliseconds = 1234,
        };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private Fixture(SqliteConnection connection, string rootPath)
        {
            this.connection = connection;
            RootPath = rootPath;

            loggerFactory = LoggerFactory.Create(_ => { });
        }

        public string RootPath { get; }

        public string FullPath(string relativePath)
            => Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            await using var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            var path = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            return new Fixture(connection, path);
        }

        public AiCallLogService CreateService(bool enabled = true, int retentionDays = 90, string? rootPath = null)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AiCallLogPath = rootPath ?? RootPath;

            return new AiCallLogService(
                new TestDbContextFactory(connection),
                loggerFactory.CreateLogger<AiCallLogService>(),
                new AiCallLogFileStore(Options.Create(settings), loggerFactory.CreateLogger<AiCallLogFileStore>()),
                new StaticOptionsMonitor<AiCallLogSettings>(new AiCallLogSettings { Enabled = enabled, RetentionDays = retentionDays }));
        }

        public async Task<List<AiCallLog>> ListAsync()
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            return await context.AiCallLog.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        public async Task AddUsageAsync(Guid callId)
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            context.TokenUsageLog.Add(new TokenUsageLog
            {
                OccurredAt = DateTime.Now,
                Operation = TokenUsageOperations.AiLogAnalysis,
                CallKind = TokenUsageCallKinds.Chat,
                Provider = "AzureOpenAI",
                Model = "gpt-a",
                InputCount = 10,
                OutputCount = 5,
                TotalCount = 15,
                Success = true,
                CallId = callId,
            });
            await context.SaveChangesAsync();
        }

        /// <summary>刻意關閉連線，用來驗證「RecordAsync 不得拋出」。</summary>
        public async Task DisposeConnectionAsync() => await connection.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // 測試殘留目錄不影響結果。
            }
        }
    }
}
