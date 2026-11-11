namespace MyProject.Web.Diagnostics;

/// <summary>
/// 錯誤追蹤碼在畫面上的寫法（0.10.2 起，WASM 遷移 M3）：伺服器 API 錯誤訊息用的 <c>TraceCode.Suffix</c> 與 WASM 頁面的
/// <c>ViewNotification</c> 共用同一個格式。追蹤碼本身怎麼產生見 <c>MyProject.Web.Diagnostics.TraceCode</c>。
/// </summary>
public static class TraceCodeText
{
    /// <summary>附在錯誤訊息後面的文字；沒有追蹤碼時回傳空字串。</summary>
    public static string Suffix(string? code)
        => string.IsNullOrEmpty(code) ? string.Empty : $"（錯誤追蹤碼：{code}）";
}
