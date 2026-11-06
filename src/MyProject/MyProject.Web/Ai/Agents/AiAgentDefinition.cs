using Microsoft.Extensions.AI;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 一個 Agent 的定義（0.9.142 起）。Agent 一律寫在 C# 程式碼裡（不在畫面上設定），提示詞沿用「AI 提示詞」頁管理。
///
/// 新增一個 Agent 要做的事見 <c>docs/architecture/MAF-AI呼叫架構.md</c>「新增 Agent」：
/// 作業名稱登記在 <c>TokenUsageOperations</c>、提示詞種類登記在 <c>PromptTemplateKeys</c>（與 <see cref="AiPromptGuardrails"/>）、
/// 模型設定加在 <c>AiModelSettings</c>（與系統參數目錄）、工具用 <see cref="AiToolDefinition"/> 宣告。
/// </summary>
public sealed record AiAgentDefinition
{
    /// <summary>程式內的識別名稱（英數），也是稽核紀錄的 agent 欄位。</summary>
    public required string Name { get; init; }

    /// <summary>畫面上顯示的名稱。</summary>
    public required string DisplayName { get; init; }

    /// <summary>Token 用量的作業名稱（<c>TokenUsageOperations</c> 的常數）。</summary>
    public required string Operation { get; init; }

    /// <summary>系統提示詞的種類（<c>PromptTemplateKeys</c> 的常數）。</summary>
    public required string PromptTemplateKey { get; init; }

    /// <summary>模型設定（<c>nameof(AiModelSettings.屬性)</c>）；null 用預設模型。</summary>
    public string? ModelKey { get; init; }

    /// <summary>可呼叫的工具。</summary>
    public IReadOnlyList<AiToolDefinition> Tools { get; init; } = [];

    /// <summary>
    /// 單次模型呼叫的逾時秒數；null 用 <c>AiSettings.TimeoutSeconds</c>。
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// 一個 Agent 工具的宣告（0.9.142 起）。
///
/// <para>
/// ⚠️ 權限是<b>必填</b>：工具以「目前登入的使用者」的身分執行，使用者在畫面上做不到的事，Agent 也做不到。
/// <see cref="AiAgentToolFactory"/> 會在執行前檢查 <see cref="PermissionPage"/>＋<see cref="PermissionAction"/>，
/// 並寫稽核；<see cref="IsMutating"/> 的工具一律包成「執行前要使用者核准」。
/// </para>
/// <para>
/// 資料層的範圍限制（例如專案的團隊範圍）不在這裡處理 —— 工具應呼叫既有的服務方法，讓那些規則照常生效。
/// </para>
/// </summary>
public sealed record AiToolDefinition
{
    /// <summary>給模型看的函式名稱（英文 snake_case）。</summary>
    public required string Name { get; init; }

    /// <summary>畫面上與稽核紀錄顯示的名稱。</summary>
    public required string DisplayName { get; init; }

    /// <summary>給模型看的說明：做什麼、什麼時候該用。</summary>
    public required string Description { get; init; }

    /// <summary>需要的頁面權限（<c>MagicObjectHelper.角色_*</c>）。</summary>
    public required string PermissionPage { get; init; }

    /// <summary>需要的動作（<c>PermissionActions.View／Create／Edit／Delete</c>）。</summary>
    public required string PermissionAction { get; init; }

    /// <summary>會新增、修改或刪除資料。為 true 時一律在執行前請使用者核准。</summary>
    public bool IsMutating { get; init; }

    /// <summary>
    /// 核准窗上給人看的補充說明（0.9.158 起，選填），例如「停用後對方會被登出」「會依保留份數刪除最舊的備份」。
    /// 只在 <see cref="IsMutating"/> 的工具上有意義。
    /// </summary>
    public string? ApprovalNote { get; init; }

    /// <summary>
    /// 不可復原的動作（0.9.159 起，例如徹底刪除、清空紀錄）：核准窗加紅字警告、紅色核准鈕，並要求輸入「確認」才能核准。
    /// 只在 <see cref="IsMutating"/> 的工具上有意義。
    /// </summary>
    public bool IsIrreversible { get; init; }

    /// <summary>
    /// 建立實際執行的函式（每段對話建一次，從當下的 scope 取服務）。
    /// 通常是 <c>AIFunctionFactory.Create(sp.GetRequiredService&lt;某工具類別&gt;().方法, Name, Description)</c>。
    /// </summary>
    public required Func<IServiceProvider, AIFunction> Create { get; init; }
}
