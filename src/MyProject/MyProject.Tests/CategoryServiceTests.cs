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

public sealed class CategoryServiceTests
{
    [Fact]
    public async Task BeforeAddCheckAsync_WithUniqueName_ShouldSucceed()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "技術文件" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateName_ShouldFail()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("技術文件");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "技術文件" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateNameDifferentCase_ShouldFail()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("Report");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "report" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithSameRecordSameName_ShouldSucceed()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var existing = await fixture.AddCategoryAsync("技術文件");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new CategoryAdapterModel { Id = existing.Id, Name = "技術文件" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithNameUsedByOtherRecord_ShouldFail()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("技術文件");
        var other = await fixture.AddCategoryAsync("會議紀錄");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new CategoryAdapterModel { Id = other.Id, Name = "技術文件" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_ShouldPersistCategory()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddAsync(new CategoryAdapterModel { Name = "技術文件", Description = "說明", IsEnabled = true });

        Assert.True(result.Success);
        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync(x => x.Name == "技術文件");
        Assert.Equal("說明", saved.Description);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task AddAsync_WithUntrimmedName_ShouldPersistTrimmedName()
    {
        // 先前檢查時 Trim、寫入時不 Trim，帶空白的名稱會原樣入庫，
        // 之後乾淨的同名字串再也比不到它。
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(new CategoryAdapterModel { Name = "  技術文件  " });

        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync();
        Assert.Equal("技術文件", saved.Name);
    }

    [Fact]
    public async Task AddAsync_WithFullWidthSpace_ShouldPersistTrimmedName()
    {
        // String.Trim() 會移除全形空白 U+3000，SQLite 的 trim(X) 預設不會。
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(new CategoryAdapterModel { Name = "技術文件　" });

        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync();
        Assert.Equal("技術文件", saved.Name);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_AfterAddingUntrimmedName_ShouldRejectTrimmedName()
    {
        // 破口 #1 的完整重現：先建「技術文件 」再建「技術文件」，修正前兩筆都進得去。
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.AddAsync(new CategoryAdapterModel { Name = "技術文件 " });

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "技術文件" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_WithDuplicateName_ShouldReturnFriendlyMessage()
    {
        // 略過前置檢查直接寫入，模擬並發時兩個請求都通過檢查的情況：
        // 唯一索引擋下之後，訊息必須看得懂，而不是泛用的「新增分類失敗。」
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("技術文件");
        var service = fixture.CreateService();

        var result = await service.AddAsync(new CategoryAdapterModel { Name = "技術文件" });

        Assert.False(result.Success);
        Assert.Equal("分類名稱已存在，無法儲存。", result.Message);
        Assert.Equal(1, await fixture.Context.Category.AsNoTracking().CountAsync());
    }

    private sealed class CategoryServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private CategoryServiceFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<CategoryServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new CategoryServiceFixture(connection, context);
        }

        public CategoryService CreateService()
        {
            // 這組測試只驗證 CRUD 前置檢查，用管理員範圍避免團隊可見性介入。
            return new CategoryService(
                new TestDbContextFactory(connection),
                mapper,
                loggerFactory.CreateLogger<CategoryService>(),
                new FakeRecordAccessScopeProvider(isAdmin: true, []));
        }

        public async Task<Category> AddCategoryAsync(string name)
        {
            var category = new Category { Name = name };
            Context.Category.Add(category);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return category;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
