using System.Text.Json;

namespace MyProject.Tests;

/// <summary>
/// 直接讀出貨的 <c>appsettings.json</c> 原文時共用的解析選項（0.9.176 起）。
///
/// 0.9.176 起 appsettings.json 每個屬性都有 <c>//</c> 註解（網站執行期的設定讀取本來就略過註解），
/// 但 <see cref="JsonDocument.Parse(string, JsonDocumentOptions)"/> 的預設選項遇到註解會丟例外。
/// 直接讀檔的測試一律帶這組選項。
/// </summary>
public static class ShippedAppSettings
{
    public static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
