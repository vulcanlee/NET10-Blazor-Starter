namespace MyProject.Models.Systems;

public class SystemSettings
{
    public ConnectionStrings ConnectionStrings { get; set; } = new();
    public SystemInformation SystemInformation { get; set; } = new();
    public ExternalFileSystem ExternalFileSystem { get; set; } = new();
    public UploadSettings Upload { get; set; } = new();
}

public class UploadSettings
{
    /// <summary>
    /// 允許上傳的副檔名白名單（例如 ".pdf"）。
    /// 留空表示採用 <c>UploadFileTypePolicy</c> 的內建預設清單。
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [];
}

public class ConnectionStrings
{
    public string SQLiteDefaultConnection { get; set; } = string.Empty;

}
public class SystemInformation
{
    public string SystemVersion { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public string SystemDescription { get; set; } = string.Empty;
}
public class ExternalFileSystem
{
    public string DatabasePath { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string UploadPath { get; set; } = string.Empty;
    public string ProjectFilePath { get; set; } = string.Empty;

    /// <summary>
    /// 系統例外紀錄的堆疊檔案存放目錄。每一種例外只在首次發生時寫一個檔，
    /// 檔案生命週期一律經由 ExceptionStackFileStore 處理。
    /// </summary>
    public string ExceptionPath { get; set; } = string.Empty;

    /// <summary>
    /// Token 用量紀錄的原始 usage JSON 存放目錄。每次 LLM 呼叫一個檔，
    /// 檔案生命週期一律經由 TokenUsageRawStore 處理。
    /// ⚠️ 只存 usage 結構，絕不存提示詞或模型回應內文。
    /// </summary>
    public string TokenUsagePath { get; set; } = string.Empty;
}

public class BootstrapSettings
{
    public string SupportAccount { get; set; } = "support";
    public string SupportName { get; set; } = "support";
    public string SupportEmail { get; set; } = "support";
    public string SupportPassword { get; set; } = "support";
}
