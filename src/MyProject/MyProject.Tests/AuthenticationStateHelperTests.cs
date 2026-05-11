using AutoMapper;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using System.Security.Claims;

namespace MyProject.Tests;

public sealed class AuthenticationStateHelperTests
{
    [Fact]
    public async Task Check_WithUnauthenticatedPrincipal_ShouldReturnUnauthenticatedAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Unauthenticated, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithInvalidSidClaim_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal("not-a-number"));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMissingUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal("999"));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithDisabledUser_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(status: false);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithMissingRoleView_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserWithoutRoleAsync();
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithInvalidRoleJson_ShouldReturnInvalidUserAndNavigateLogout()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(roleJson: "not-json");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.InvalidUser, result);
        Assert.Equal("/Auths/Logout", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WhenPasswordChangeRequiredOutsideChangePasswordPage_ShouldReturnRequiresPasswordChange()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WhenPasswordChangeRequiredOnChangePasswordPageWithQuery_ShouldSucceed()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/ChangePassword?returnUrl=/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Null(navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithCachedUserStillRechecksPasswordChangeRequirement()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(password: MagicObjectHelper.NeedChangePassword);
        fixture.CurrentUserService.CurrentUser.IsAuthenticated = true;
        fixture.CurrentUserService.CurrentUser.Id = user.Id;
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.RequiresPasswordChange, result);
        Assert.Equal("/ChangePassword", navigationManager.NavigatedTo);
    }

    [Fact]
    public async Task Check_WithValidUser_ShouldInitializeCurrentUser()
    {
        await using var fixture = await AuthenticationStateHelperFixture.CreateAsync();
        var user = await fixture.AddUserAsync(permissionName: "PermissionA");
        var authProvider = new TestAuthenticationStateProvider(CreatePrincipal(user.Id.ToString()));
        var navigationManager = new TestNavigationManager("http://localhost/App");

        var result = await fixture.CreateHelper().Check(authProvider, navigationManager);

        Assert.Equal(AuthenticationCheckResult.Succeeded, result);
        Assert.Equal(user.Id, fixture.CurrentUserService.CurrentUser.Id);
        Assert.True(fixture.CurrentUserService.CurrentUser.IsAuthenticated);
        Assert.Contains("PermissionA", fixture.CurrentUserService.CurrentUser.RoleList);
        Assert.Null(navigationManager.NavigatedTo);
    }

    private static ClaimsPrincipal CreatePrincipal(string sid)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Sid, sid)],
            "Test");

        return new ClaimsPrincipal(identity);
    }

    private sealed class AuthenticationStateHelperFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private AuthenticationStateHelperFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            CurrentUserService = new CurrentUserService();

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public CurrentUserService CurrentUserService { get; }

        public static async Task<AuthenticationStateHelperFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new AuthenticationStateHelperFixture(connection, context);
        }

        public AuthenticationStateHelper CreateHelper()
        {
            var rolePermissionService = new RolePermissionService();

            return new AuthenticationStateHelper(
                loggerFactory.CreateLogger<AuthenticationStateHelper>(),
                mapper,
                new MyUserService(Context, mapper, loggerFactory.CreateLogger<MyUserService>()),
                CurrentUserService,
                rolePermissionService);
        }

        public async Task<MyUser> AddUserAsync(
            bool status = true,
            string password = "secure-password",
            string roleJson = """["PermissionA"]""",
            string permissionName = "PermissionA")
        {
            var roleView = new RoleView
            {
                Name = $"role-{Guid.NewGuid():N}",
                TabViewJson = roleJson == """["PermissionA"]"""
                    ? $"""["{permissionName}"]"""
                    : roleJson
            };

            Context.RoleView.Add(roleView);
            await Context.SaveChangesAsync();

            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = status,
                RoleViewId = roleView.Id
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, password);

            Context.MyUser.Add(user);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return user;
        }

        public async Task<MyUser> AddUserWithoutRoleAsync()
        {
            var user = new MyUser
            {
                Account = $"user-{Guid.NewGuid():N}",
                Name = "Test User",
                Salt = Guid.NewGuid().ToString(),
                Status = true
            };
            user.Password = PasswordHelper.GetPasswordSHA(user.Salt, "secure-password");

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

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal principal;

        public TestAuthenticationStateProvider(ClaimsPrincipal principal)
        {
            this.principal = principal;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            return Task.FromResult(new AuthenticationState(principal));
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string initialUri)
        {
            Initialize("http://localhost/", initialUri);
        }

        public string? NavigatedTo { get; private set; }

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            NavigatedTo = uri;
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }
}
