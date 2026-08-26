using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Web.Diagnostics;

namespace MyProject.Tests;

/// <summary>
/// 日誌查詢服務測試。
///
/// 特別注意「多行紀錄」相關案例：線上日誌目前完全沒有堆疊追蹤樣本，
/// 續行處理只能靠此處的合成資料驗證。
/// </summary>
public sealed class LogQueryServiceTests : IDisposable
{
    private const string Prefix = "MyProject.Web-logfile";

    private readonly string rootPath;
    private readonly string logDirectory;

    public LogQueryServiceTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), $"logquery-{Guid.NewGuid():N}");
        logDirectory = Path.Combine(rootPath, "MyProject.Web");
        Directory.CreateDirectory(logDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // 測試清理失敗不影響結果。
        }
    }

    [Fact]
    public async Task Query_SingleLineEntry_ShouldParseAllFields()
    {
        var today = DateTime.Today;
        // 取自實際日誌檔的真實樣本，但日期一律以 today 內插產生。
        // 日期若寫死，測試只在撰寫當天有效：查詢範圍是 today，內容卻停在舊日期。
        WriteLog(today, $"{today:yyyy-MM-dd} 08:53:19.9079||INFO|2|MyProject.Web.Program|Application host built successfully.|");

        var result = await QueryAsync(CreateRequest(today));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(new DateTime(today.Year, today.Month, today.Day, 8, 53, 19, 907).AddTicks(9000), entry.Timestamp);
        Assert.Equal(string.Empty, entry.TraceId);
        Assert.Equal("INFO", entry.Level);
        Assert.Equal(LogLevelRank.Info, entry.Rank);
        Assert.Equal("2", entry.ThreadId);
        Assert.Equal("MyProject.Web.Program", entry.Logger);
        Assert.Equal("Application host built successfully.", entry.Message);
    }

    [Fact]
    public async Task Query_TraceIdPopulated_ShouldParseTraceId()
    {
        var today = DateTime.Today;
        WriteLog(today, $"{today:yyyy-MM-dd} 08:56:30.4666|0HNO28D1R7FN2:00000001|WARN|19|MyProject.Business.Services.Other.AuthenticationStateHelper|Authentication check failed.|");

        var result = await QueryAsync(CreateRequest(today));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("0HNO28D1R7FN2:00000001", entry.TraceId);
        Assert.Equal(LogLevelRank.Warn, entry.Rank);
    }

    [Fact]
    public async Task Query_MultiLineEntry_ShouldMergeContinuationLinesIntoOneEntry()
    {
        var today = DateTime.Today;
        WriteLog(today,
            $"{today:yyyy-MM-dd} 09:00:00.0000||ERROR|5|MyProject.Web.Boom|Something failed.|System.InvalidOperationException: boom",
            "   at MyProject.Web.Boom.Explode()",
            "   at MyProject.Web.Boom.Run()",
            "--- End of stack trace ---",
            "   at MyProject.Web.Caller.Invoke()",
            $"{today:yyyy-MM-dd} 09:00:01.0000||INFO|5|MyProject.Web.Boom|Recovered.|");

        var result = await QueryAsync(CreateRequest(today));

        Assert.Equal(2, result.Entries.Count);

        var errorEntry = result.Entries[0];
        Assert.Equal(LogLevelRank.Error, errorEntry.Rank);
        Assert.True(errorEntry.HasException);
        Assert.Contains("at MyProject.Web.Boom.Explode()", errorEntry.Raw);
        Assert.Contains("--- End of stack trace ---", errorEntry.Raw);
        Assert.Contains("at MyProject.Web.Caller.Invoke()", errorEntry.Raw);
        // 表頭 + 4 行續行。
        Assert.Equal(5, errorEntry.Raw.Split('\n').Length);
    }

    [Fact]
    public async Task Query_MessageContainingPipe_ShouldStillParseLoggerCorrectly()
    {
        var today = DateTime.Today;
        WriteLog(today, $"{today:yyyy-MM-dd} 09:00:00.0000||INFO|7|MyProject.Web.Pipes|a|b|c value|");

        var result = await QueryAsync(CreateRequest(today));

        var entry = Assert.Single(result.Entries);
        // 前五欄由左往右切，結構上不可能出錯。
        Assert.Equal("MyProject.Web.Pipes", entry.Logger);
        Assert.Equal("7", entry.ThreadId);
        Assert.Equal(LogLevelRank.Info, entry.Rank);
    }

    [Fact]
    public async Task Query_MinimumLevel_ShouldExcludeLowerLevels()
    {
        var today = DateTime.Today;
        WriteLog(today,
            $"{today:yyyy-MM-dd} 09:00:00.0000||DEBUG|1|A|debug line.|",
            $"{today:yyyy-MM-dd} 09:00:01.0000||INFO|1|A|info line.|",
            $"{today:yyyy-MM-dd} 09:00:02.0000||WARN|1|A|warn line.|",
            $"{today:yyyy-MM-dd} 09:00:03.0000||ERROR|1|A|error line.|");

        var request = CreateRequest(today);
        request.MinimumLevel = LogLevelRank.Warn;

        var result = await QueryAsync(request);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.True(entry.Rank >= LogLevelRank.Warn));
    }

    [Fact]
    public async Task Query_Keyword_ShouldBeCaseInsensitiveAndMatchContinuationLines()
    {
        var today = DateTime.Today;
        WriteLog(today,
            $"{today:yyyy-MM-dd} 09:00:00.0000||ERROR|1|A|failed.|System.Exception: outer",
            "   at Deep.Hidden.Frame()",
            $"{today:yyyy-MM-dd} 09:00:01.0000||INFO|1|A|unrelated.|");

        var request = CreateRequest(today);
        // 大小寫互換，且該字串只存在於續行中。
        request.Keyword = "deep.HIDDEN";

        var result = await QueryAsync(request);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(LogLevelRank.Error, entry.Rank);
    }

    [Fact]
    public async Task Query_Take_ShouldReturnLatestEntriesInAscendingOrder()
    {
        var today = DateTime.Today;
        var lines = Enumerable.Range(0, 20)
            .Select(i => $"{today:yyyy-MM-dd} 09:00:{i:00}.0000||INFO|1|A|line {i}.|")
            .ToArray();
        WriteLog(today, lines);

        var request = CreateRequest(today);
        request.Take = 5;

        var result = await QueryAsync(request);

        Assert.Equal(5, result.Entries.Count);
        // 最新 5 筆為 15..19，且回傳為時間正序。
        Assert.Equal("line 15.", result.Entries[0].Message);
        Assert.Equal("line 19.", result.Entries[4].Message);
        Assert.True(result.Entries.SequenceEqual(result.Entries.OrderBy(e => e.Timestamp)));
    }

    [Fact]
    public async Task Query_AcrossTwoDailyFiles_ShouldMergeInChronologicalOrder()
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        WriteLog(yesterday, $"{yesterday:yyyy-MM-dd} 23:59:00.0000||INFO|1|A|yesterday entry.|");
        WriteLog(today, $"{today:yyyy-MM-dd} 00:01:00.0000||INFO|1|A|today entry.|");

        var request = new LogQueryRequest
        {
            StartTime = yesterday.AddHours(20),
            EndTime = today.AddHours(12),
            Take = 100,
        };

        var result = await QueryAsync(request);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("yesterday entry.", result.Entries[0].Message);
        Assert.Equal("today entry.", result.Entries[1].Message);
    }

    [Fact]
    public async Task Query_SpanBeyondLimit_ShouldClampStartTimeAndWarn()
    {
        var today = DateTime.Today;
        WriteLog(today, $"{today:yyyy-MM-dd} 09:00:00.0000||INFO|1|A|entry.|");

        var end = today.AddHours(12);
        var request = new LogQueryRequest
        {
            StartTime = end.AddDays(-5),
            EndTime = end,
            Take = 100,
        };

        var result = await QueryAsync(request);

        Assert.Equal(end - LogQueryRequest.MaxSpan, result.AppliedStartTime);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Query_TakeAboveLimit_ShouldClampAndWarn()
    {
        var today = DateTime.Today;
        WriteLog(today, $"{today:yyyy-MM-dd} 09:00:00.0000||INFO|1|A|entry.|");

        var request = CreateRequest(today);
        request.Take = LogQueryRequest.MaxTake + 1;

        var result = await QueryAsync(request);

        Assert.Contains(result.Warnings, warning => warning.Contains(LogQueryRequest.MaxTake.ToString()));
    }

    [Fact]
    public async Task Query_EntriesAfterEndTime_ShouldBeExcluded()
    {
        var today = DateTime.Today;
        WriteLog(today,
            $"{today:yyyy-MM-dd} 09:00:00.0000||INFO|1|A|inside.|",
            $"{today:yyyy-MM-dd} 23:00:00.0000||INFO|1|A|outside.|");

        var request = new LogQueryRequest
        {
            StartTime = today.AddHours(8),
            EndTime = today.AddHours(10),
            Take = 100,
        };

        var result = await QueryAsync(request);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("inside.", entry.Message);
    }

    [Fact]
    public async Task Query_MalformedLine_ShouldBeKeptNotDropped()
    {
        var today = DateTime.Today;
        // 有合法時間戳但欄位不足。
        WriteLog(today, $"{today:yyyy-MM-dd} 09:00:00.0000|only two fields");

        var result = await QueryAsync(CreateRequest(today));

        var entry = Assert.Single(result.Entries);
        Assert.Equal(LogLevelRank.Unknown, entry.Rank);
        Assert.Equal("only two fields", entry.Message);
    }

    [Fact]
    public async Task Query_MalformedLine_ShouldSurviveMinimumLevelFilter()
    {
        var today = DateTime.Today;
        WriteLog(today, $"{today:yyyy-MM-dd} 09:00:00.0000|only two fields");

        var request = CreateRequest(today);
        request.MinimumLevel = LogLevelRank.Error;

        var result = await QueryAsync(request);

        // 無法解析等級者一律保留，不因等級篩選而隱藏資料。
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task Query_BasePathNotConfigured_ShouldReturnEmptyWithMessage()
    {
        var configuration = new ConfigurationBuilder().Build();
        var service = CreateService(configuration);

        var result = await service.QueryAsync(CreateRequest(DateTime.Today));

        Assert.Empty(result.Entries);
        Assert.Contains("NLog:BasePath", result.Message);
    }

    [Fact]
    public async Task Query_DirectoryMissing_ShouldReturnEmptyWithMessage()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), $"logquery-missing-{Guid.NewGuid():N}");
        var configuration = BuildConfiguration(missingRoot);
        var service = CreateService(configuration);

        var result = await service.QueryAsync(CreateRequest(DateTime.Today));

        Assert.Empty(result.Entries);
        Assert.Contains("不存在", result.Message);
    }

    [Fact]
    public async Task Query_NoFilesInRange_ShouldReturnEmptyWithMessage()
    {
        var result = await QueryAsync(CreateRequest(DateTime.Today));

        Assert.Empty(result.Entries);
        Assert.Contains("沒有日誌檔案", result.Message);
    }

    private static LogQueryRequest CreateRequest(DateTime day) => new()
    {
        StartTime = day,
        EndTime = day.AddDays(1).AddTicks(-1),
        Take = 100,
    };

    private void WriteLog(DateTime day, params string[] lines)
    {
        var path = Path.Combine(logDirectory, $"{Prefix}-{day:yyyy-MM-dd}.log");
        File.AppendAllLines(path, lines);
    }

    private Task<LogQueryResult> QueryAsync(LogQueryRequest request)
        => CreateService(BuildConfiguration(rootPath)).QueryAsync(request);

    private static IConfiguration BuildConfiguration(string basePath)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NLog:BasePath"] = basePath
            })
            .Build();

    private static LogQueryService CreateService(IConfiguration configuration)
        => new(
            new NLogFilePathResolver(configuration, new TestWebHostEnvironment()),
            NullLogger<LogQueryService>.Instance);

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MyProject.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
