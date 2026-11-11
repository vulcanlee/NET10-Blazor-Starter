using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 外框的通知鈴鐺（0.10.1 起，WASM 遷移 M2）。標記與 Server 版 <c>NotificationBell</c> 相同；
/// 資料來自 <c>/api/v1/notifications</c>（只看得到自己的），每 60 秒與切回分頁時更新；0.10.7 起另由推播串流（<see cref="ClientPushConnection"/>）即時更新，輪詢留作推播斷線或關閉時的備援。
/// 連結由伺服器先過濾成安全的站內網址；目標不是 WASM 頁面時由前端路由整頁載入。
/// </summary>
public partial class ClientNotificationBell : IAsyncDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private int unreadCount;
    private bool isOpen;
    private bool hasRead;
    private List<NotificationItemDto> items = [];
    private PeriodicRefresh? periodicRefresh;
    private bool isDisposed;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    public ILogger<ClientNotificationBell> Logger { get; set; } = default!;

    [Inject]
    public ClientNotificationSignal NotificationSignal { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        // 頁面做了會讓自己收到通知的事（例如發送測試通知）時立即更新，不等下一次輪詢（0.10.6 起）。
        NotificationSignal.RefreshRequested += OnRefreshRequestedAsync;
        await RefreshAsync();
        var started = await PeriodicRefresh.StartAsync(JSRuntime, PollInterval, () => InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        }), Logger);

        // 啟動途中元件已被移除（例如很快整頁換走）：立刻停掉，不留下沒人管的計時器。
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
            // 立即更新失敗不影響版面；下一次輪詢再試。
            Logger.LogWarning(ex, "Notification bell refresh on request failed.");
        }
    }

    /// <summary>「99+」：超過 99 則就不再顯示確切數字（與 Server 版相同）。</summary>
    internal static string BadgeText(int count) => count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task RefreshAsync()
    {
        var result = await Api.GetAsync<NotificationSummaryDto>("api/v1/notifications/summary");
        if (result.Success && result.Data is { } summary)
        {
            unreadCount = summary.UnreadCount;
            hasRead = summary.HasRead;
            items = summary.Latest;
        }
    }

    private async Task ToggleAsync()
    {
        isOpen = !isOpen;
        if (isOpen)
        {
            await RefreshAsync();
        }
    }

    private async Task OpenAsync(NotificationItemDto item)
    {
        if (!item.IsRead)
        {
            await Api.PostAsync<object>($"api/v1/notifications/{item.Id}/read");
        }

        isOpen = false;
        await RefreshAndTellOthersAsync();
        if (!string.IsNullOrWhiteSpace(item.Link))
        {
            NavigationManager.NavigateTo(item.Link);
        }
    }

    /// <summary>
    /// 在鈴鐺標已讀或刪除之後：自己重讀，並請首頁「我的通知」也立即重讀（0.10.6 起；Server 版兩邊都靠伺服器訊號即時更新，
    /// WASM 不通知的話首頁要等下一次 60 秒輪詢）。
    /// </summary>
    private async Task RefreshAndTellOthersAsync()
    {
        await RefreshAsync();
        NotificationSignal.RequestRefresh(except: OnRefreshRequestedAsync);
    }

    private async Task MarkAllReadAsync()
    {
        await Api.PostAsync<object>("api/v1/notifications/read-all");
        await RefreshAndTellOthersAsync();
    }

    private async Task DeleteAsync(NotificationItemDto item)
    {
        await Api.DeleteAsync<object>($"api/v1/notifications/{item.Id}");
        await RefreshAndTellOthersAsync();
    }

    private async Task ClearReadAsync()
    {
        await Api.DeleteAsync<object>("api/v1/notifications/read");
        await RefreshAndTellOthersAsync();
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
