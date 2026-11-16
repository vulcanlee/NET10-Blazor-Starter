using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 管理清單的欄位排序（0.10.34 起補測）：分類、團隊、角色、使用者每個可排序欄位都要升冪、降冪各一次。
/// 專案清單的排序見 <see cref="ProjectServiceTeamAccessTests"/>。
///
/// 種子資料「第 1～3 筆」的每個欄位值都依序遞增；布林欄位只有兩種值，第 2、3 筆相同時要靠 Id 排（Id 也依序遞增），
/// 所以升冪一定是 1、2、3，降冪一定是 3、2、1。排序分支寫錯或退回預設排序，兩個方向至少會有一個不對。
/// </summary>
public sealed class ListSortingTests : IAsyncDisposable
{
    private static readonly DateTime BaseTime = new(2026, 8, 27, 10, 0, 0);

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly CurrentUserService currentUser = new();

    [Theory]
    [InlineData(nameof(CategoryAdapterModel.Name))]
    [InlineData(nameof(CategoryAdapterModel.IsEnabled))]
    [InlineData(nameof(CategoryAdapterModel.CreatedAt))]
    [InlineData(nameof(CategoryAdapterModel.UpdatedAt))]
    public async Task Category_ShouldSortByEachColumn(string sortField)
    {
        await using var db = await CreateAsync();
        db.Category.AddRange(Ranks.Select(rank => new Category
        {
            Name = $"分類{rank}",
            IsEnabled = rank > 1,
            CreatedAt = BaseTime.AddMinutes(rank),
            UpdatedAt = BaseTime.AddHours(rank),
        }));
        await SaveInOrderAsync(db);
        var service = new CategoryService(Factory(), NullLogger<CategoryService>.Instance,
            new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser);

        await AssertSortedAsync(sortField, request => service.GetAsync(request), item => item.Name, "分類");
    }

    [Theory]
    [InlineData(nameof(TeamAdapterModel.Name))]
    [InlineData(nameof(TeamAdapterModel.Code))]
    [InlineData(nameof(TeamAdapterModel.IsEnabled))]
    [InlineData(nameof(TeamAdapterModel.CreatedAt))]
    [InlineData(nameof(TeamAdapterModel.UpdatedAt))]
    public async Task Team_ShouldSortByEachColumn(string sortField)
    {
        await using var db = await CreateAsync();
        db.Team.AddRange(Ranks.Select(rank => new Team
        {
            Name = $"團隊{rank}",
            Code = $"T{rank}",
            IsEnabled = rank > 1,
            CreatedAt = BaseTime.AddMinutes(rank),
            UpdatedAt = BaseTime.AddHours(rank),
        }));
        await SaveInOrderAsync(db);
        var service = new TeamService(Factory(), NullLogger<TeamService>.Instance, new RecordingAuditLogService(), currentUser,
            new ContextTeamTreeCache(db));

        await AssertSortedAsync(sortField, request => service.GetAsync(request), item => item.Name, "團隊");
    }

    [Theory]
    [InlineData(nameof(RoleViewAdapterModel.Name))]
    [InlineData(nameof(RoleViewAdapterModel.CreateAt))]
    [InlineData(nameof(RoleViewAdapterModel.UpdateAt))]
    public async Task RoleView_ShouldSortByEachColumn(string sortField)
    {
        await using var db = await CreateAsync();
        db.RoleView.AddRange(Ranks.Select(rank => new RoleView
        {
            Name = $"角色{rank}",
            TabViewJson = "[]",
            CreateAt = BaseTime.AddMinutes(rank),
            UpdateAt = BaseTime.AddHours(rank),
        }));
        await SaveInOrderAsync(db);
        var service = new RoleViewService(Factory(), NullLogger<RoleViewService>.Instance, new RolePermissionService(),
            new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser);

        await AssertSortedAsync(sortField, request => service.GetAsync(request), item => item.Name, "角色");
    }

    [Theory]
    [InlineData(nameof(MyUserAdapterModel.Account))]
    [InlineData(nameof(MyUserAdapterModel.Name))]
    [InlineData(nameof(MyUserAdapterModel.Email))]
    [InlineData(nameof(MyUserAdapterModel.RoleViewName))]
    [InlineData(nameof(MyUserAdapterModel.StatusText))]
    [InlineData(nameof(MyUserAdapterModel.IsAdminText))]
    [InlineData(nameof(MyUserAdapterModel.CreateAt))]
    [InlineData(nameof(MyUserAdapterModel.UpdateAt))]
    public async Task MyUser_ShouldSortByEachColumn(string sortField)
    {
        await using var db = await CreateAsync();
        foreach (var rank in Ranks)
        {
            db.MyUser.Add(new MyUser
            {
                Account = $"user{rank}",
                Name = $"使用者{rank}",
                Password = "x",
                Email = $"user{rank}@example.invalid",
                RoleView = new RoleView { Name = $"角色{rank}", TabViewJson = "[]" },
                Status = rank > 1,
                IsAdmin = rank > 1,
                CreateAt = BaseTime.AddMinutes(rank),
                UpdateAt = BaseTime.AddHours(rank),
            });
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        var service = new MyUserService(Factory(), NullLogger<MyUserService>.Instance,
            new RbacWriteService(db, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser,
            Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy());

        await AssertSortedAsync(sortField, request => service.GetAsync(request), item => item.Name, "使用者");
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    private static readonly int[] Ranks = [1, 2, 3];

    private static async Task AssertSortedAsync<T>(
        string sortField, Func<DataRequest, Task<DataRequestResult<T>>> query, Func<T, string> name, string prefix)
    {
        var request = new DataRequest { Search = string.Empty, SortField = sortField, SortDescending = false, CurrentPage = 1, PageSize = 50 };
        var ascending = (await query(request)).Result.Select(name).ToList();
        request.SortDescending = true;
        var descending = (await query(request)).Result.Select(name).ToList();

        Assert.Equal([$"{prefix}1", $"{prefix}2", $"{prefix}3"], ascending);
        Assert.Equal([$"{prefix}3", $"{prefix}2", $"{prefix}1"], descending);
    }

    /// <summary>一筆一筆存，Id 才保證依第 1～3 筆遞增（布林欄位同值時靠 Id 排）。</summary>
    private static async Task SaveInOrderAsync(BackendDBContext db)
    {
        var pending = db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToList();
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entity in pending)
        {
            db.Add(entity);
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
    }

    private async Task<BackendDBContext> CreateAsync()
    {
        await connection.OpenAsync();
        var db = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private TestDbContextFactory Factory() => new(connection);
}
