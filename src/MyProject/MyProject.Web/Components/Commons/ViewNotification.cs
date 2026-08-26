using AntDesign;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 檢視層的通知樣板。
///
/// 五個 CRUD 檢視原本各自重複 9～11 個結構完全相同的 <see cref="NotificationConfig"/> 區塊
/// （共 48 個），只有 Description 與 NotificationType 不同。
/// 收斂到這裡之後，新模組不需要再靠複製貼上取得一致的通知外觀，
/// 也不會出現「某個檢視忘了設 Placement」這種只在該頁看得出來的漂移。
/// </summary>
public static class ViewNotification
{
    private const string SystemTitle = "系統訊息";
    private const string ValidationTitle = "驗證失敗";

    /// <summary>
    /// 一般系統訊息。注意：本專案的「新增成功／修改成功／刪除成功」等訊息
    /// **沿用 Warning 型別**（既有外觀，非筆誤），因此刻意不提供 Success 多載，
    /// 以免有人不小心改掉既有的視覺表現。
    /// </summary>
    public static void Warning(NotificationService notificationService, string description)
        => Open(notificationService, SystemTitle, description, NotificationType.Warning);

    public static void Error(NotificationService notificationService, string description)
        => Open(notificationService, SystemTitle, description, NotificationType.Error);

    /// <summary>
    /// 表單驗證失敗。標題與一般系統訊息區隔，讓使用者知道是自己的輸入有問題；
    /// 停留時間也拉長到 5 秒（驗證訊息通常較長、需要時間讀完）。
    /// </summary>
    public static void ValidationError(NotificationService notificationService, string description)
        => Open(notificationService, ValidationTitle, description, NotificationType.Error, durationSeconds: 5);

    private static void Open(
        NotificationService notificationService,
        string message,
        string description,
        NotificationType notificationType,
        double? durationSeconds = null)
    {
        var config = new NotificationConfig
        {
            Message = message,
            Description = description,
            NotificationType = notificationType,
            Placement = NotificationPlacement.BottomRight
        };

        if (durationSeconds.HasValue)
        {
            config.Duration = durationSeconds.Value;
        }

        _ = notificationService.Open(config);
    }
}
