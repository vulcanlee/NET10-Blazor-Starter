using MyProject.Models.AdapterModel;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 「異動歷程」對話窗的資料來源（0.10.2 起，WASM 遷移 M3）：對話窗只有一份（Server 與 WASM 頁面共用），
/// Server 頁面直接查資料庫（<c>ServerAuditHistorySource</c>），WASM 頁面走管理員專用的 API（<c>ApiAuditHistorySource</c>）。
/// 兩邊都在伺服器端寫稽核：打開時一筆「搜尋」、點一筆時一筆「查看」。
/// </summary>
public interface IAuditHistorySource
{
    /// <summary>
    /// 一頁異動（新的在前、時間為伺服器本地時間）。<paramref name="opening"/> 為 true 時是剛打開對話窗，寫一筆搜尋稽核（換頁不寫）。
    /// 讀不到時丟 <see cref="AuditHistoryUnavailableException"/>（訊息直接給使用者看）；其他例外是未預期的錯誤。
    /// </summary>
    Task<AuditHistoryPage> LoadAsync(string targetType, string targetId, int pageIndex, int pageSize, bool opening);

    /// <summary>點一筆的詳細內容（修改前後差異）；讀不到時回說明文字。寫一筆查看稽核。</summary>
    Task<string> ReadContentAsync(AuditLogAdapterModel item);
}

/// <summary>一頁異動歷程。</summary>
public sealed record AuditHistoryPage(IReadOnlyList<AuditLogAdapterModel> Items, int Total);

/// <summary>讀不到異動歷程（例如 API 回錯誤或連不上伺服器）：訊息直接顯示給使用者。</summary>
public sealed class AuditHistoryUnavailableException(string message) : Exception(message);
