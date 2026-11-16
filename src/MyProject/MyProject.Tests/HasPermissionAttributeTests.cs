using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;
using MyProject.Web.Filters;

namespace MyProject.Tests;

/// <summary>
/// API 動作級授權 <see cref="HasPermissionAttribute"/> 的每一道防線（0.10.34 起）。
/// 整合測試走不到「未登入」「身分解析不到」：Controller 前面的 <c>RequireAuthorization()</c> 會先擋，
/// 所以這裡直接建 <see cref="AuthorizationFilterContext"/> 呼叫，確保前面的設定被改掉時，這裡仍是最後一道防線。
/// </summary>
public sealed class HasPermissionAttributeTests
{
    private const string Page = "專案項目";
    private const string Path = "/api/v1/Project";

    [Fact]
    public async Task Anonymous_ShouldGet401_WithoutCheckingPermissionOrWritingAudit()
    {
        var (context, checker, audit) = CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));

        await new HasPermissionAttribute(Page, PermissionActions.View).OnAuthorizationAsync(context);

        AssertStatus(context, StatusCodes.Status401Unauthorized);
        Assert.Empty(checker.Calls);
        Assert.Empty(audit.Entries); // 匿名流量灌不爆稽核表
    }

    [Theory]
    [InlineData(null)]
    [InlineData("support")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task UnparsableUserId_ShouldGet401_WithoutCheckingPermissionOrWritingAudit(string? nameIdentifier)
    {
        var (context, checker, audit) = CreateContext(Authenticated(nameIdentifier));

        await new HasPermissionAttribute(Page, PermissionActions.View).OnAuthorizationAsync(context);

        AssertStatus(context, StatusCodes.Status401Unauthorized);
        Assert.Empty(checker.Calls);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task MissingPermission_ShouldGet403_AndWriteADeniedAudit()
    {
        var (context, checker, audit) = CreateContext(Authenticated("7", "alice"), granted: false);

        await new HasPermissionAttribute(Page, PermissionActions.Delete).OnAuthorizationAsync(context);

        AssertStatus(context, StatusCodes.Status403Forbidden);
        Assert.Equal([(7, PermissionKey.For(Page, PermissionActions.Delete))], checker.Calls);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.Permission.Denied, entry.Action);
        Assert.False(entry.Success);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Equal("alice", entry.ActorAccount);
        Assert.Contains(PermissionKey.For(Page, PermissionActions.Delete), entry.Detail);
        Assert.Equal(Path, Assert.Single(audit.Contents).TargetId);
    }

    [Fact]
    public async Task WhenTheDeniedAuditFails_TheRequestShouldStillBeRejected()
    {
        var (context, _, _) = CreateContext(Authenticated("7", "alice"), granted: false, auditThrows: true);

        await new HasPermissionAttribute(Page, PermissionActions.Delete).OnAuthorizationAsync(context);

        AssertStatus(context, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task GrantedPermission_ShouldLetTheRequestThrough_WithoutAudit()
    {
        var (context, checker, audit) = CreateContext(Authenticated("7", "alice"), granted: true);

        await new HasPermissionAttribute(Page, PermissionActions.View).OnAuthorizationAsync(context);

        Assert.Null(context.Result);
        Assert.Single(checker.Calls);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task PageLevelConstructor_ShouldCheckTheBareKey()
    {
        var (context, checker, _) = CreateContext(Authenticated("7", "alice"), granted: true);

        await new HasPermissionAttribute(Page).OnAuthorizationAsync(context);

        Assert.Equal([(7, Page)], checker.Calls);
    }

    private static ClaimsPrincipal Authenticated(string? nameIdentifier, string? account = null)
    {
        var claims = new List<Claim>();
        if (nameIdentifier is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        }

        if (account is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, account));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static (AuthorizationFilterContext Context, RecordingPermissionChecker Checker, AuditDouble Audit) CreateContext(
        ClaimsPrincipal user, bool granted = false, bool auditThrows = false)
    {
        var checker = new RecordingPermissionChecker(granted);
        var audit = new AuditDouble(auditThrows);
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddSingleton<IPermissionChecker>(checker)
            .AddSingleton<IAuditLogService>(audit)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services, User = user };
        httpContext.Request.Path = Path;
        var context = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());
        return (context, checker, audit);
    }

    private static void AssertStatus(AuthorizationFilterContext context, int expected)
        => Assert.Equal(expected, Assert.IsType<ObjectResult>(context.Result).StatusCode);

    private sealed class RecordingPermissionChecker(bool granted) : IPermissionChecker
    {
        public List<(int UserId, string Key)> Calls { get; } = [];

        public Task<bool> HasPermissionAsync(int userId, string permissionKey)
        {
            Calls.Add((userId, permissionKey));
            return Task.FromResult(granted);
        }

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId)
            => Task.FromResult<IReadOnlyCollection<string>>([]);
    }

    private sealed class AuditDouble(bool throws) : IAuditLogService
    {
        private readonly RecordingAuditLogService inner = new();

        public List<(string Action, bool Success, int? ActorUserId, string? ActorAccount, string? Detail)> Entries => inner.Entries;

        public List<(string Action, string? TargetId, string? Content)> Contents => inner.Contents;

        public Task WriteAsync(
            string action, bool success = true, int? actorUserId = null, string? actorAccount = null,
            string? targetType = null, string? targetId = null, string? detail = null, string? content = null)
            => throws
                ? throw new InvalidOperationException("稽核寫入失敗（測試）")
                : inner.WriteAsync(action, success, actorUserId, actorAccount, targetType, targetId, detail, content);
    }
}
