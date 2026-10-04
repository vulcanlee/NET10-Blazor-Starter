using Microsoft.AspNetCore.Components;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Web.Components.Auths;

namespace MyProject.Web.Components.Views.Dashboard;

/// <summary>
/// 首頁「我的通知」（0.9.106 起）：未讀數與最新 5 則。同一個行程發的通知經 <see cref="INotificationSignal"/> 即時更新；
/// ⚠️ Dispose 時一定要取消訂閱（訊號是 singleton）。全部通知與「全部標為已讀」在右上角的鈴鐺。
/// </summary>
public partial class MyNotificationsWidget : IDisposable
{
    internal const int ListSize = 5;

    private IDisposable? subscription;
    private bool loaded;
    private int userId;
    private int unreadCount;
    private List<NotificationAdapterModel> items = [];

    [Inject]
    public NotificationQueryService QueryService { get; set; } = default!;

    [Inject]
    public INotificationSignal Signal { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    public TimeProvider TimeProvider { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        userId = CurrentUserService.CurrentUser.Id;
        subscription = Signal.Subscribe(userId, () => InvokeAsync(async () =>
        {
            await LoadAsync();
            StateHasChanged();
        }));
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        unreadCount = await QueryService.GetUnreadCountAsync(userId);
        items = await QueryService.GetLatestAsync(userId, ListSize);
        loaded = true;
    }

    private async Task MarkReadAsync(NotificationAdapterModel item)
    {
        if (item.ReadAtUtc is null)
        {
            await QueryService.MarkReadAsync(userId, item.Id);
        }
    }

    /// <summary>只導向站內網址（與鈴鐺相同的規則）。</summary>
    private static string? SafeLink(string? link)
        => string.IsNullOrWhiteSpace(link) || ReturnUrlGuard.Sanitize(link) != link ? null : link;

    private string ToLocalText(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeProvider.LocalTimeZone).ToString("MM-dd HH:mm");

    public void Dispose()
    {
        subscription?.Dispose();
    }
}
