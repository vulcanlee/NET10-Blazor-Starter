using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Admins;

/// <summary>系統參數的值型別（0.9.98 起；0.10.6 起從 Web 搬到這裡，WASM 頁面與伺服器共用）。</summary>
public enum SystemParameterKind
{
    Int,
    Bool,
    String,
}

/// <summary>系統參數目前生效的值來自哪裡（0.10.6 起從 Web 搬到這裡）。</summary>
public enum SystemParameterSource
{
    /// <summary>沒有覆寫，用設定檔（appsettings／環境變數）的值。</summary>
    File,

    /// <summary>用「系統參數」頁設定的值。</summary>
    Override,

    /// <summary>資料庫裡有覆寫，但不合法而沒有套用，目前用設定檔的值。</summary>
    NotApplied,
}

/// <summary>
/// 格式化與解析一個系統參數的值需要的定義（0.10.6 起）：伺服器的 <c>SystemParameterDefinition</c> 與畫面拿到的
/// <see cref="SystemParameterDefinitionDto"/> 都實作它，兩邊共用同一份 <c>SystemParameterValueCodec</c>（顯示、範圍文字、縮短保留判斷）。
/// </summary>
public interface ISystemParameterFormat
{
    SystemParameterKind Kind { get; }

    string? Unit { get; }

    string? ZeroMeaning { get; }

    int? Min { get; }

    int? Max { get; }

    int? MaxLength { get; }

    IReadOnlyList<string>? AllowedValues { get; }

    string? BlankMeaning { get; }

    bool Required { get; }
}

/// <summary>系統參數頁一列的定義（0.10.6 起，WASM 遷移 M7）。由伺服器的參數目錄（<c>SystemParameterCatalog</c>）帶來，畫面只顯示。</summary>
public class SystemParameterDefinitionDto : ISystemParameterFormat
{
    /// <summary>完整設定鍵，例如 <c>LogRetentionSettings:AuditLogDays</c>。</summary>
    public string Key { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>修改後什麼時候生效。</summary>
    public string EffectNote { get; set; } = string.Empty;

    public SystemParameterKind Kind { get; set; }

    public string? Unit { get; set; }

    public string? ZeroMeaning { get; set; }

    public int? Min { get; set; }

    public int? Max { get; set; }

    public int? MaxLength { get; set; }

    /// <summary>限定選項的文字參數（下拉選單）；null 代表不限定。</summary>
    public List<string>? AllowedValues { get; set; }

    public string? BlankMeaning { get; set; }

    public bool Required { get; set; }

    /// <summary>調小這個值會讓排程永久刪除更多資料：存檔前畫面要危險確認。</summary>
    public bool IsRetention { get; set; }

    IReadOnlyList<string>? ISystemParameterFormat.AllowedValues => AllowedValues;
}

/// <summary>
/// 系統參數頁的一列（<c>GET api/v1/system-parameters</c>）。值都是設定讀到的原始字串，畫面以 <c>SystemParameterValueCodec.Display</c> 顯示。
/// </summary>
public class SystemParameterItemDto
{
    public SystemParameterDefinitionDto Definition { get; set; } = new();

    /// <summary>目前生效的值。</summary>
    public string? EffectiveValue { get; set; }

    /// <summary>設定檔這一層的值。</summary>
    public string? BaseValue { get; set; }

    /// <summary>資料庫裡的覆寫；沒有覆寫時為 null。</summary>
    public SystemParameterOverrideDto? Override { get; set; }

    public SystemParameterSource Source { get; set; }

    /// <summary>未套用的原因。</summary>
    public string? Problem { get; set; }
}

/// <summary>資料庫裡的一筆覆寫。<see cref="UpdatedAt"/> 是伺服器的本地時間（不帶時差送出）。</summary>
public class SystemParameterOverrideDto
{
    /// <summary>開修改窗時記下，存檔與還原時送回；別人先改過就回 409。</summary>
    public string ConcurrencyStamp { get; set; } = string.Empty;

    public string? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>資料庫裡不在目錄的鍵（不會套用），讓管理員移除。</summary>
public class SystemParameterOrphanDto
{
    public string ParameterKey { get; set; } = string.Empty;

    public string ConcurrencyStamp { get; set; } = string.Empty;
}

/// <summary>設定檔本身有誤的區段與錯誤。</summary>
public class SystemParameterSectionErrorDto
{
    public string Section { get; set; } = string.Empty;

    public List<string> Errors { get; set; } = [];
}

/// <summary>系統參數頁的全部內容（依目錄順序；畫面依 <see cref="SystemParameterDefinitionDto.Group"/> 分組）。</summary>
public class SystemParameterOverviewDto
{
    public List<SystemParameterItemDto> Items { get; set; } = [];

    public List<SystemParameterOrphanDto> Orphans { get; set; } = [];

    public List<SystemParameterSectionErrorDto> BaseInvalid { get; set; } = [];
}

/// <summary>
/// 修改一個參數（<c>POST api/v1/system-parameters/save</c>）。驗證（型別、範圍、長度、允許空白、所有設定驗證器）一律在伺服器做。
/// <see cref="ExpectedStamp"/> 是開窗時覆寫的版本號，開窗時沒有覆寫就送 null。
/// </summary>
public class SystemParameterSaveRequestDto
{
    [Required(ErrorMessage = "請指定參數。")]
    [MaxLength(200, ErrorMessage = "參數名稱過長。")]
    public string Key { get; set; } = string.Empty;

    [MaxLength(4000, ErrorMessage = "值過長。")]
    public string? Value { get; set; }

    [MaxLength(64, ErrorMessage = "版本號不正確。")]
    public string? ExpectedStamp { get; set; }
}

/// <summary>還原為設定檔的值，或移除不認得的參數（<c>POST api/v1/system-parameters/reset</c>）。</summary>
public class SystemParameterResetRequestDto
{
    [Required(ErrorMessage = "請指定參數。")]
    [MaxLength(200, ErrorMessage = "參數名稱過長。")]
    public string Key { get; set; } = string.Empty;

    [Required(ErrorMessage = "版本號不正確。")]
    [MaxLength(64, ErrorMessage = "版本號不正確。")]
    public string ExpectedStamp { get; set; } = string.Empty;
}
