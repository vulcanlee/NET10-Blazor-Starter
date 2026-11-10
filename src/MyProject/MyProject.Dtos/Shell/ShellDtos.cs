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

/// <summary>瀏覽器端的錯誤回報（<c>POST /api/v1/shell/client-errors</c>），規則同 Server 版的 BrowserErrorReporter。</summary>
public class ClientErrorReportDto
{
    public string? Kind { get; set; }

    public string? Message { get; set; }

    public string? Stack { get; set; }

    public string? Source { get; set; }

    public string? Path { get; set; }
}
