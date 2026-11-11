using MyProject.Share.Helpers;

namespace MyProject.Web.Dashboard;

/// <summary>
/// 首頁的一個小工具（0.9.106 起；0.10.6 起首頁在 WASM 執行，元件在 <c>MyProject.Web.Client/Views/Dashboard</c>，
/// 以 <paramref name="Id"/> 對應 <c>DashboardWidgetComponents</c>）。
/// </summary>
/// <param name="Id">唯一代號（也用在 HTML 的 <c>data-widget</c>，並對應前端的元件）。</param>
/// <param name="Title">卡片標題。</param>
/// <param name="Order">排序，小的在前；同順序依 <paramref name="Id"/>。</param>
/// <param name="PermissionKey">需要的頁面權限鍵（例如 <c>MagicObjectHelper.角色_專案項目</c>）；null＝登入就看得到。</param>
/// <param name="AdminOnly">只有管理員看得到。</param>
/// <param name="Icon">卡片標題前的 classic Material Icons 名稱（見速查表 §6.1）。</param>
public sealed record DashboardWidgetDescriptor(string Id, string Title, int Order, string? PermissionKey, bool AdminOnly, string Icon);

/// <summary>所有登記的小工具（singleton），依權限過濾與排序。</summary>
public sealed class DashboardWidgetCatalog
{
    public DashboardWidgetCatalog(IEnumerable<DashboardWidgetDescriptor> widgets)
    {
        All = widgets.OrderBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        var duplicate = All.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"首頁小工具的代號「{duplicate.Key}」登記了兩次。");
        }
    }

    public IReadOnlyList<DashboardWidgetDescriptor> All { get; }

    /// <summary>這個人看得到的小工具：管理員專屬的只給管理員；有權限鍵的要有該頁權限（管理員一律有）。</summary>
    public IReadOnlyList<DashboardWidgetDescriptor> VisibleTo(bool isAdmin, Func<string, bool> hasPagePermission)
        => All.Where(x => (!x.AdminOnly || isAdmin) && (x.PermissionKey is null || isAdmin || hasPagePermission(x.PermissionKey))).ToList();
}

/// <summary>首頁的快速入口。<paramref name="PermissionKey"/> 用於濾掉目前使用者沒有權限的項目。</summary>
public sealed record DashboardQuickLink(string Url, string Icon, string Title, string PermissionKey);

/// <summary>首頁的快速入口（0.10.6 起由伺服器依權限過濾後送給 WASM 首頁）。</summary>
public static class DashboardQuickLinks
{
    /// <summary>
    /// ⚠️ 圖示名稱必須是 <b>classic Material Icons</b>（App.razor 載入的是 Material Icons，
    /// 不是 Material Symbols）。用 Symbols 專有名稱會渲染失敗。見開發慣例速查 §6.1。
    /// </summary>
    public static readonly IReadOnlyList<DashboardQuickLink> All =
    [
        new("/projects", "workspaces", "專案項目", MagicObjectHelper.角色_專案項目),
        new("/categories", "category", "分類清單", MagicObjectHelper.角色_分類清單),
        new("/teams", "groups", "團隊清單", MagicObjectHelper.角色_團隊清單),
    ];
}

public static class DashboardServiceCollectionExtensions
{
    /// <summary>登記一個首頁小工具。新增小工具＝在 Client 寫一個元件並登記到 <c>DashboardWidgetComponents</c>＋在 <see cref="AddDashboard"/> 加一行＋資料端點。</summary>
    public static IServiceCollection AddDashboardWidget(
        this IServiceCollection services, string id, string title, int order, string icon, string? permissionKey = null, bool adminOnly = false)
        => services.AddSingleton(new DashboardWidgetDescriptor(id, title, order, permissionKey, adminOnly, icon));

    /// <summary>首頁儀表板與內建的三個小工具。</summary>
    public static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddDashboardWidget("notifications", "我的通知", 10, "notifications");
        services.AddDashboardWidget("account", "我的帳號", 20, "account_circle");
        services.AddDashboardWidget("scheduled-jobs", "排程作業", 30, "schedule", adminOnly: true);
        services.AddSingleton<DashboardWidgetCatalog>();
        return services;
    }
}
