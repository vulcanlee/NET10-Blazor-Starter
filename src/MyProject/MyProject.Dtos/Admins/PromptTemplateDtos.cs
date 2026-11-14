namespace MyProject.Dtos.Admins;

/// <summary>
/// AI 提示詞頁的一種範本（0.10.5 起，WASM 遷移 M6；<c>GET api/v1/prompt-templates</c> 依頁籤順序回傳全部種類）。
/// 內建預設與固定附加的規則只在伺服器的程式裡，由這裡帶給畫面顯示（不能修改）。
/// </summary>
public class PromptTemplateDto
{
    /// <summary>範本鍵（例：<c>LogAnalysis</c>）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>顯示名稱（例：「日誌分析」「管理助理／分派」；畫面依「／」分組）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>程式內建的分析指示（沒有作用中的版本時使用）。</summary>
    public string DefaultInstructions { get; set; } = string.Empty;

    /// <summary>送出時固定接在分析指示後面的規則。</summary>
    public string LockedRules { get; set; } = string.Empty;

    /// <summary>所有版本，新到舊。</summary>
    public List<PromptTemplateVersionDto> Versions { get; set; } = [];
}

/// <summary>一個版本。建立時間是伺服器的本地時間（不帶時差送出）。</summary>
public class PromptTemplateVersionDto
{
    public int Version { get; set; }

    public string Content { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public string? Note { get; set; }

    public string? CreatedByAccount { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 存成新版本（<c>POST api/v1/prompt-templates/{key}/versions</c>）。
/// <see cref="ExpectedActiveVersion"/> 是開啟編輯窗時的作用中版本（內建預設為 null），與目前不同就回 409、不儲存。
/// 建立者一律是目前登入的帳號（伺服器取）。
/// </summary>
public class PromptTemplateSaveRequestDto
{
    public string? Content { get; set; }

    public string? Note { get; set; }

    public int? ExpectedActiveVersion { get; set; }
}

/// <summary>存檔結果：新的版本號。</summary>
public class PromptTemplateSaveResultDto
{
    public int Version { get; set; }
}

/// <summary>切換作用中的版本（<c>POST api/v1/prompt-templates/{key}/activate</c>）；<see cref="Version"/> 為 null 代表改用內建預設。</summary>
public class PromptTemplateActivateRequestDto
{
    public int? Version { get; set; }
}
