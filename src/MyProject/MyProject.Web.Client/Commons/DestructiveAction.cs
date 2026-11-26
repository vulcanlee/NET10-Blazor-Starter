using AntDesign;
using MyProject.Dtos.Commons;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 日誌類頁面的「批次清除／清空全部」（0.10.45 起，整併自 AI 對話紀錄、Token 用量、例外紀錄、稽核紀錄四頁相同的寫法）：
/// 破壞性確認（<see cref="ConfirmDialog.AskDestructiveAsync"/>）→ 呼叫伺服器（伺服器清除並寫稽核）→
/// 成功顯示 <c>successMessage</c> 算出的那一句、失敗顯示伺服器的原因。回傳 true 表示已清除，呼叫端接著重新讀取畫面。
/// </summary>
public static class DestructiveAction
{
    public static async Task<bool> RunAsync<T>(
        ModalService modalService,
        NotificationService notifications,
        string title,
        string content,
        string okText,
        Func<Task<ApiResult<T>>> call,
        Func<ApiResult<T>, string> successMessage)
    {
        if (!await ConfirmDialog.AskDestructiveAsync(modalService, title, content, okText))
        {
            return false;
        }

        var result = await call();
        if (!result.Success)
        {
            ViewNotification.Error(notifications, result.UserMessage());
            return false;
        }

        ViewNotification.Warning(notifications, successMessage(result));
        return true;
    }

    /// <summary>伺服器回的訊息（例如「已清除 N 筆紀錄。」）；空白時改顯示 <c>fallback</c>。</summary>
    public static string ServerMessageOr<T>(ApiResult<T> result, string fallback)
        => string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message;
}
