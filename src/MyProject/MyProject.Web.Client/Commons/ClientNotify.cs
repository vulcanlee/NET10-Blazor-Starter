using AntDesign;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// WASM 頁面右下角的提示（0.10.1 起）。外觀與 Server 版 <c>ViewNotification</c> 相同（標題「系統訊息」、右下角；錯誤不自動消失）。
/// Server 版錯誤提示上的「複製」按鈕（含追蹤碼）在 WASM 版還沒有：API 錯誤的追蹤碼已記在伺服器日誌。
/// </summary>
public static class ClientNotify
{
    private const string SystemTitle = "系統訊息";

    public static void Warning(NotificationService notificationService, string description)
        => _ = notificationService.Open(new NotificationConfig
        {
            Message = SystemTitle,
            Description = description,
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
        });

    public static void Error(NotificationService notificationService, string description)
        => _ = notificationService.Open(new NotificationConfig
        {
            Message = SystemTitle,
            Description = description,
            NotificationType = NotificationType.Error,
            Placement = NotificationPlacement.BottomRight,
            Duration = 0,
        });
}
