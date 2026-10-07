using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.Business.Services.Other;

namespace MyProject.Tests;

public sealed class AuditLogServiceTests
{
    [Fact]
    public async Task WriteAsync_ShouldPersistAuditRow()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.WriteAsync(
            action: "Login.Success",
            success: true,
            actorUserId: 7,
            actorAccount: "alice",
            targetType: "MyUser",
            targetId: "7",
            detail: "unit test");

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("Login.Success", saved.Action);
        Assert.True(saved.Success);
        Assert.Equal(7, saved.ActorUserId);
        Assert.Equal("alice", saved.ActorAccount);
        Assert.Equal("MyUser", saved.TargetType);
        Assert.Equal("7", saved.TargetId);
        Assert.NotEqual(default, saved.OccurredAt);
        Assert.Null(saved.ClientIp); // 沒有 IClientIpProvider（例如排程作業的情境）就不記 IP
    }

    [Fact]
    public async Task WriteAsync_ShouldPersistClientIpFromProvider()
    {
        await using var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService(new FixedClientIpProvider("203.0.113.5"));

        await service.WriteAsync("Login.Success", actorUserId: 7, actorAccount: "alice");

        var saved = await fixture.Context.AuditLog.AsNoTracking().SingleAsync();
        Assert.Equal("203.0.113.5", saved.ClientIp);
    }

    [Fact]
    public async Task WriteAsync_WhenPersistFails_ShouldNotThrow()
    {
        var fixture = await AuditFixture.CreateAsync();
        var service = fixture.CreateService();
        await fixture.DisposeAsync(); // context disposed → write will fail internally

        var exception = await Record.ExceptionAsync(() => service.WriteAsync("Login.Failed", success: false));

        Assert.Null(exception);
    }

    private sealed class AuditFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;

        private AuditFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
        }

        public BackendDBContext Context { get; }

        public static async Task<AuditFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new AuditFixture(connection, context);
        }

        public AuditLogService CreateService(IClientIpProvider? clientIpProvider = null)
        {
            return new AuditLogService(Context, loggerFactory.CreateLogger<AuditLogService>(), clientIpProvider);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    private sealed class FixedClientIpProvider(string? ip) : IClientIpProvider
    {
        public string? GetClientIp() => ip;
    }
}
