using System.Text;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 右下角通知「複製」按鈕放進剪貼簿的文字：使用者回報問題時一次貼出標題、內文、追蹤碼、時間與頁面，
/// 管理員拿追蹤碼就能在 /logs 找到同一筆紀錄。
///
/// 追蹤碼獨立一行，所以傳進來的必須是**原始內文**（UnexpectedError 畫面上的「（錯誤追蹤碼：…）」後綴不要帶進來），
/// 否則同一個碼會出現兩次。
/// </summary>
internal static class ToastCopyText
{
    public static string Build(string title, string description, string? traceCode, DateTime occurredAt, string? pageUrl)
    {
        var text = new StringBuilder()
            .AppendLine(title)
            .AppendLine(description);

        if (!string.IsNullOrEmpty(traceCode))
        {
            text.AppendLine($"錯誤追蹤碼：{traceCode}");
        }

        text.AppendLine($"時間：{occurredAt:yyyy/MM/dd HH:mm:ss}");

        if (!string.IsNullOrEmpty(pageUrl))
        {
            text.AppendLine($"頁面：{pageUrl}");
        }

        return text.ToString().TrimEnd();
    }
}
