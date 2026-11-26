namespace MyProject.Dtos.Shell;

// WASM 前端外框（版面、側邊選單、通知、公告、操作說明）用的 DTO（0.10.1 起，WASM 遷移 M2）。
// 時間欄位一律已換成伺服器的本地時間（*Local），前端照字面顯示 —— 與 Server 版畫面用 TimeProvider 換算的結果一致，
// 不依賴瀏覽器的時區。

/// <summary>系統資訊與說明主題清單（<c>GET /api/v1/shell/info</c>）。</summary>
public class ShellInfoDto
{
    public string SystemName { get; set; } = string.Empty;

    public string? SystemDescription { get; set; }

    public string SystemVersion { get; set; } = string.Empty;

    public string EnvironmentName { get; set; } = string.Empty;

    public DateTime StartedAtLocal { get; set; }

    /// <summary>每個頁面的操作說明主題（路由、標題）；選單外的頁面（例如個人資料）靠它決定頁面標題。</summary>
    public List<HelpTopicDto> HelpTopics { get; set; } = [];

    /// <summary>
    /// 是否開啟通知推播串流（0.10.7 起，<c>NotificationSettings:PushEnabled</c>）。關閉時外框不連 <c>/api/v1/notifications/stream</c>，
    /// 通知鈴鐺與公告只靠 60 秒輪詢。
    /// </summary>
    public bool NotificationPushEnabled { get; set; }
}

/// <summary>
/// 伺服器（zh-TW）的日期時間格式（0.10.2 起，<c>GET /api/v1/shell/culture</c>，不需登入）：
/// 瀏覽器內建的地區資料與伺服器不同（例如「上午9:00:00」與「上午 09:00:00」），WASM 啟動前先套用這一組，
/// 清單上的時間才與 Server 頁面一模一樣。只有 <see cref="System.Globalization.DateTimeFormatInfo"/> 的幾個欄位。
/// </summary>
public class DateTimeFormatDto
{
    public string ShortDatePattern { get; set; } = string.Empty;

    public string LongDatePattern { get; set; } = string.Empty;

    public string ShortTimePattern { get; set; } = string.Empty;

    public string LongTimePattern { get; set; } = string.Empty;

    public string AMDesignator { get; set; } = string.Empty;

    public string PMDesignator { get; set; } = string.Empty;
}

public class HelpTopicDto
{
    public string Route { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

/// <summary>側邊選單的一個項目（已依目前使用者的權限過濾）。</summary>
public class MenuItemDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public List<MenuItemDto> SubMenu { get; set; } = [];
}

/// <summary><c>GET /api/v1/shell/menu</c>：目前使用者看得到的選單，以及全部選單網址（操作說明的「相關頁面」用來判斷要不要顯示）。</summary>
public class ShellMenuDto
{
    public List<MenuItemDto> Items { get; set; } = [];

    public List<string> AllMenuUrls { get; set; } = [];
}

/// <summary><c>GET /api/v1/shell/help?route=…</c>：操作說明，Markdown 已在伺服器端以安全管線轉成 HTML。</summary>
public class HelpDocumentDto
{
    public string Title { get; set; } = string.Empty;

    public string PreambleHtml { get; set; } = string.Empty;

    public List<HelpSectionDto> Sections { get; set; } = [];

    /// <summary>「相關頁面」：已過濾掉目前使用者沒有權限的頁面。</summary>
    public List<HelpRelatedPageDto> RelatedPages { get; set; } = [];

    /// <summary>「相關頁面」段落開頭的說明文字（HTML）。</summary>
    public string? RelatedPagesIntroHtml { get; set; }
}

public class HelpSectionDto
{
    public string Heading { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;
}

public class HelpRelatedPageDto
{
    public string Title { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

/// <summary>前端換頁時寫一筆開頁稽核（<c>POST /api/v1/shell/page-view</c>）。</summary>
public class PageViewRequestDto
{
    public string Path { get; set; } = string.Empty;

    /// <summary>從哪一頁來（<c>entry</c>＝這次載入的第一頁）。</summary>
    public string From { get; set; } = string.Empty;
}

/// <summary>
/// 瀏覽器端的回報批次（0.10.36 起，<c>POST /api/v1/shell/client-reports</c>）：前端把錯誤與事件先存在瀏覽器（localStorage），
/// 登入後批次送出；登入前發生的、離線時送不出去的，都留到下一次送。
/// </summary>
public class ClientReportBatchDto
{
    public List<ClientReportDto> Items { get; set; } = [];

    /// <summary>瀏覽器暫存區滿了而捨棄的筆數（累計到這次送出為止）。</summary>
    public int Dropped { get; set; }
}

/// <summary>
/// 一筆瀏覽器端的回報。<see cref="Kind"/> 決定等級：
/// <c>error</c>／<c>unhandledrejection</c>／<c>boundary</c>／<c>unhandled</c>／<c>caught</c>／<c>sw-install</c>／<c>boot</c> 是錯誤（Error，進系統例外紀錄）；
/// <c>resource</c>（資源載入失敗）、<c>csp</c>（安全政策擋下）是 Warning；<c>network</c>（一段連線中斷）依 <see cref="BrowserOnline"/> 是 Warning 或 Information。
/// 前端送來的一律不信任：伺服器再截一次長度、去掉查詢字串。
/// </summary>
public class ClientReportDto
{
    /// <summary>瀏覽器產生的識別碼，伺服器回 <see cref="ClientReportBatchResultDto.DoneIds"/> 時用它對應。</summary>
    public string? Id { get; set; }

    public string? Kind { get; set; }

    public string? Message { get; set; }

    public string? Stack { get; set; }

    /// <summary>錯誤來源（JS 檔案與行號、.NET 例外型別、出問題的資源網址、被擋下的網址…）。</summary>
    public string? Source { get; set; }

    /// <summary>發生時的頁面路徑。</summary>
    public string? Path { get; set; }

    /// <summary>瀏覽器產生的 8 碼錯誤追蹤碼（畫面上已經顯示給使用者）；格式不對時伺服器改用自己的。</summary>
    public string? TraceCode { get; set; }

    /// <summary>瀏覽器端的發生時間（UTC）。</summary>
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>前端程式的指紋（前端主程式的模組識別碼前 8 碼；前端沒有改版就不變）。</summary>
    public string? AppBuild { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>發生時已登入的使用者 Id；沒有（登入前發生）或與送出時的使用者不同，就標記為「登入前」。</summary>
    public int? SignedInUserId { get; set; }

    /// <summary>發生前最近的操作軌跡（換頁、API 呼叫），只有錯誤才帶。</summary>
    public List<ClientBreadcrumbDto>? Breadcrumbs { get; set; }

    /// <summary>連線中斷（<c>network</c>）：第一次失敗的時間。</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>連線中斷：最後一次失敗的時間。</summary>
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>連線中斷：恢復連線（第一次成功）的時間。</summary>
    public DateTimeOffset? RecoveredAt { get; set; }

    /// <summary>連線中斷期間失敗的 API 呼叫次數。</summary>
    public int? FailedCalls { get; set; }

    /// <summary>連線中斷期間失敗的 API 路徑（不含查詢字串）。</summary>
    public List<string>? Paths { get; set; }

    /// <summary>連線中斷期間瀏覽器是否一直認為自己有網路（true＝伺服器連不上；false＝使用者端離線）。</summary>
    public bool? BrowserOnline { get; set; }
}

/// <summary>操作軌跡的一筆：換頁（<c>nav</c>）或 API 呼叫（<c>api</c>）。只有路徑，不含查詢字串與輸入內容。</summary>
public class ClientBreadcrumbDto
{
    public DateTimeOffset At { get; set; }

    public string? Kind { get; set; }

    public string? Method { get; set; }

    public string? Path { get; set; }

    /// <summary>HTTP 狀態碼；0＝連不上伺服器。</summary>
    public int? Status { get; set; }

    public long? ElapsedMs { get; set; }

    /// <summary>伺服器回的追蹤碼（<c>X-Trace-Code</c> 標頭），可到 /logs 查同一次請求。</summary>
    public string? TraceCode { get; set; }
}

/// <summary>回報批次的結果：<see cref="DoneIds"/> 是已處理（記錄了或確定不記）的項目，前端從暫存區移除；其餘下次再送。</summary>
public class ClientReportBatchResultDto
{
    public List<string> DoneIds { get; set; } = [];

    /// <summary>有項目被限流時，幾秒後再送。</summary>
    public int? RetryAfterSeconds { get; set; }
}

/// <summary>
/// 前端診斷記錄的日誌批次（0.10.37 起，<c>POST /api/v1/shell/diagnostic-logs</c>）：管理員對這位使用者開啟診斷時，
/// 前端把自己的日誌（達到門檻等級的）批次送來，伺服器依原等級寫進日誌檔。
/// </summary>
public class ClientLogBatchDto
{
    public List<ClientLogEntryDto> Items { get; set; } = [];

    /// <summary>前端緩衝區滿了而捨棄的筆數。</summary>
    public int Dropped { get; set; }
}

/// <summary>前端的一筆日誌。不帶例外物件，只帶例外型別名稱（不會進系統例外紀錄）。</summary>
public class ClientLogEntryDto
{
    /// <summary><c>Trace</c>、<c>Debug</c>、<c>Information</c>、<c>Warning</c>、<c>Error</c>、<c>Critical</c>。</summary>
    public string? Level { get; set; }

    /// <summary>前端的日誌分類（例如 <c>MyProject.Web.Client.Api.ApiClient</c>）。</summary>
    public string? Category { get; set; }

    /// <summary>格式化後的訊息。</summary>
    public string? Message { get; set; }

    public string? ExceptionType { get; set; }

    /// <summary>發生時的頁面路徑。</summary>
    public string? Path { get; set; }

    public DateTimeOffset? ClientTime { get; set; }
}

/// <summary>前端診斷記錄的結果：<see cref="Active"/> 為 false 表示這位使用者的診斷已經關閉（或到期），前端停止送。</summary>
public class ClientLogBatchResultDto
{
    public bool Active { get; set; }
}
