using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyProject.Business.Helpers;

/// <summary>
/// 寫成檔案或給人看的 JSON 共用的序列化設定（0.10.44 起，整併自三處逐字相同的寫法）。
/// </summary>
public static class JsonFileOptions
{
    /// <summary>
    /// 縮排、中文不跳脫：AI 對話紀錄的內容檔、系統巡檢報告的內容檔、AI 對話紀錄頁的「格式化 JSON」。
    /// ⚠️ 必須用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>：預設編碼器會把中文跳脫成 \uXXXX，
    /// 檔案膨脹數倍且無法直接閱讀。這些內容不會以 HTML 輸出，所以放寬跳脫是安全的。
    /// </summary>
    public static readonly JsonSerializerOptions IndentedRelaxed = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
