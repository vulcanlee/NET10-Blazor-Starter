using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Layout;

namespace MyProject.Web.Client.Views.Dashboard;

/// <summary>
/// 首頁「我的通知」（0.9.106 起；0.10.6 起在 WASM 執行）：未讀數與最新 5 則（<c>GET api/v1/dashboard/notifications</c>）。
/// 與外框的通知鈴鐺相同每 60 秒與切回分頁時更新；頁面請鈴鐺立即更新（<see cref="ClientNotificationSignal"/>）時一併更新。
/// ⚠️ Dispose 時一定要取消訂閱與停掉定期更新。全部通知與「全部標為已讀」在右上角的鈴鐺。
/// </summary>
public partial class MyNotificationsWidget
{
    private bool loaded;
    private bool isDisposed;
    private int unreadCount;
    private List<DashboardNotificationItemDto> items = [];
    private PeriodicRefresh? periodicRefresh;

    [Inject]
    public DashboardApi Api { get; set; } = default!;

    [Inject]
    public ClientNotificationSignal NotificationSignal { get; set; } = default!;

    [Inject]
    public IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    public ILogger<MyNotificationsWidget> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        var result = await Api.NotificationsAsync();
        if (!result.Success || result.Data is not { } data)
        {
            await FailAsync(result);
            return;
        }

        // 第一次讀取還沒回來就換頁：元件已移除，不要再訂閱（否則這個元件會一直被訊號抓著、每次都多打一次 API；0.10.6 獨立審查）。
        if (isDisposed)
        {
            return;
        }

        Apply(data);
        NotificationSignal.RefreshRequested += OnRefreshRequestedAsync;
        var started = await PeriodicRefresh.StartAsync(JSRuntime, ClientNotificationBell.PollInterval, () => InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        }), Logger);

        // 啟動途中元件已被移除：立刻停掉，不留下沒人管的計時器。
        if (isDisposed)
        {
            await started.DisposeAsync();
            return;
        }

        periodicRefresh = started;
    }

    private async Task OnRefreshRequestedAsync()
    {
        try
        {
            await InvokeAsync(async () =>
            {
                await RefreshAsync();
                StateHasChanged();
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Notification widget refresh on request failed.");
        }
    }

    /// <summary>定期更新用：讀不到不該讓首頁壞掉，下一次再試。</summary>
    private async Task RefreshAsync()
    {
        var result = await Api.NotificationsAsync();
        if (result.Success && result.Data is { } data)
        {
            Apply(data);
        }
        else
        {
            Logger.LogWarning("Failed to refresh the notification widget. StatusCode={StatusCode}", result.StatusCode);
        }
    }

    private void Apply(DashboardNotificationsDto data)
    {
        unreadCount = data.UnreadCount;
        items = data.Items;
        loaded = true;
    }

    private async Task MarkReadAsync(DashboardNotificationItemDto item)
    {
        if (!item.IsRead)
        {
            await Api.MarkNotificationReadAsync(item.Id);
            NotificationSignal.RequestRefresh();
        }
    }

    public async ValueTask DisposeAsync()
    {
        isDisposed = true;
        NotificationSignal.RefreshRequested -= OnRefreshRequestedAsync;
        if (periodicRefresh is not null)
        {
            await periodicRefresh.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
