using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 稽核紀錄的查詢服務。
///
/// ⚠️ <b>本檔最重要的是時區。</b><c>AuditLog.OccurredAt</c> 是全專案唯一以 UTC 寫入的時間欄位
/// （<c>AuditLogService</c> 用 <c>DateTime.UtcNow</c>，而 <c>ExceptionLog</c> 與 <c>TokenUsageLog</c>
/// 用的是 <c>DateTime.Now</c>）。查詢條件來自畫面的 DatePicker＝本地時間，顯示也是本地時間，
/// 因此服務必須在兩端各做一次換算。照抄例外紀錄那支（完全不換算）會讓整條時間軸偏掉一個時區。
/// </summary>
public sealed class AuditLogQueryServiceTests
{
    // ---------------------------------------------------------------
    // 時區換算（與執行機器的時區無關，因此在任何 CI 機器上都有意義）
    // ---------------------------------------------------------------

    [Fact]
    public void ToUtc_ShouldTreatUnspecifiedKindAsLocalTime()
    {
        // DatePicker 給的 DateTime 多半是 Kind=Unspecified。
        var picked = new DateTime(2026, 9, 20, 13, 30, 0, DateTimeKind.Unspecified);
        var expectedOffset = TimeZoneInfo.Local.GetUtcOffset(picked);

        var utc = AuditLogQueryService.ToUtc(picked);

        Assert.Equal(picked - expectedOffset, utc);
    }

    [Fact]
    public void ToLocal_ShouldTreatUnspecifiedKindAsUtc()
    {
        // SQLite 讀回來的 DateTime 也是 Kind=Unspecified，但它的內容是 UTC。
        var stored = new DateTime(2026, 9, 20, 5, 30, 0, DateTimeKind.Unspecified);
        var expected = DateTime.SpecifyKind(stored, DateTimeKind.Utc).ToLocalTime();

        var local = AuditLogQueryService.ToLocal(stored);

        Assert.Equal(expected, local);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnOccurredAtAsLocalTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var utc = DateTime.UtcNow.AddMinutes(-5);
        await fixture.SeedAsync(new AuditLog { OccurredAt = utc, Action = "Login.Success" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery());

        var item = Assert.Single(result.Result);

        // 允許 1 秒誤差：SQLite 的 DateTime 往返會丟掉部分精度。
        var expected = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        Assert.True(
            Math.Abs((item.OccurredAt - expected).TotalSeconds) < 1,
            $"回傳的時間應為本地時間 {expected:O}，實際為 {item.OccurredAt:O}。");
    }

    /// <summary>
    /// 查詢條件是本地時間、資料欄位是 UTC，服務必須換算後才比對。
    ///
    /// 若服務漏掉 <c>ToUtc</c>，在 UTC+8 會拿「比 UtcNow 早 8 小時的牆上時間」去比對 UTC 欄位，
    /// 三小時的區間就會整個落空、查無資料。
    /// 註：在 UTC±0 的機器上本地即 UTC，這條會平凡通過 —— 它抓的是有時差的環境（含台灣）。
    /// </summary>
    [Fact]
    public async Task GetAsync_TimeRangeFilter_ShouldTreatQueryAsLocalAndColumnAsUtc()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow.AddHours(-2), Action = "Login.Success" },
            new AuditLog { OccurredAt = DateTime.UtcNow.AddHours(-26), Action = "Login.Failed" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery
        {
            // 牆上時間的三小時前 —— 使用者在 DatePicker 上就是這樣選的。
            StartTime = DateTime.Now.AddHours(-3),
        });

        var item = Assert.Single(result.Result);
        Assert.Equal("Login.Success", item.Action);
    }

    // ---------------------------------------------------------------
    // 過濾
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ShouldFilterByActorAccount_PartialMatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success", ActorAccount = "alice" },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success", ActorAccount = "bob" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery { Account = "ali" });

        var item = Assert.Single(result.Result);
        Assert.Equal("alice", item.ActorAccount);
    }

    [Fact]
    public async Task GetAsync_ShouldFilterByAction_ExactMatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success" },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Failed" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery { Action = "Login.Success" });

        var item = Assert.Single(result.Result);
        Assert.Equal("Login.Success", item.Action);
    }

    [Theory]
    [InlineData(true, "User.Create")]
    [InlineData(false, "Login.Failed")]
    public async Task GetAsync_ShouldFilterBySuccessFlag(bool success, string expectedAction)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "User.Create", Success = true },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Failed", Success = false });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery { Success = success });

        var item = Assert.Single(result.Result);
        Assert.Equal(expectedAction, item.Action);
    }

    [Fact]
    public async Task GetAsync_SuccessFilterNull_ShouldReturnBoth()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "User.Create", Success = true },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Failed", Success = false });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery { Success = null });

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData("42")]          // TargetId
    [InlineData("account=")]    // Detail
    [InlineData("MyUser")]      // TargetType
    public async Task GetAsync_ShouldFilterByKeyword_AcrossTargetAndDetail(string keyword)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog
            {
                OccurredAt = DateTime.UtcNow,
                Action = "User.Create",
                TargetType = "MyUser",
                TargetId = "42",
                Detail = "account=newbie",
            },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success", ActorAccount = "bob" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery { Keyword = keyword });

        var item = Assert.Single(result.Result);
        Assert.Equal("User.Create", item.Action);
    }

    // ---------------------------------------------------------------
    // 排序與分頁
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ShouldDefaultToOccurredAtDescending()
    {
        await using var fixture = await Fixture.CreateAsync();
        var now = DateTime.UtcNow;
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = now.AddMinutes(-10), Action = "Login.Failed" },
            new AuditLog { OccurredAt = now, Action = "Login.Success" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery());

        var items = result.Result.ToList();
        Assert.Equal("Login.Success", items[0].Action);
        Assert.Equal("Login.Failed", items[1].Action);
    }

    [Fact]
    public async Task GetAsync_UnknownSortField_ShouldFallBackToDefaultWithoutThrowing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success" });

        var result = await fixture.CreateService().GetAsync(new AuditLogQuery
        {
            SortField = "NoSuchColumn",
            SortDescending = null,
        });

        Assert.Single(result.Result);
    }

    /// <summary>
    /// 稽核事件「同一秒鐘好幾筆」是常態（登入失敗連發、一次儲存寫多筆權限異動）。
    /// 排序若沒有以 Id 收尾，SQLite 不保證同值列的相對順序，翻頁就會重複或漏資料。
    /// </summary>
    [Fact]
    public async Task GetAsync_SameTimestampRows_ShouldPageWithoutDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sameMoment = DateTime.UtcNow;
        var rows = Enumerable.Range(0, 10)
            .Select(index => new AuditLog
            {
                OccurredAt = sameMoment,
                Action = "Login.Failed",
                Detail = $"seed-{index}",
            })
            .ToArray();
        await fixture.SeedAsync(rows);

        var service = fixture.CreateService();
        var seen = new List<int>();

        for (var page = 1; page <= 4; page++)
        {
            var result = await service.GetAsync(new AuditLogQuery { CurrentPage = page, PageSize = 3 });
            seen.AddRange(result.Result.Select(x => x.Id));
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct().Count());
    }

    [Fact]
    public async Task GetAsync_ShouldReturnTotalCountBeforePaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        var rows = Enumerable.Range(0, 7)
            .Select(index => new AuditLog
            {
                OccurredAt = DateTime.UtcNow.AddMinutes(-index),
                Action = "Login.Success",
            })
            .ToArray();
        await fixture.SeedAsync(rows);

        var result = await fixture.CreateService()
            .GetAsync(new AuditLogQuery { CurrentPage = 1, PageSize = 3 });

        Assert.Equal(7, result.Count);
        Assert.Equal(3, result.Result.Count());
    }

    // ---------------------------------------------------------------
    // 下拉選項
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetDistinctActionsAsync_ShouldReturnSortedUniqueActions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "User.Create" },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success" },
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success" });

        var actions = await fixture.CreateService().GetDistinctActionsAsync();

        Assert.Equal(new[] { "Login.Success", "User.Create" }, actions);
    }

    // ---------------------------------------------------------------
    // 清除與清空
    // ---------------------------------------------------------------

    /// <summary>
    /// 清除門檻必須以 UTC 計算。
    ///
    /// 種一筆「比門檻新 4 小時」的紀錄：用 <c>DateTime.UtcNow</c> 算門檻時它應該留著；
    /// 若誤用 <c>DateTime.Now</c>（UTC+8），門檻會往後挪 8 小時，這一筆就會被誤刪。
    /// 註：與上面的時間區間測試同理，在 UTC±0 的機器上這條會平凡通過。
    /// </summary>
    [Fact]
    public async Task PurgeAsync_ShouldComputeThresholdInUtc()
    {
        await using var fixture = await Fixture.CreateAsync();
        var justInsideThreshold = DateTime.UtcNow.AddDays(-365).AddHours(4);
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = justInsideThreshold, Action = "Login.Success" },
            new AuditLog { OccurredAt = DateTime.UtcNow.AddDays(-400), Action = "Login.Failed" });

        var result = await fixture.CreateService().PurgeAsync(365);

        Assert.True(result.Success);
        var remaining = await fixture.ListAsync();
        var row = Assert.Single(remaining);
        Assert.Equal("Login.Success", row.Action);
    }

    [Fact]
    public async Task PurgeAsync_WhenNothingMatches_ShouldSucceedWithMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success" });

        var result = await fixture.CreateService().PurgeAsync(365);

        Assert.True(result.Success);
        Assert.Single(await fixture.ListAsync());
    }

    [Fact]
    public async Task ClearAllAsync_ShouldRemoveEveryRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync(
            new AuditLog { OccurredAt = DateTime.UtcNow, Action = "Login.Success" },
            new AuditLog { OccurredAt = DateTime.UtcNow.AddDays(-1), Action = "User.Create" });

        var result = await fixture.CreateService().ClearAllAsync();

        Assert.True(result.Success);
        Assert.Empty(await fixture.ListAsync());
    }

    // ---------------------------------------------------------------
    // AdapterModel 的計算屬性
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Login.Success", "Login")]
    [InlineData("Permission.Denied", "Permission")]
    [InlineData("NoDotHere", "NoDotHere")]
    [InlineData("", "")]
    public void ActionCategory_ShouldReturnFirstSegment(string action, string expected)
    {
        var model = new AuditLogAdapterModel { Action = action };

        Assert.Equal(expected, model.ActionCategory);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;
        private readonly IMapper mapper;

        private Fixture(SqliteConnection connection)
        {
            this.connection = connection;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            await using var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new Fixture(connection);
        }

        public AuditLogQueryService CreateService()
            => new(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<AuditLogQueryService>());

        public async Task SeedAsync(params AuditLog[] rows)
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            context.AuditLog.AddRange(rows);
            await context.SaveChangesAsync();
        }

        public async Task<List<AuditLog>> ListAsync()
        {
            await using var context = new TestDbContextFactory(connection).CreateDbContext();
            return await context.AuditLog.AsNoTracking().ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            loggerFactory.Dispose();
            await connection.DisposeAsync();
        }
    }
}
