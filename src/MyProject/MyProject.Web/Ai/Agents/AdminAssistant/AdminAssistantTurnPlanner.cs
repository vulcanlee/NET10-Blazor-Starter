using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理的每輪規劃器（0.9.155 起）：每則訊息先用一次便宜的模型呼叫判斷領域（記在「系統管理助理（分派）」），
/// 再組出這一輪的定義與提示詞。分派失敗或讀不出代碼時退回「全部領域的說明＋唯讀工具」，這一輪照常進行。
/// </summary>
public sealed class AdminAssistantTurnPlanner : IAiAgentTurnPlanner
{
    /// <summary>分派呼叫的逾時（0.9.157 起）。分派只回幾個字，等太久不如直接退回全部領域。</summary>
    public static readonly TimeSpan RouterTimeout = TimeSpan.FromSeconds(20);

    private readonly IAiChatCompletionClient client;
    private readonly IAiSystemPromptProvider prompts;
    private readonly IOptionsMonitor<AiModelSettings> models;
    private readonly ILogger<AdminAssistantTurnPlanner> logger;

    public AdminAssistantTurnPlanner(
        IAiChatCompletionClient client,
        IAiSystemPromptProvider prompts,
        IOptionsMonitor<AiModelSettings> models,
        ILogger<AdminAssistantTurnPlanner> logger)
    {
        this.client = client;
        this.prompts = prompts;
        this.models = models;
        this.logger = logger;
    }

    public AiAgentDefinition Home => AdminAssistantAgent.Home;

    public async Task<AiAgentTurn> HomeTurnAsync(CancellationToken cancellationToken)
        => new(Home, await ComposeAsync(new HashSet<AdminAssistantDomain>(), cancellationToken), null);

    /// <summary>
    /// 定時巡檢這一輪（0.9.161 起）：不呼叫分派模型，直接用 <see cref="AdminAssistantAgent.ScheduledPatrol"/>（只有唯讀工具）
    /// 與巡檢提示詞（共同說明＋四個領域＋巡檢那一行）。提示詞照樣讀「AI 提示詞」頁的作用中版本。
    /// </summary>
    public async Task<AiAgentTurn> PatrolTurnAsync(CancellationToken cancellationToken)
    {
        var domains = new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Patrol };
        return new AiAgentTurn(AdminAssistantAgent.ScheduledPatrol, await ComposeAsync(domains, cancellationToken), AdminAssistantRouting.LabelOf(domains));
    }

    public async Task<AiAgentTurn> PlanAsync(
        Guid conversationId,
        string message,
        IReadOnlyList<AiAgentTranscriptEntry> transcript,
        CancellationToken cancellationToken)
    {
        var domains = await RouteAsync(conversationId, message, transcript, cancellationToken);
        return await TurnForAsync(domains, cancellationToken);
    }

    /// <summary>
    /// 重建上次分派到的那一輪（0.10.7 起，WASM 版接續停在核准的對話）：同樣的工具、提示詞與標籤，不呼叫分派模型。
    /// 讀不出代碼（空字串）就是「沒有分出領域」那一輪，與分派失敗時相同。
    /// </summary>
    public Task<AiAgentTurn> RestoreTurnAsync(string key, CancellationToken cancellationToken)
        => TurnForAsync(AdminAssistantRouting.Parse(key), cancellationToken);

    private async Task<AiAgentTurn> TurnForAsync(IReadOnlySet<AdminAssistantDomain> domains, CancellationToken cancellationToken)
    {
        var instructions = await ComposeAsync(domains, cancellationToken);
        return new AiAgentTurn(
            AdminAssistantAgent.DefinitionFor(domains), instructions, AdminAssistantRouting.LabelOf(domains), AdminAssistantRouting.KeyOf(domains));
    }

    private async Task<IReadOnlySet<AdminAssistantDomain>> RouteAsync(
        Guid conversationId, string message, IReadOnlyList<AiAgentTranscriptEntry> transcript, CancellationToken cancellationToken)
    {
        var routerPrompt = await prompts.GetAsync(PromptTemplateKeys.AdminAssistantRouter, cancellationToken);
        var result = await client.CompleteAsync(
            new AiChatCompletionRequest
            {
                Operation = TokenUsageOperations.AdminAssistantRouter,
                Messages =
                [
                    new AiChatMessage(AiChatRoles.System, routerPrompt),
                    new AiChatMessage(AiChatRoles.User, AdminAssistantRouting.BuildRouterInput(message, transcript)),
                ],
                ConversationId = conversationId,
                ModelKey = string.IsNullOrWhiteSpace(models.CurrentValue.AdminAssistantRouter)
                    ? nameof(AiModelSettings.AdminAssistant)
                    : nameof(AiModelSettings.AdminAssistantRouter),
                RelatedInfo = TokenUsageOperations.AdminAssistantRouter,
                Timeout = RouterTimeout,
                ContextLengthExceededMessage = "對話內容太長，無法判斷領域。",
                TimeoutHint = "請稍後再試。",
                SubmittedContentLabel = "對話內容",
            },
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Success == false)
        {
            logger.LogWarning(
                "Admin assistant routing failed; using all domains. ConversationId={ConversationId}, Reason={Reason}",
                conversationId, result.Reason);
            return new HashSet<AdminAssistantDomain>();
        }

        var domains = AdminAssistantRouting.Parse(result.Markdown);
        if (domains.Count == 0)
        {
            logger.LogWarning("Admin assistant routing returned no domain code; using all domains. ConversationId={ConversationId}", conversationId);
        }

        return domains;
    }

    /// <summary>共同說明＋領域說明，最後接上一次固定規則。提示詞每輪重讀：管理員改了版本，下一則訊息就生效。</summary>
    private async Task<string> ComposeAsync(IReadOnlySet<AdminAssistantDomain> domains, CancellationToken cancellationToken)
    {
        var baseText = await prompts.GetInstructionsAsync(PromptTemplateKeys.AdminAssistant, cancellationToken);
        var domainTexts = new Dictionary<AdminAssistantDomain, string>();
        foreach (var (domain, key) in AdminAssistantAgent.DomainPromptKeys)
        {
            domainTexts[domain] = await prompts.GetInstructionsAsync(key, cancellationToken);
        }

        return AiPromptGuardrails.Compose(
            PromptTemplateKeys.AdminAssistant,
            AdminAssistantAgent.ComposeInstructions(baseText, domainTexts, domains));
    }
}
