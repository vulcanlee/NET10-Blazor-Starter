using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;

namespace MyProject.Tests;

public sealed class PermissionCheckerTests
{
    [Fact]
    public async Task HasPermissionAsync_ForAdmin_ShouldReturnTrueForAnyKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("root", isAdmin: true, permissions: Array.Empty<string>());
        var checker = new PermissionChecker(fixture.Context);

        Assert.True(await checker.HasPermissionAsync(user.Id, "任何權限"));
    }

    [Fact]
    public async Task HasPermissionAsync_WhenRoleHasKey_ShouldReturnTrue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("alice", isAdmin: false, permissions: new[] { "專案項目", "首頁" });
        var checker = new PermissionChecker(fixture.Context);

        Assert.True(await checker.HasPermissionAsync(user.Id, "專案項目"));
    }

    [Fact]
    public async Task HasPermissionAsync_WhenRoleLacksKey_ShouldReturnFalse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("bob", isAdmin: false, permissions: new[] { "首頁" });
        var checker = new PermissionChecker(fixture.Context);

        Assert.False(await checker.HasPermissionAsync(user.Id, "專案項目"));
    }

    [Fact]
    public async Task HasPermissionAsync_ForUnknownUser_ShouldReturnFalse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var checker = new PermissionChecker(fixture.Context);

        Assert.False(await checker.HasPermissionAsync(999, "首頁"));
    }

    [Fact]
    public async Task GetEffectivePermissionKeysAsync_ShouldReturnRoleKeys()
    {
        await using var fixture = await Fixture.CreateAsync();
        var user = await fixture.AddUserAsync("carol", isAdmin: false, permissions: new[] { "首頁", "工作項目" });
        var checker = new PermissionChecker(fixture.Context);

        var keys = await checker.GetEffectivePermissionKeysAsync(user.Id);

        Assert.Contains("首頁", keys);
        Assert.Contains("工作項目", keys);
        Assert.Equal(2, keys.Count);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private Fixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public BackendDBContext Context { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(connection, context);
        }

        public async Task<MyUser> AddUserAsync(string account, bool isAdmin, string[] permissions)
        {
            var role = new RoleView
            {
                Name = account + "-role",
                TabViewJson = JsonSerializer.Serialize(permissions),
            };
            Context.RoleView.Add(role);
            await Context.SaveChangesAsync();

            var user = new MyUser
            {
                Account = account,
                Name = account,
                Password = "x",
                Status = true,
                IsAdmin = isAdmin,
                RoleViewId = role.Id,
            };
            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
