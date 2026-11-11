namespace MyProject.Dtos.Admins;

/// <summary>
/// 系統備份、資料保留頁上顯示的排程作業狀態（0.10.6 起，WASM 遷移 M7）：只帶畫面用到的欄位。
/// 時間是伺服器的本地時間、不帶時差（WASM 不會換成瀏覽器的時區）。
/// </summary>
public class MaintenanceJobDto
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    /// <summary>下次執行；停用或總開關關閉時為 null。</summary>
    public DateTime? NextRunLocal { get; set; }

    /// <summary>「立即執行」已在佇列或執行中。</summary>
    public bool IsQueued { get; set; }

    public MaintenanceJobRunDto? LastRun { get; set; }
}

/// <summary>最近一次執行（可能還在執行中）。</summary>
public class MaintenanceJobRunDto
{
    /// <summary>Running、Succeeded、Failed、Interrupted、Skipped（<c>JobRunStatuses</c>）。</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedAtLocal { get; set; }

    public string? Message { get; set; }
}

/// <summary>系統備份頁（<c>GET api/v1/backups</c>）：備份清單（新到舊）、備份作業狀態與保留份數。</summary>
public class BackupListDto
{
    public List<BackupFileDto> Items { get; set; } = [];

    /// <summary>備份作業；找不到時為 null（畫面不顯示摘要）。</summary>
    public MaintenanceJobDto? Job { get; set; }

    /// <summary>保留份數；0＝不自動刪除。</summary>
    public int KeepCount { get; set; }
}

/// <summary>一份備份檔。</summary>
public class BackupFileDto
{
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime CreatedAtLocal { get; set; }

    /// <summary>備份說明檔（manifest.json）；讀不到時為 null。</summary>
    public BackupManifestDto? Manifest { get; set; }
}

/// <summary>備份說明檔裡畫面用到的欄位（不含資料庫雜湊與 migration）。</summary>
public class BackupManifestDto
{
    public string SystemVersion { get; set; } = string.Empty;

    /// <summary>Schedule、CatchUp、Manual。</summary>
    public string Trigger { get; set; } = string.Empty;

    public List<BackupManifestFolderDto> Folders { get; set; } = [];

    public int MissingFileCount { get; set; }
}

public class BackupManifestFolderDto
{
    /// <summary><c>ExternalFileSystem</c> 的設定鍵名，例如 <c>ProjectFilePath</c>。</summary>
    public string SettingKey { get; set; } = string.Empty;

    public int FileCount { get; set; }
}

/// <summary>資料保留與清理頁（<c>GET api/v1/data-retention</c>）。</summary>
public class DataRetentionOverviewDto
{
    /// <summary>排程總開關（<c>ScheduledJobSettings:Enabled</c>）。</summary>
    public bool SchedulingEnabled { get; set; }

    /// <summary>執行中設定被改壞時的驗證訊息；正常為 null。</summary>
    public string? SettingsError { get; set; }

    /// <summary>伺服器的「今天」（本地日期）：自訂清除窗的預設日期由它往前推，不用瀏覽器的日期。</summary>
    public DateTime TodayLocal { get; set; }

    public List<DataRetentionRowDto> Rows { get; set; } = [];
}

/// <summary>一種資料。值為 null 的欄位在畫面上顯示「—」（不適用）。</summary>
public class DataRetentionRowDto
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Note { get; set; }

    public int? Count { get; set; }

    public string CountUnit { get; set; } = "筆";

    public DataRetentionFolderDto? Folder { get; set; }

    public DateTime? OldestLocal { get; set; }

    public string RuleText { get; set; } = string.Empty;

    public string? ParameterGroup { get; set; }

    public int? Expected { get; set; }

    public string? ExpectedNote { get; set; }

    public string? JobName { get; set; }

    public MaintenanceJobDto? Job { get; set; }

    public string ScheduleText { get; set; } = "—";

    public bool CanPurgeNow { get; set; }

    public string? PurgeDisabledReason { get; set; }

    public string? ManageUrl { get; set; }

    public bool CanCustomPurge { get; set; }

    public string? Error { get; set; }
}

/// <summary>資料夾大小。</summary>
public class DataRetentionFolderDto
{
    /// <summary>NotConfigured、Missing、Ok、Partial（Partial＝逾時或途中出錯，<see cref="Bytes"/> 是「至少」）。</summary>
    public string Status { get; set; } = string.Empty;

    public long Bytes { get; set; }
}

/// <summary>「自訂清除」的條件：二選一 —— 只留最近 <see cref="KeepDays"/> 天，或刪除 <see cref="BeforeDate"/> 這一天之前（不含當天）。</summary>
public class CustomPurgeRequestDto
{
    public int? KeepDays { get; set; }

    public DateOnly? BeforeDate { get; set; }
}

/// <summary>「自訂清除」的結果：刪掉幾筆、留下幾筆（受保護、仍在使用或中途被還原）、途中是否有一筆失敗。</summary>
public class CustomPurgeResultDto
{
    public int Removed { get; set; }

    public int Left { get; set; }

    public bool Failed { get; set; }
}

/// <summary>資料庫用量頁（<c>GET api/v1/database-usage</c>）。不帶資料庫的完整路徑。</summary>
public class DatabaseUsageDto
{
    public string DatabaseFileName { get; set; } = string.Empty;

    public long MainDbBytes { get; set; }

    public long WalBytes { get; set; }

    public long ShmBytes { get; set; }

    public long TotalOnDiskBytes { get; set; }

    public bool WalFileExists { get; set; }

    public bool ShmFileExists { get; set; }

    public long PageCount { get; set; }

    public long PageSize { get; set; }

    public long AllocatedBytes { get; set; }

    public long FreelistCount { get; set; }

    public long ReclaimableBytes { get; set; }

    public List<DatabaseTableUsageDto> Tables { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public string Message { get; set; } = string.Empty;

    /// <summary>致命錯誤訊息；有值時畫面只顯示此訊息。</summary>
    public string ErrorMessage { get; set; } = string.Empty;
}

public class DatabaseTableUsageDto
{
    public string TableName { get; set; } = string.Empty;

    public long? RowCount { get; set; }

    public long? EstimatedBytes { get; set; }

    public int IndexCount { get; set; }

    public string Note { get; set; } = string.Empty;
}
