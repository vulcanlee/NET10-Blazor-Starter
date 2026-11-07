using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>定時巡檢的執行器（0.9.161 起）：排程作業「系統管理助理巡檢」用它跑一次巡檢、拿回報告全文。</summary>
public interface IAdminPatrolRunner
{
    /// <summary>AI 設定不完整時的中文原因；可用時為 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 跑一次巡檢。失敗（逾時、超過用量上限、上游錯誤…）回 <c>Success == false</c> 的結果，不丟例外；取消時丟 <see cref="OperationCanceledException"/>。
    /// ⚠️ 只能在沒有登入使用者的 scope（排程作業）呼叫，否則丟 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task<AiAnalysisResult> RunAsync(Guid reportId, CancellationToken cancellationToken);
}

/// <summary>
/// 定時巡檢的執行器（0.9.161 起）。刻意<b>不重用</b> <see cref="AiAgentRunner"/>：
/// <list type="bullet">
/// <item>它每次都會存對話，沒有使用者時只會記一筆警告；巡檢報告另外存（<c>AdminPatrolReportService</c>）。</item>
/// <item>它的權限檢查來自 DI，沒有使用者時拒絕所有工具；這裡改用只放行唯讀工具的 <see cref="ScheduledPatrolToolGuard"/>。</item>
/// <item>它每輪都先做一次分派模型呼叫；巡檢直接用 <see cref="AdminAssistantTurnPlanner.PatrolTurnAsync"/>。</item>
/// </list>
/// 記帳、AI 對話紀錄與用量上限一樣走 <see cref="IAiAgentFactory"/> 的管線（同 <c>AiWorkflowRunner</c>）。
/// ConversationId 是報告編號，可以從「AI 對話紀錄」追到這次巡檢的每一次模型呼叫。
/// </summary>
public sealed class AdminPatrolRunner : IAdminPatrolRunner
{
    /// <summary>交給 agent 的那一則訊息。</summary>
    public const string PatrolMessage = "請做一次每日例行巡檢，依巡檢報告格式產出完整報告。這是排程自動執行，沒有人會即時回覆，請不要提問。";

    private readonly IAiAgentFactory agentFactory;
    private readonly IServiceProvider services;
    private readonly IAiToolPermissionGuard guard;
    private readonly AdminAssistantTurnPlanner planner;
    private readonly IOptionsMonitor<AiSettings> settingsMonitor;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<AdminPatrolRunner> logger;

    public AdminPatrolRunner(
        IAiAgentFactory agentFactory,
        IServiceProvider services,
        IAiToolPermissionGuard guard,
        AdminAssistantTurnPlanner planner,
        IOptionsMonitor<AiSettings> settingsMonitor,
        CurrentUserService currentUserService,
        ILogger<AdminPatrolRunner> logger)
    {
        this.agentFactory = agentFactory;
        this.services = services;
        this.guard = guard;
        this.planner = planner;
        this.settingsMonitor = settingsMonitor;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    public string? UnavailableReason => AiChatEndpoint.Validate(settingsMonitor.CurrentValue);

    public async Task<AiAnalysisResult> RunAsync(Guid reportId, CancellationToken cancellationToken)
    {
        // 窄門只給排程用：有人登入的 scope（頁面）呼叫進來，等於讓那個人繞過自己的權限。
        if (currentUserService.CurrentUser.Id > 0)
        {
            throw new InvalidOperationException("定時巡檢只能由排程作業執行（目前的 scope 有登入的使用者）。");
        }

        var turn = await planner.PatrolTurnAsync(cancellationToken);
        return await RunCoreAsync(turn, AdminAssistantAgent.PatrolToolNames, reportId, cancellationToken);
    }

    /// <summary>跑一輪（測試可以換成範例工具）。工具只經 <see cref="ScheduledPatrolToolGuard"/> 放行。</summary>
    internal async Task<AiAnalysisResult> RunCoreAsync(
        AiAgentTurn turn, IEnumerable<string> allowedToolNames, Guid reportId, CancellationToken cancellationToken)
    {
        var definition = turn.Definition;
        var settings = settingsMonitor.CurrentValue;
        var timeoutSeconds = definition.TimeoutSeconds ?? (settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120);
        var tools = new AiAgentToolFactory(services, new ScheduledPatrolToolGuard(guard, allowedToolNames)).Create(definition);
        var handle = agentFactory.CreateAgent(
            new AiCallContext
            {
                Operation = definition.Operation,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                RelatedInfo = definition.Operation,
                ConversationId = reportId,
                RunId = Guid.NewGuid(),
                ModelKey = definition.ModelKey,

                // Agent 一律走 Responses API（同 AiAgentRunner）。
                UseResponsesApi = true,
                ContextLengthExceededMessage = "巡檢內容超過模型的內容視窗上限。",
                TimeoutHint = "請稍後在「排程作業」頁按「立即執行」重試。",
                SubmittedContentLabel = "巡檢內容",
            },
            turn.Instructions,
            tools);

        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        try
        {
            var session = await handle.Agent.CreateSessionAsync(cancellationToken);
            await foreach (var update in handle.Agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, PatrolMessage)], session, cancellationToken: cancellationToken))
            {
                // 巡檢只有唯讀工具，不會要求核准；真的出現就是定義出錯，不要停在那裡等不會來的人。
                if (update.Contents.OfType<ToolApprovalRequestContent>().Any())
                {
                    logger.LogError("Admin patrol received a tool approval request. ReportId={ReportId}", reportId);
                    return AiAnalysisResult.Failure(
                        AiAnalysisFailureReason.Unexpected, "巡檢時出現需要核准的工具呼叫，已停止。", elapsed: stopwatch.Elapsed);
                }

                text.Append(update.Text);
            }
        }
        catch (AiCallException ex)
        {
            // 已在記帳中介層記過帳、寫過日誌。
            return ex.ToResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin patrol run failed unexpectedly. ReportId={ReportId}", reportId);
            return AiAnalysisResult.Failure(
                AiAnalysisFailureReason.Unexpected, $"巡檢失敗：{ex.GetType().Name}。", elapsed: stopwatch.Elapsed, detail: ex.ToString());
        }

        var outcomes = handle.State.Outcomes;
        return new AiAnalysisResult
        {
            Success = true,
            Markdown = text.ToString(),
            Usage = AiTokenUsage.Sum(outcomes.Select(outcome => outcome.Usage)),
            ModelName = outcomes.LastOrDefault()?.ModelName ?? settings.Model,
            IsTruncatedByLength = string.Equals(outcomes.LastOrDefault()?.FinishReason, "length", StringComparison.OrdinalIgnoreCase),
            Elapsed = stopwatch.Elapsed,
        };
    }
}
