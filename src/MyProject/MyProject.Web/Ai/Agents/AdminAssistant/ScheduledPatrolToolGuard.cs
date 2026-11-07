namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 定時巡檢的權限窄門（0.9.161 起，「系統排程身分＋窄門」）。
///
/// <para>
/// 排程執行時沒有登入的使用者，一般的 <see cref="AiToolPermissionGuard"/> 會拒絕所有工具。這個窄門只在一次巡檢裡由
/// <see cref="AdminPatrolRunner"/> 自己建立，<b>不註冊進 DI</b>，也不改動共用的權限檢查：
/// 只放行「不會異動資料」而且「在允許清單裡」的工具（<see cref="AdminAssistantAgent.PatrolToolNames"/>），其他一律拒絕。
/// </para>
/// 稽核轉給原本的權限檢查寫（沒有使用者，操作者自然留空），detail 註明「排程巡檢」，不冒用任何人。
/// </summary>
internal sealed class ScheduledPatrolToolGuard : IAiToolPermissionGuard
{
    /// <summary>附在工具稽核 detail 最後的註記。</summary>
    public const string AuditNote = "排程巡檢";

    private readonly IAiToolPermissionGuard inner;
    private readonly HashSet<string> allowedToolNames;

    public ScheduledPatrolToolGuard(IAiToolPermissionGuard inner, IEnumerable<string> allowedToolNames)
    {
        this.inner = inner;
        this.allowedToolNames = new HashSet<string>(allowedToolNames, StringComparer.Ordinal);
    }

    public Task<bool> CanInvokeAsync(AiToolDefinition tool)
        => Task.FromResult(tool.IsMutating == false && allowedToolNames.Contains(tool.Name));

    public Task AuditAsync(string action, string agentName, AiToolDefinition tool, bool success, string? note = null)
        => inner.AuditAsync(action, agentName, tool, success, string.IsNullOrWhiteSpace(note) ? AuditNote : $"{note}; {AuditNote}");
}
