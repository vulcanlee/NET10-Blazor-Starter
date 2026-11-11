using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 與 <see cref="ApiTestApplicationFactory"/> 相同，但 AI 日誌分析換成假的（不連外），AI 送出筆數上限設為 2。
/// </summary>
public sealed class LogApiTestApplicationFactory : ApiTestApplicationFactory
{
    public const int MaxEntries = 2;

    public FakeAiLogAnalysisService Ai { get; } = new();

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiSettings:MaxEntries"] = MaxEntries.ToString(),
            // 「查詢」屬於瀏覽類稽核，預設不記；打開才驗得到搜尋稽核。
            ["AuditLogSettings:RecordBrowsing"] = "true",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAiLogAnalysisService>();
            services.AddSingleton<IAiLogAnalysisService>(Ai);
        });
    }
}

/// <summary>假的 AI 日誌分析：先送一段文字，之後依 <see cref="WaitForCancellation"/> 正常完成或等到請求被中斷才回「已取消」。</summary>
public sealed class FakeAiLogAnalysisService : IAiLogAnalysisService
{
    public bool IsAvailable => true;

    public string UnavailableReason => string.Empty;

    /// <summary>最近一次送進來分析的日誌（伺服器重新查詢的結果）。</summary>
    public IReadOnlyList<LogEntry>? LastEntries { get; private set; }

    public bool WaitForCancellation { get; set; }

    public Task<AiAnalysisResult> AnalyzeAsync(IReadOnlyList<LogEntry> entriesAscending, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<AiStreamUpdate> AnalyzeStreamingAsync(
        IReadOnlyList<LogEntry> entriesAscending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastEntries = entriesAscending;
        var prompt = AiLogPromptBuilder.Build(entriesAscending, LogApiTestApplicationFactory.MaxEntries);
        yield return new AiStreamUpdate { TextDelta = "## 摘要\n" };

        if (WaitForCancellation)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 使用者關窗或按「停止產生」：瀏覽器中斷請求。
            }

            yield return new AiStreamUpdate
            {
                Result = new AiAnalysisResult
                {
                    Success = false,
                    Reason = AiAnalysisFailureReason.Canceled,
                    ErrorMessage = "已取消本次 AI 分析。",
                    Markdown = "## 摘要\n",
                    IsInterrupted = true,
                    Prompt = prompt,
                    ModelName = "fake-model",
                    Elapsed = TimeSpan.FromMilliseconds(500),
                },
            };
            yield break;
        }

        yield return new AiStreamUpdate { TextDelta = "一切正常。" };
        yield return new AiStreamUpdate
        {
            Result = new AiAnalysisResult
            {
                Success = true,
                Markdown = "## 摘要\n一切正常。",
                Prompt = prompt,
                ModelName = "fake-model",
                Usage = new AiTokenUsage { InputCount = 10, OutputCount = 5, TotalCount = 15 },
                Elapsed = TimeSpan.FromMilliseconds(1234),
            },
        };
    }
}

/// <summary>
/// 日誌檢視與日誌等級設定的 Web API（0.10.5 起，WASM 遷移 M6）：只限管理員（<c>[AdminOnly]</c>），
/// 查詢、匯出、AI 分析（NDJSON 串流）、AI 報告 PDF、選項，以及日誌等級的讀取、套用、還原；稽核寫法與 Server 版頁面相同。
/// 需要「現在」的（預設區間、快速區間、檔名、PDF 產生時間）都用伺服器時間；本地時間不帶時差送出。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class LogApiTests : IClassFixture<LogApiTestApplicationFactory>
{
    private const string Password = "Passw0rd-1";
    private static readonly Regex Offset = new(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)");
    private static readonly Regex DateLike = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}");

    private readonly LogApiTestApplicationFactory factory;

    public LogApiTests(LogApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Search_ShouldReturnTheEntriesOldestFirst_AndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var day = WriteSampleLog(new DateTime(2001, 2, 3));

        var opened = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search", Range(day));
        var searched = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search",
            Range(day, level: "WARN", keyword: "磁碟", recordSearch: true));

        Assert.Equal(3, opened.TotalCount);
        Assert.Equal([1, 2, 3], opened.Entries.Select(x => x.Sequence));
        var error = opened.Entries[1];
        Assert.Equal(day.AddHours(9).AddMinutes(1), error.Timestamp);
        Assert.Equal("ERROR", error.Level);
        Assert.Equal("K7Q2M9XA", error.TraceId);
        Assert.Equal("MyProject.Web.Disk", error.Logger);
        Assert.Equal("磁碟空間不足", error.Message);
        Assert.True(error.HasException);
        Assert.Contains("\n   at MyProject.Web.Disk.Check()", error.Raw);
        Assert.Equal(day.AddHours(8), opened.AppliedStartTime);
        Assert.Equal(day.AddHours(10), opened.AppliedEndTime);
        Assert.Empty(opened.Warnings);
        Assert.Equal("ERROR", Assert.Single(searched.Entries).Level);

        var audit = Assert.Single(await AuditsAsync(AuditActions.LogViewer.Search, admin.Id));
        Assert.Equal("LogQuery", audit.TargetType);
        Assert.Equal("start=2001/02/03 08:00:00; end=2001/02/03 10:00:00; minimumLevel=WARN; take=100; hasKeyword=True; hasTraceCode=False; rows=1", audit.Detail);
    }

    [Fact]
    public async Task Search_ShouldClampTheRangeLikeTheScreen_AndFilterByTraceCode()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var day = WriteSampleLog(new DateTime(2001, 3, 4));

        var reversed = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search",
            new LogSearchRequestDto { StartTime = day.AddHours(10), EndTime = day.AddHours(8), TraceCode = " k7q2m9xa " });
        var tooLong = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search",
            new LogSearchRequestDto { StartTime = day.AddDays(-10), EndTime = day.AddHours(10) });

        Assert.Equal("起始時間晚於結束時間，已自動對調。", Assert.Single(reversed.Warnings));
        Assert.Equal(day.AddHours(8), reversed.AppliedStartTime);
        Assert.Equal(["K7Q2M9XA", "K7Q2M9XA"], reversed.Entries.Select(x => x.TraceId));
        Assert.Equal(day.AddHours(10).AddDays(-3), tooLong.AppliedStartTime);
        Assert.Contains(tooLong.Warnings, x => x.StartsWith("查詢區間上限為 3 天", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_WithoutARange_ShouldUseTheServerNow_ForTheDefaultHourAndTheQuickRanges()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var before = DateTime.Now;
        var defaultRange = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search", new LogSearchRequestDto());
        var last5 = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search",
            new LogSearchRequestDto { QuickRange = LogQuickRanges.Last5Minutes, StartTime = new DateTime(2001, 1, 1) });
        var last15 = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search", new LogSearchRequestDto { QuickRange = LogQuickRanges.Last15Minutes });
        var lastHour = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search", new LogSearchRequestDto { QuickRange = LogQuickRanges.LastHour });
        var today = await SendAsync<LogSearchResultDto>(client, HttpMethod.Post, "/api/v1/logs/search", new LogSearchRequestDto { QuickRange = LogQuickRanges.Today });
        var after = DateTime.Now;

        Assert.InRange(defaultRange.AppliedEndTime, before, after);
        Assert.Equal(TimeSpan.FromHours(1), defaultRange.AppliedEndTime - defaultRange.AppliedStartTime);
        Assert.InRange(last5.AppliedEndTime, before, after);
        Assert.Equal(TimeSpan.FromMinutes(5), last5.AppliedEndTime - last5.AppliedStartTime);
        Assert.Equal(TimeSpan.FromMinutes(15), last15.AppliedEndTime - last15.AppliedStartTime);
        Assert.Equal(TimeSpan.FromHours(1), lastHour.AppliedEndTime - lastHour.AppliedStartTime);
        Assert.Equal(today.AppliedEndTime.Date, today.AppliedStartTime);
    }

    [Theory]
    [InlineData("{\"quickRange\":\"last2h\"}")]
    [InlineData("{\"minimumLevel\":\"VERBOSE\"}")]
    public async Task Search_WithAnUnknownQuickRangeOrLevel_ShouldReturn400(string body)
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.PostAsync("/api/v1/logs/search", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalTimes_ShouldBeSentWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var day = WriteSampleLog(new DateTime(2001, 4, 5));

        var search = await (await client.PostAsJsonAsync("/api/v1/logs/search", Range(day))).Content.ReadAsStringAsync();
        var quick = await (await client.PostAsJsonAsync("/api/v1/logs/search", new LogSearchRequestDto { QuickRange = LogQuickRanges.Today })).Content.ReadAsStringAsync();
        var stream = await (await client.PostAsJsonAsync("/api/v1/logs/ai", Range(day))).Content.ReadAsStringAsync();

        var times = new[] { search, quick }.SelectMany(text => DateTexts(JsonDocument.Parse(text).RootElement, isEnvelope: true))
            .Concat(stream.Split('\n', StringSplitOptions.RemoveEmptyEntries).SelectMany(line => DateTexts(JsonDocument.Parse(line).RootElement, isEnvelope: false)))
            .ToList();
        Assert.Contains("2001-04-05T09:01:00", times);
        Assert.Contains("2001-04-05T08:00:00", times);
        Assert.True(times.Count >= 9, string.Join(", ", times));
        Assert.All(times, text => Assert.False(Offset.IsMatch(text), text));
    }

    /// <summary>JSON 裡所有看起來像時間的字串（ApiResult 外層的 timestamp 是回應時間、不顯示在畫面上，不在檢查範圍）。</summary>
    private static IEnumerable<string> DateTexts(JsonElement element, bool isEnvelope)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (isEnvelope && property.NameEquals("timestamp"))
                    {
                        continue;
                    }

                    foreach (var text in DateTexts(property.Value, isEnvelope: false))
                    {
                        yield return text;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var text in element.EnumerateArray().SelectMany(item => DateTexts(item, isEnvelope: false)))
                {
                    yield return text;
                }

                break;
            case JsonValueKind.String when DateLike.IsMatch(element.GetString()!):
                yield return element.GetString()!;
                break;
        }
    }

    [Fact]
    public async Task Export_ShouldReturnTheRawLogWithABom_AndWriteOneExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var day = WriteSampleLog(new DateTime(2001, 5, 6));

        var response = await client.PostAsJsonAsync("/api/v1/logs/export", Range(day, level: "ERROR"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(@"^MyProject\.Web-logs-\d{8}-\d{6}\.log$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("2001-05-06 09:01:00.0000|K7Q2M9XA|ERROR|", text);
        Assert.Contains(Environment.NewLine + "2001-05-06 09:30:00.0000|K7Q2M9XA|FATAL|", text);

        var audit = Assert.Single(await AuditsAsync(AuditActions.LogViewer.Export, admin.Id));
        Assert.Equal("LogQuery", audit.TargetType);
        Assert.Null(audit.TargetId);
        Assert.Equal("format=log; rows=2", audit.Detail);
    }

    [Fact]
    public async Task Options_ShouldTellWhetherAiIsAvailable()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var options = await SendAsync<LogViewerOptionsDto>(client, HttpMethod.Get, "/api/v1/logs/options");

        Assert.True(options.AiAvailable);
        Assert.Equal(string.Empty, options.AiUnavailableReason);
    }

    [Fact]
    public async Task Ai_ShouldStreamTheScopeTheTextAndTheResult_ThenWriteOneAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var day = WriteSampleLog(new DateTime(2001, 6, 7));
        factory.Ai.WaitForCancellation = false;

        var response = await client.PostAsJsonAsync("/api/v1/logs/ai", Range(day));
        var frames = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<AiStreamFrameDto>(line, JsonSerializerOptions.Web)!)
            .ToList();

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([AiStreamFrameKinds.Start, AiStreamFrameKinds.Delta, AiStreamFrameKinds.Delta, AiStreamFrameKinds.Result], frames.Select(x => x.Kind));
        var scope = frames[0].Scope!;
        Assert.Equal(3, scope.TotalEntryCount);
        Assert.Equal(LogApiTestApplicationFactory.MaxEntries, scope.IncludedEntryCount);
        Assert.True(scope.DroppedByEntryLimit);
        Assert.Equal(day.AddHours(8), scope.StartLocal);
        Assert.Equal(day.AddHours(10), scope.EndLocal);
        Assert.Equal("## 摘要\n一切正常。", string.Concat(frames.Where(x => x.Kind == AiStreamFrameKinds.Delta).Select(x => x.Text)));
        var result = frames[^1].Result!;
        Assert.True(result.Success);
        Assert.Equal("fake-model", result.ModelName);
        Assert.Equal(3, result.TotalEntryCount);
        Assert.Equal(3, factory.Ai.LastEntries!.Count);

        var audit = Assert.Single(await AuditsAsync(AuditActions.LogViewer.AiAnalyze, admin.Id));
        Assert.True(audit.Success);
        Assert.Equal("LogQuery", audit.TargetType);
        Assert.Equal("20010607080000-20010607100000", audit.TargetId);
        Assert.Equal($"送出 2/3 筆、{result.CharacterCount} 字元；模型 fake-model；用量 輸入 10　輸出 5　合計 15；耗時 1234 ms", audit.Detail);
        Assert.DoesNotContain("磁碟", audit.Detail);
    }

    /// <summary>使用者關窗或按「停止產生」時瀏覽器中斷請求：AI 呼叫跟著取消，伺服器照樣寫一筆稽核（與 Server 版相同）。</summary>
    [Fact]
    public async Task Ai_WhenTheBrowserAbortsTheRequest_ShouldCancelTheCallAndStillWriteTheAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var day = WriteSampleLog(new DateTime(2001, 7, 8));
        factory.Ai.WaitForCancellation = true;

        try
        {
            using (var cts = new CancellationTokenSource())
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs/ai") { Content = JsonContent.Create(Range(day)) })
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token)))
            {
                Assert.Equal(AiStreamFrameKinds.Start, JsonSerializer.Deserialize<AiStreamFrameDto>((await reader.ReadLineAsync(cts.Token))!, JsonSerializerOptions.Web)!.Kind);
                Assert.Equal(AiStreamFrameKinds.Delta, JsonSerializer.Deserialize<AiStreamFrameDto>((await reader.ReadLineAsync(cts.Token))!, JsonSerializerOptions.Web)!.Kind);

                // 瀏覽器中斷請求（fetch abort）：取消並丟掉回應。
                await cts.CancelAsync();
            }

            AuditLog? audit = null;
            for (var attempt = 0; attempt < 50 && audit is null; attempt++)
            {
                await Task.Delay(100);
                audit = (await AuditsAsync(AuditActions.LogViewer.AiAnalyze, admin.Id)).SingleOrDefault();
            }

            Assert.NotNull(audit);
            Assert.False(audit.Success);
            Assert.EndsWith("；失敗原因 Canceled", audit.Detail);
        }
        finally
        {
            factory.Ai.WaitForCancellation = false;
        }
    }

    [Fact]
    public async Task AiPdf_ShouldRebuildThePromptOnTheServer_AndWriteOneExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var day = WriteSampleLog(new DateTime(2001, 8, 9));

        var response = await client.PostAsJsonAsync("/api/v1/logs/ai/pdf", new LogAiReportPdfRequestDto
        {
            Query = Range(day),
            Markdown = "## 摘要\n一切正常。",
            Success = true,
            Reason = "None",
            ModelName = "fake-model",
            Usage = new AiUsageDto { InputCount = 10, OutputCount = 5, TotalCount = 15 },
            ElapsedMilliseconds = 1234,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^MyProject\.Web-ai-log-report-\d{8}-\d{6}\.pdf$", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("%PDF"u8.ToArray(), (await response.Content.ReadAsByteArrayAsync())[..4]);

        var prompt = AiLogPromptBuilder.Build(await QueryAsync(day), LogApiTestApplicationFactory.MaxEntries);
        var audit = Assert.Single(await AuditsAsync(AuditActions.LogViewer.AiAnalyzeExportPdf, admin.Id));
        Assert.True(audit.Success);
        Assert.Equal("20010809080000-20010809100000", audit.TargetId);
        Assert.Equal($"送出 2/3 筆、{prompt.CharacterCount} 字元；模型 fake-model；用量 輸入 10　輸出 5　合計 15；耗時 1234 ms", audit.Detail);
    }

    [Fact]
    public async Task AiPdf_WithAnUnknownReason_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.PostAsJsonAsync("/api/v1/logs/ai/pdf", new LogAiReportPdfRequestDto { Query = Range(new DateTime(2001, 8, 9)), Reason = "<script>" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>報告與模型名稱來自畫面，會拿去排版 PDF 與寫稽核摘要：超過上限回 400、不產生檔案、不寫稽核（0.10.5 獨立審查）。</summary>
    [Fact]
    public async Task AiPdf_WithOversizedFieldsFromTheBrowser_ShouldReturn400WithoutAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var longModel = await client.PostAsJsonAsync("/api/v1/logs/ai/pdf", new LogAiReportPdfRequestDto { Query = Range(new DateTime(2001, 8, 9)), Markdown = "x", ModelName = new string('m', 201) });
        var longReport = await client.PostAsJsonAsync("/api/v1/logs/ai/pdf", new LogAiReportPdfRequestDto { Query = Range(new DateTime(2001, 8, 9)), Markdown = new string('x', ExceptionAiRequestDto.MaxReplyLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, longModel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longReport.StatusCode);
        Assert.Empty(await AuditsAsync(AuditActions.LogViewer.AiAnalyzeExportPdf, admin.Id));
    }

    [Fact]
    public async Task LogLevel_ShouldApplyAndRestoreAtRuntime_AndAuditEachChange()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var state = await SendAsync<LogLevelStateDto>(client, HttpMethod.Get, "/api/v1/log-level");
        Assert.True(state.IsAvailable);
        Assert.False(state.HasOverride);
        var target = state.SystemDefaultLevel == "WARN" ? "ERROR" : "WARN";

        try
        {
            var applied = await SendAsync<LogLevelStateDto>(client, HttpMethod.Post, "/api/v1/log-level/apply", new LogLevelApplyRequestDto { Level = target });
            Assert.Equal(target, applied.EffectiveLevel);
            Assert.Equal(state.SystemDefaultLevel, applied.SystemDefaultLevel);
            Assert.True(applied.HasOverride);
        }
        finally
        {
            var restored = await SendAsync<LogLevelStateDto>(client, HttpMethod.Post, "/api/v1/log-level/restore");
            Assert.Equal(state.SystemDefaultLevel, restored.EffectiveLevel);
            Assert.False(restored.HasOverride);
        }

        var apply = Assert.Single(await AuditsAsync(AuditActions.LogLevel.Apply, admin.Id));
        Assert.Equal("NLog", apply.TargetType);
        Assert.Equal("logger:*", apply.TargetId);
        Assert.Equal($"日誌最低等級由 {state.EffectiveLevel} 調整為 {target}（僅執行期有效）。", apply.Detail);
        Assert.Equal($"日誌最低等級由 {target} 調整為 {state.SystemDefaultLevel}（僅執行期有效）。", Assert.Single(await AuditsAsync(AuditActions.LogLevel.Restore, admin.Id)).Detail);
    }

    [Fact]
    public async Task LogLevel_WithAnUnknownLevelOrNothingToRestore_ShouldBeRejectedWithoutAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var unknown = await client.PostAsJsonAsync("/api/v1/log-level/apply", new LogLevelApplyRequestDto { Level = "VERBOSE" });
        var restore = await client.PostAsync("/api/v1/log-level/restore", null);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Contains("還原失敗，請確認 NLog 設定。", await restore.Content.ReadAsStringAsync());
        Assert.Empty(await AuditsAsync(AuditActions.LogLevel.Apply, admin.Id));
        Assert.Empty(await AuditsAsync(AuditActions.LogLevel.Restore, admin.Id));
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/v1/logs/search", new LogSearchRequestDto()),
            (HttpMethod.Post, "/api/v1/logs/export", new LogSearchRequestDto()),
            (HttpMethod.Post, "/api/v1/logs/ai", new LogSearchRequestDto()),
            (HttpMethod.Post, "/api/v1/logs/ai/pdf", new LogAiReportPdfRequestDto()),
            (HttpMethod.Get, "/api/v1/logs/options", null),
            (HttpMethod.Get, "/api/v1/log-level", null),
            (HttpMethod.Post, "/api/v1/log-level/apply", new LogLevelApplyRequestDto { Level = "WARN" }),
            (HttpMethod.Post, "/api/v1/log-level/restore", null),
        };

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        await using var context = await CreateContextAsync();
        Assert.Equal(calls.Length, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
        Assert.Null(factory.Services.GetRequiredService<LogLevelRuntimeState>().OverrideLevel);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static LogSearchRequestDto Range(DateTime day, string? level = null, string? keyword = null, bool recordSearch = false) => new()
    {
        StartTime = day.AddHours(8),
        EndTime = day.AddHours(10),
        MinimumLevel = level,
        Keyword = keyword,
        RecordSearch = recordSearch,
    };

    /// <summary>在測試主機的日誌目錄寫一個過去日期的日誌檔（主機自己的日誌只會寫今天，互不干擾）：INFO、ERROR（含堆疊）、FATAL 各一筆。</summary>
    private DateTime WriteSampleLog(DateTime day)
    {
        using var scope = factory.Services.CreateScope();
        var path = scope.ServiceProvider.GetRequiredService<INLogFilePathResolver>().GetLogFilePath(DateOnly.FromDateTime(day));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n",
            $"{day:yyyy-MM-dd} 09:00:00.0000|ZZZZ0000|INFO|1|MyProject.Web.Program|啟動完成|",
            $"{day:yyyy-MM-dd} 09:01:00.0000|K7Q2M9XA|ERROR|4|MyProject.Web.Disk|磁碟空間不足|System.IO.IOException: 磁碟已滿",
            "   at MyProject.Web.Disk.Check()",
            $"{day:yyyy-MM-dd} 09:30:00.0000|K7Q2M9XA|FATAL|4|MyProject.Web.Program|停止服務|",
            $"{day:yyyy-MM-dd} 11:00:00.0000||INFO|1|MyProject.Web.Program|區間外|") + "\n", new UTF8Encoding(false));
        return day;
    }

    private async Task<IReadOnlyList<LogEntry>> QueryAsync(DateTime day)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ILogQueryService>().QueryAsync(new LogQueryRequest { StartTime = day.AddHours(8), EndTime = day.AddHours(10) });
        return result.Entries;
    }

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private async Task<List<AuditLog>> AuditsAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.Where(x => x.Action == action && x.ActorUserId == actorId).ToListAsync();
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
