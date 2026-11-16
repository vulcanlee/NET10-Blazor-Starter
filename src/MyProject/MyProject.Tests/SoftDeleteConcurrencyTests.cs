using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 刪除、還原、永久刪除遇到並行衝突（0.10.34 起補測）。修改的衝突見 <see cref="OptimisticConcurrencyTests"/>。
///
/// 情境：服務讀出資料後、存檔之前，另一個人改了同一筆（版本號變了）。用 <see cref="SaveChangesInterceptor"/> 在存檔前
/// 以另一個 context 改掉版本號，存檔時 EF Core 比對不到原版本號 → 服務要回「衝突」，資料庫保留對方的版本，不能刪掉或還原。
/// </summary>
public sealed class SoftDeleteConcurrencyTests : IAsyncDisposable
{
    private const string OtherStamp = "changed-by-someone-else";

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly CurrentUserService currentUser = new();
    private ChangeStampBeforeSave? hook;

    public enum Operation
    {
        Delete,
        Restore,
        Purge,
    }

    public static TheoryData<string, Operation> Cases()
    {
        var data = new TheoryData<string, Operation>();
        foreach (var type in new[] { nameof(Project), nameof(Category), nameof(Team), nameof(RoleView), nameof(MyUser) })
        {
            foreach (var operation in Enum.GetValues<Operation>())
            {
                data.Add(type, operation);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task WhenSomeoneElseChangedTheRecordFirst_ShouldReturnConflict_AndKeepTheirVersion(string type, Operation operation)
    {
        await connection.OpenAsync();
        await using (var db = NewContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var deleted = operation != Operation.Delete;
        var id = await SeedAsync(type, deleted);
        hook = new ChangeStampBeforeSave(connection, type, id);

        var result = await RunAsync(type, operation, id);

        Assert.True(hook.Fired, $"{type}.{operation} 沒有存到這一筆，測試沒有測到衝突。");
        Assert.False(result.Success, $"{type}.{operation} 存檔時版本號已被別人改掉，應該回衝突，實際卻成功了。");
        Assert.Equal(VerifyRecordErrorKind.Conflict, result.Kind);
        Assert.IsType<DbUpdateConcurrencyException>(result.Exception);
        var (exists, isDeleted, stamp) = await ReadAsync(type, id);
        Assert.True(exists, "衝突時不可以永久刪除。");
        Assert.Equal(deleted, isDeleted);
        Assert.Equal(OtherStamp, stamp);
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    private async Task<VerifyRecordResult> RunAsync(string type, Operation operation, int id)
    {
        await using var shared = NewContext();
        return type switch
        {
            nameof(Project) => await Run(new ProjectService(Factory(), NullLogger<ProjectService>.Instance, Options.Create(new SystemSettings()),
                new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser,
                new ProjectFileStore(Options.Create(new SystemSettings()), NullLogger<ProjectFileStore>.Instance)),
                (s, x) => s.DeleteAsync(x), (s, x) => s.RestoreAsync(x), (s, x) => s.PurgeAsync(x)),
            nameof(Category) => await Run(new CategoryService(Factory(), NullLogger<CategoryService>.Instance,
                new FakeRecordAccessScopeProvider(true, []), new RecordingAuditLogService(), currentUser),
                (s, x) => s.DeleteAsync(x), (s, x) => s.RestoreAsync(x), (s, x) => s.PurgeAsync(x)),
            nameof(Team) => await Run(new TeamService(Factory(), NullLogger<TeamService>.Instance, new RecordingAuditLogService(), currentUser,
                new ContextTeamTreeCache(shared)),
                (s, x) => s.DeleteAsync(x), (s, x) => s.RestoreAsync(x), (s, x) => s.PurgeAsync(x)),
            nameof(RoleView) => await Run(new RoleViewService(Factory(), NullLogger<RoleViewService>.Instance, new RolePermissionService(),
                new RbacWriteService(shared, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser),
                (s, x) => s.DeleteAsync(x), (s, x) => s.RestoreAsync(x), (s, x) => s.PurgeAsync(x)),
            _ => await Run(new MyUserService(Factory(), NullLogger<MyUserService>.Instance,
                new RbacWriteService(shared, NullLogger<RbacWriteService>.Instance), new RecordingAuditLogService(), currentUser,
                Options.Create(new BootstrapSettings()), PasswordTestDefaults.Policy()),
                (s, x) => s.DeleteAsync(x), (s, x) => s.RestoreAsync(x), (s, x) => s.PurgeAsync(x)),
        };

        Task<VerifyRecordResult> Run<TService>(
            TService service,
            Func<TService, int, Task<VerifyRecordResult>> delete,
            Func<TService, int, Task<VerifyRecordResult>> restore,
            Func<TService, int, Task<VerifyRecordResult>> purge) => operation switch
            {
                Operation.Delete => delete(service, id),
                Operation.Restore => restore(service, id),
                _ => purge(service, id),
            };
    }

    private async Task<int> SeedAsync(string type, bool deleted)
    {
        await using var db = NewContext();
        var deletedAt = deleted ? DateTime.Now.AddDays(-1) : (DateTime?)null;
        ISoftDeletable entity = type switch
        {
            nameof(Project) => new Project { Title = "並行專案", Owner = "owner", Status = "未開始", Priority = "中" },
            nameof(Category) => new Category { Name = "並行分類", IsEnabled = true },
            nameof(Team) => new Team { Name = "並行團隊" },
            nameof(RoleView) => new RoleView { Name = "並行角色", TabViewJson = "[]" },
            _ => new MyUser { Account = "concurrent", Name = "並行使用者", Password = "x", Status = true },
        };
        entity.IsDeleted = deleted;
        entity.DeletedAt = deletedAt;
        db.Add(entity);
        await db.SaveChangesAsync();
        return (int)db.Entry(entity).Property("Id").CurrentValue!;
    }

    private async Task<(bool Exists, bool IsDeleted, string? Stamp)> ReadAsync(string type, int id)
    {
        // 直接查資料表：EF Core 的 Find 會套用「未刪除」的全域篩選，看不到已刪除的那一筆。
        await using var db = NewContext();
        var table = db.Model.GetEntityTypes().Single(x => x.ClrType.Name == type).GetTableName();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"IsDeleted\", \"ConcurrencyStamp\" FROM \"{table}\" WHERE \"Id\" = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (true, reader.GetBoolean(0), reader.GetString(1))
            : (false, false, null);
    }

    private BackendDBContext NewContext()
        => new(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);

    private HookedFactory Factory() => new(connection, () => hook);

    private sealed class HookedFactory(SqliteConnection connection, Func<IInterceptor?> interceptor) : IDbContextFactory<BackendDBContext>
    {
        public BackendDBContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection);
            if (interceptor() is { } hook)
            {
                options.AddInterceptors(hook);
            }

            return new BackendDBContext(options.Options);
        }
    }

    /// <summary>服務存到目標那一筆（修改或刪除）之前，用另一個 context 改掉它的版本號；只做一次。</summary>
    private sealed class ChangeStampBeforeSave(SqliteConnection connection, string type, int id) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var target = eventData.Context!.ChangeTracker.Entries()
                .Any(entry => entry.Metadata.ClrType.Name == type
                    && entry.State is (EntityState.Modified or EntityState.Deleted)
                    && Equals(entry.Property("Id").CurrentValue, id));
            if (target && !Fired)
            {
                Fired = true;
                await using var other = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
                var table = other.Model.GetEntityTypes().Single(x => x.ClrType.Name == type).GetTableName();
#pragma warning disable EF1002 // 表名來自 EF 模型，不是使用者輸入
                await other.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"{table}\" SET \"ConcurrencyStamp\" = {{0}} WHERE \"Id\" = {{1}}", [OtherStamp, id], cancellationToken);
#pragma warning restore EF1002
            }

            return result;
        }
    }
}
