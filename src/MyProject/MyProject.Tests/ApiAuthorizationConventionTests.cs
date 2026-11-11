using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Web.Filters;

namespace MyProject.Tests;

/// <summary>
/// 每個 Web API action 都要明確標註授權意圖（0.10.4 起，WASM 遷移 M5）：
/// <c>[HasPermission]</c>（頁面或動作權限）、<c>[AdminOnly]</c>（只有管理員）、<c>[SignedInUser]</c>（登入即可）或 <c>[AllowAnonymous]</c>。
/// 只有 <c>[Authorize]</c> 而沒有其他標註的端點，任何登入的人都能呼叫 —— 新增端點時忘了加權限是最常見的越權漏洞，這裡在編譯後就擋下。
/// 標在 controller 類別上等於標在它的每個 action。
/// </summary>
public sealed class ApiAuthorizationConventionTests
{
    private static readonly Type[] IntentAttributes =
    [
        typeof(HasPermissionAttribute),
        typeof(AdminOnlyAttribute),
        typeof(SignedInUserAttribute),
        typeof(AllowAnonymousAttribute),
    ];

    [Fact]
    public void EveryApiAction_ShouldDeclareWhoMayCallIt()
    {
        var missing = ApiActions()
            .Where(x => !HasIntent(x.Controller) && !HasIntent(x.Action))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        Assert.True(missing.Count == 0, "以下 API action 沒有標註授權意圖（[HasPermission]／[AdminOnly]／[SignedInUser]／[AllowAnonymous]）：\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// 審查修正（0.10.4）：<c>[SignedInUser]</c> 本身沒有行為、<c>[HasPermission]</c>／<c>[AdminOnly]</c> 遇到匿名請求只回 401 而不負責驗證身分，
    /// 所以除了 <c>[AllowAnonymous]</c> 的端點，controller 或 action 上一定要有 <c>[Authorize]</c>（指定驗證機制）。
    /// </summary>
    [Fact]
    public void ProtectedActions_ShouldAlsoRequireAuthentication()
    {
        var missing = ApiActions()
            .Where(x => !x.Action.IsDefined(typeof(AllowAnonymousAttribute), true) && !x.Controller.IsDefined(typeof(AllowAnonymousAttribute), true))
            .Where(x => !x.Action.IsDefined(typeof(AuthorizeAttribute), true) && !x.Controller.IsDefined(typeof(AuthorizeAttribute), true))
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        Assert.True(missing.Count == 0, "以下 API action 有授權標註卻沒有 [Authorize]：\n" + string.Join("\n", missing));
    }

    [Fact]
    public void TheConventionShouldActuallyFindTheControllers()
    {
        var controllers = ApiActions().Select(x => x.Controller.Name).Distinct().ToList();

        Assert.Contains("ProjectController", controllers);
        Assert.Contains("AuditLogsController", controllers);
        Assert.True(controllers.Count >= 10, $"只找到 {controllers.Count} 個 controller：{string.Join("、", controllers)}");
    }

    private static bool HasIntent(MemberInfo member)
        => IntentAttributes.Any(type => member.GetCustomAttributes(type, inherit: true).Length > 0);

    private static IEnumerable<(Type Controller, MethodInfo Action)> ApiActions()
        => typeof(HasPermissionAttribute).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<ApiControllerAttribute>() is not null)
            // 不用 DeclaredOnly：繼承自自訂基底 controller 的 action 也要檢查（審查發現）；ControllerBase 本身的方法不是 action。
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
                .Where(m => m.DeclaringType is { } declaring && declaring != typeof(ControllerBase) && declaring != typeof(object)
                    && typeof(ControllerBase).IsAssignableFrom(declaring))
                .Select(m => (t, m)));
}
