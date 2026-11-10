using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 外框的公告橫幅（0.10.1 起，WASM 遷移 M2）。標記與 Server 版 <c>AnnouncementBanner</c> 相同；
/// 要顯示哪些公告（時間、對象、沒被關閉）由伺服器以同一套規則挑好，每 60 秒與切回分頁時重新取得。
/// </summary>
public partial class ClientAnnouncementBanner : IAsyncDisposable
{
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    private List<ActiveAnnouncementDto> visible = [];
    private readonly HashSet<int> dismissedHere = [];
    private PeriodicRefresh? periodicRefresh;
    private bool isDisposed;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    public ILogger<ClientAnnouncementBanner> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
        var started = await PeriodicRefresh.StartAsync(JSRuntime, RecheckInterval, () => InvokeAsync(async () =>
        {
            await RefreshAsync();
            StateHasChanged();
        }), Logger);

        // 啟動途中元件已被移除（例如很快換到 Server 頁面）：立刻停掉，不留下沒人管的計時器。
        if (isDisposed)
        {
            await started.DisposeAsync();
            return;
        }

        periodicRefresh = started;
    }

    private async Task RefreshAsync()
    {
        var result = await Api.GetAsync<List<ActiveAnnouncementDto>>("api/v1/announcements/active");
        if (result.Success && result.Data is { } items)
        {
            visible = items.Where(x => !dismissedHere.Contains(x.Id)).ToList();
        }
    }

    private async Task DismissAsync(ActiveAnnouncementDto item)
    {
        dismissedHere.Add(item.Id);
        visible.Remove(item);
        var result = await Api.PostAsync<object>($"api/v1/announcements/{item.Id}/dismiss");
        if (!result.Success)
        {
            // 沒存進去只是下次載入還會再看到一次。
            Logger.LogWarning("Failed to save announcement dismissal. AnnouncementId={AnnouncementId}", item.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        isDisposed = true;
        if (periodicRefresh is not null)
        {
            await periodicRefresh.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
