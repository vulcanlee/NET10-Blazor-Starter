using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

public sealed class MyUserServiceLoginTests
{
    [Fact]
    public async Task LoginAsync_WithNewFormatHash_AndCorrectPassword_ShouldSucceed()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("alice", "secret-password", legacy: false);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("alice", "secret-password");

        Assert.Equal(string.Empty, error);
        Assert.NotNull(user);
    }

    [Fact]
    public async Task LoginAsync_WithLegacyHash_AndCorrectPassword_ShouldUpgradeStoredHash()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        var created = await fixture.AddUserAsync("bob", "secret-password", legacy: true);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("bob", "secret-password");

        Assert.Equal(string.Empty, error);
        Assert.NotNull(user);

        var saved = await fixture.Context.MyUser.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.StartsWith("PBKDF2", saved.Password);
        Assert.Equal(
            PasswordVerificationOutcome.Success,
            SecurePasswordHasher.VerifyPassword("secret-password", saved.Password, saved.Salt));
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ShouldFail()
    {
        await using var fixture = await LoginFixture.CreateAsync();
        await fixture.AddUserAsync("carol", "secret-password", legacy: false);
        var service = fixture.CreateService();

        var (error, user) = await service.LoginAsync("carol", "wrong-password");

        Assert.NotEqual(string.Empty, error);
        Assert.Null(user);
    }

    private sealed class LoginFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private LoginFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<LoginFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new LoginFixture(connection, context);
        }

        public MyUserServiceLogin CreateService()
        {
            return new MyUserServiceLogin(
                Context,
                mapper,
                new ConfigurationBuilder().Build(),
                loggerFactory.CreateLogger<MyUserServiceLogin>(),
                new RolePermissionService());
        }

        public async Task<MyUser> AddUserAsync(string account, string password, bool legacy)
        {
            var salt = Guid.NewGuid().ToString();
            var user = new MyUser
            {
                Account = account,
                Name = account,
                Salt = salt,
                Status = true,
                Password = legacy
                    ? PasswordHelper.GetPasswordSHA(salt, password)
                    : SecurePasswordHasher.HashPassword(password),
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
            loggerFactory.Dispose();
        }
    }
}
