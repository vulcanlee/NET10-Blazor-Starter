using Microsoft.Extensions.DependencyInjection;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Web.Client.Auth;

namespace MyProject.Tests.Components;

/// <summary>
/// 元件測試用的 <see cref="PagePermissions"/>：目前使用者取自測試登記的 <see cref="CurrentUserService"/>。
/// 0.10.9 起（WASM 遷移 M10）Server 版的 <c>ServerPagePermissions</c> 已刪除，正式環境只有 WASM 的 <see cref="ClientPagePermissions"/>（<c>/me</c>）；
/// 規則同一份（<see cref="PagePermissions"/> 本身），這裡只換掉「目前使用者從哪裡來」。
/// </summary>
internal sealed class TestPagePermissions : PagePermissions
{
    private readonly CurrentUserService currentUserService;

    public TestPagePermissions(CurrentUserService currentUserService) => this.currentUserService = currentUserService;

    protected override CurrentUser User => currentUserService.CurrentUser;

    /// <summary>測試先登記 <see cref="CurrentUserService"/>，再呼叫這裡補上權限判斷。</summary>
    internal static void AddTo(IServiceCollection services) => services.AddScoped<PagePermissions, TestPagePermissions>();
}
