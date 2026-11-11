using Microsoft.AspNetCore.Components;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Views.Dashboard;

/// <summary>
/// 首頁小工具代號 → 元件（0.10.6 起，WASM 遷移 M7）。哪些小工具存在、誰看得到在伺服器（<c>AddDashboard()</c> 與 <c>DashboardWidgetCatalog</c>）；
/// 這裡只決定代號用哪個元件渲染。新增小工具時兩邊都要登記（<c>DashboardTests</c> 守門）。
/// </summary>
public static class DashboardWidgetComponents
{
    public static readonly IReadOnlyDictionary<string, Type> All = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["notifications"] = typeof(MyNotificationsWidget),
        ["account"] = typeof(MyAccountWidget),
        ["scheduled-jobs"] = typeof(ScheduledJobsWidget),
    };

    /// <summary>不認得的代號回 null（伺服器較新、前端還是舊版時不顯示內容，不讓首頁壞掉）。</summary>
    public static Type? Resolve(string id) => All.GetValueOrDefault(id);
}

/// <summary>首頁小工具的共同基底：第一次讀不到資料時交給 <see cref="DashboardGrid"/> 顯示錯誤（附錯誤追蹤碼）。</summary>
public abstract class DashboardWidgetBase : ComponentBase
{
    /// <summary>讀取失敗；參數是 API 回應的錯誤追蹤碼（可能是 null）。</summary>
    [Parameter]
    public EventCallback<string?> OnFailed { get; set; }

    protected Task FailAsync<T>(ApiResult<T> result) => OnFailed.InvokeAsync(result.TraceId);
}
