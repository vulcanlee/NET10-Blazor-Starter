using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;

namespace MyProject.Tests;

/// <summary>
/// 測試用的 <see cref="IDbContextFactory{TContext}"/>：在**同一條 in-memory SQLite 連線**上
/// 產生新的 <see cref="BackendDBContext"/>。
///
/// 這樣才能真實反映正式環境的行為 —— 服務每次操作各拿一個全新、乾淨的 context
/// （0.4.36 起 Blazor 路徑的服務都改注入工廠），而不是整個測試共用一個。
///
/// 註：`Data Source=:memory:` 的資料庫存活時間等同連線本身，
/// 因此只要連線還開著，多個 context 看到的就是同一份資料。
/// </summary>
public sealed class TestDbContextFactory : IDbContextFactory<BackendDBContext>
{
    private readonly SqliteConnection connection;

    public TestDbContextFactory(SqliteConnection connection)
    {
        this.connection = connection;
    }

    public BackendDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connection)
            .Options;

        return new BackendDBContext(options);
    }
}
