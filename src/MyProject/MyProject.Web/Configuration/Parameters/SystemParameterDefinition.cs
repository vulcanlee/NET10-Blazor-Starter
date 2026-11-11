using MyProject.Dtos.Admins;

namespace MyProject.Web.Configuration.Parameters;

/// <summary>
/// 一個可在「系統參數」頁修改的設定鍵（0.9.98 起）。由 <see cref="SystemParameterCatalog"/> 建立，不要自己 new。
/// 值的型別 <see cref="SystemParameterKind"/> 與格式介面 <see cref="ISystemParameterFormat"/> 在 Dtos（0.10.6 起，WASM 頁面共用同一份格式化）。
/// </summary>
public sealed class SystemParameterDefinition : ISystemParameterFormat
{
    /// <summary>完整設定鍵，例如 <c>LogRetentionSettings:AuditLogDays</c>。</summary>
    public required string Key { get; init; }

    /// <summary>所屬設定區段（同一區段的鍵一起驗證）。</summary>
    public required string SectionName { get; init; }

    /// <summary>區段之下的屬性路徑，例如 <c>SystemInformation:SystemName</c>。</summary>
    public required string PropertyPath { get; init; }

    public required Type OptionsType { get; init; }

    public required string Group { get; init; }

    public required string Label { get; init; }

    public required string Description { get; init; }

    /// <summary>修改後什麼時候生效（顯示在頁面上）。</summary>
    public required string EffectNote { get; init; }

    public required SystemParameterKind Kind { get; init; }

    public string? Unit { get; init; }

    /// <summary>0 有特別意義時的說明，例如「不自動清除」。</summary>
    public string? ZeroMeaning { get; init; }

    public int? Min { get; init; }

    public int? Max { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>
    /// 限定選項的文字參數（0.9.168 起，例如推論等級）：只接受空白或清單裡的值（不分大小寫，存成清單裡的寫法），
    /// 頁面用下拉選單編輯。null 代表不限定。
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>限定選項時，空白那一項在下拉選單上的說明，例如「沿用全域設定」。</summary>
    public string? BlankMeaning { get; init; }

    public bool Required { get; init; }

    /// <summary>調小這個值會讓排程永久刪除更多資料：存檔前要危險確認。</summary>
    public bool IsRetention { get; init; }

    /// <summary>以真的 options 管線（含所有已註冊的驗證器）驗證一份候選設定區段，回傳錯誤訊息。</summary>
    internal Func<IServiceProvider, IConfiguration, IReadOnlyList<string>> ValidateSection { get; init; } = (_, _) => [];
}
