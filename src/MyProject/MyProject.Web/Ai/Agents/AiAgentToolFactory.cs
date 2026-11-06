using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 把 <see cref="AiToolDefinition"/> 變成交給 agent 的工具（0.9.142 起）：每個工具都包上權限檢查與稽核，
/// 會異動資料的再包上 <see cref="ApprovalRequiredAIFunction"/>（執行前要使用者核准）。
///
/// ⚠️ 不要把 <see cref="AIFunctionFactory"/> 建出來的函式直接交給 agent，那會繞過權限與稽核。
/// </summary>
public sealed class AiAgentToolFactory
{
    private readonly IServiceProvider services;
    private readonly IAiToolPermissionGuard guard;

    public AiAgentToolFactory(IServiceProvider services, IAiToolPermissionGuard guard)
    {
        this.services = services;
        this.guard = guard;
    }

    public IList<AITool> Create(AiAgentDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var tools = new List<AITool>(agent.Tools.Count);
        foreach (var definition in agent.Tools)
        {
            AIFunction function = new PermissionCheckedAIFunction(definition.Create(services), definition, agent.Name, guard);
            if (definition.IsMutating)
            {
                function = new ApprovalRequiredAIFunction(function);
            }

            tools.Add(function);
        }

        return tools;
    }

    /// <summary>預覽最多等多久（0.9.160 起）：只是給人看的說明，不能讓核准窗遲遲不出現。</summary>
    public static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 算核准窗的「目前狀態」（<see cref="AiToolDefinition.ApprovalPreview"/>，0.9.160 起）。
    /// 沒有預覽、逾時或丟例外時回 null（記警告）—— 預覽失敗不能擋住核准。
    /// </summary>
    public async Task<string?> PreviewAsync(AiToolDefinition tool, FunctionCallContent call)
    {
        if (tool.ApprovalPreview is null)
        {
            return null;
        }

        try
        {
            using var timeout = new CancellationTokenSource(PreviewTimeout);
            return await tool.ApprovalPreview(services, call.Arguments?.AsReadOnly() ?? new Dictionary<string, object?>().AsReadOnly(), timeout.Token);
        }
        catch (Exception ex)
        {
            services.GetService<ILogger<AiAgentToolFactory>>()?.LogWarning(ex, "Approval preview failed. Tool={Tool}", tool.Name);
            return null;
        }
    }
}

/// <summary>工具的權限檢查與稽核（0.9.142 起）。執行器在核准之前也用它先檢查，沒權限就不必請使用者核准。</summary>
public interface IAiToolPermissionGuard
{
    /// <summary>目前登入的使用者是否可以執行這個工具（未登入一律不行；管理員一律可以）。</summary>
    Task<bool> CanInvokeAsync(AiToolDefinition tool);

    /// <summary>寫一筆工具稽核。detail 只記 agent、工具與結果，<b>不記參數與回傳內容</b>（那些只進 AI 對話紀錄）。</summary>
    Task AuditAsync(string action, string agentName, AiToolDefinition tool, bool success, string? note = null);
}

/// <inheritdoc cref="IAiToolPermissionGuard" />
public sealed class AiToolPermissionGuard : IAiToolPermissionGuard
{
    private readonly IPermissionChecker permissionChecker;
    private readonly CurrentUserService currentUserService;
    private readonly IAuditLogService auditLogService;

    public AiToolPermissionGuard(
        IPermissionChecker permissionChecker,
        CurrentUserService currentUserService,
        IAuditLogService auditLogService)
    {
        this.permissionChecker = permissionChecker;
        this.currentUserService = currentUserService;
        this.auditLogService = auditLogService;
    }

    public async Task<bool> CanInvokeAsync(AiToolDefinition tool)
    {
        var user = currentUserService.CurrentUser;
        if (user.Id <= 0)
        {
            return false;
        }

        return await permissionChecker.HasPermissionAsync(user.Id, PermissionKey.For(tool.PermissionPage, tool.PermissionAction));
    }

    public Task AuditAsync(string action, string agentName, AiToolDefinition tool, bool success, string? note = null)
    {
        var user = currentUserService.CurrentUser;
        var detail = $"agent={agentName}; tool={tool.Name}（{tool.DisplayName}）; permission={PermissionKey.For(tool.PermissionPage, tool.PermissionAction)}";
        if (string.IsNullOrWhiteSpace(note) == false)
        {
            detail += $"; {note}";
        }

        return auditLogService.WriteAsync(
            action,
            success: success,
            actorUserId: user.Id > 0 ? user.Id : null,
            actorAccount: user.Id > 0 ? user.Account : null,
            targetType: "AiTool",
            targetId: tool.Name,
            detail: detail);
    }
}

/// <summary>
/// 執行前檢查權限、執行後寫稽核的工具外殼（0.9.142 起）。
///
/// 沒有權限時<b>不執行</b>，改回一段說明給模型（讓它告訴使用者需要申請權限），並寫 <c>Ai.ToolDenied</c>。
/// 工具丟例外時同樣回一段說明而不是讓整次執行失敗，並寫失敗的 <c>Ai.ToolInvoked</c>。
/// </summary>
internal sealed class PermissionCheckedAIFunction : DelegatingAIFunction
{
    /// <summary>沒有權限時回給模型的文字開頭。測試與執行器以它辨認。</summary>
    public const string DeniedPrefix = "使用者沒有執行這個操作的權限";

    private readonly AiToolDefinition definition;
    private readonly string agentName;
    private readonly IAiToolPermissionGuard guard;

    public PermissionCheckedAIFunction(
        AIFunction innerFunction,
        AiToolDefinition definition,
        string agentName,
        IAiToolPermissionGuard guard)
        : base(innerFunction)
    {
        this.definition = definition;
        this.agentName = agentName;
        this.guard = guard;
    }

    public override string Name => definition.Name;

    public override string Description => definition.Description;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (await guard.CanInvokeAsync(definition) == false)
        {
            await guard.AuditAsync(AuditActions.Ai.ToolDenied, agentName, definition, success: false);
            return $"{DeniedPrefix}（需要「{definition.PermissionPage}」的 {definition.PermissionAction} 權限），沒有執行。"
                + "請告訴使用者若需要這個操作，請向管理員申請權限。";
        }

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            await guard.AuditAsync(AuditActions.Ai.ToolInvoked, agentName, definition, success: true);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await guard.AuditAsync(AuditActions.Ai.ToolInvoked, agentName, definition, success: false, note: $"error={ex.GetType().Name}");
            return $"工具「{definition.DisplayName}」執行失敗（{ex.GetType().Name}），沒有完成。請告訴使用者稍後再試。";
        }
    }
}
