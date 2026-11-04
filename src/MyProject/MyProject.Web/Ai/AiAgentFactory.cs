using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

// OPENAI001：OpenAI .NET SDK 2.14 把 Responses API（GetResponsesClient、CreateResponseOptions）標為評估用。
// Agent 功能必須走 Responses（Azure gpt-6.1-sol 在 Chat Completions 上不能帶工具，0.9.149），所以只在這個檔案豁免這一個規則碼。
// 解除條件：SDK 拿掉 Responses 的 [Experimental] 標記後移除本行。
#pragma warning disable OPENAI001

namespace MyProject.Web.Ai;

/// <summary>
/// 建立 Microsoft Agent Framework（MAF）agent 的<b>唯一入口</b>（0.9.140 起）。
///
/// <para>
/// ⚠️ 系統內任何 LLM 呼叫都必須經由這裡建立的 agent 送出：記帳、AI 對話紀錄、用量上限、逾時與錯誤對應
/// 都掛在這裡組出的管線上。自己 <c>new OpenAIClient</c> 或另建 <c>IChatClient</c> 就等於繞過記帳（速查表 §6.7）。
/// </para>
/// </summary>
public interface IAiAgentFactory
{
    /// <summary>
    /// 依目前的 <see cref="AiSettings"/> 建立一個 agent。每個作業（每次按鈕、每輪追問）各建一個，用完即棄。
    /// 呼叫前必須先以 <see cref="AiChatEndpoint.Validate"/> 確認設定完整。
    /// </summary>
    /// <param name="context">呼叫情境：作業名稱、逾時、錯誤訊息用語等。</param>
    /// <param name="instructions">agent 的系統提示詞；呼叫端自己在訊息裡帶 system 訊息時傳 null。</param>
    /// <param name="tools">
    /// agent 可呼叫的工具（0.9.142 起）。由 MAF 的工具呼叫迴圈執行，每一次模型呼叫都經過記帳中介層。
    /// ⚠️ 請用 <c>AiAgentToolFactory</c> 建立，權限檢查、稽核與異動工具的強制核准都在那裡包上。
    /// </param>
    AiAgentHandle CreateAgent(AiCallContext context, string? instructions = null, IList<AITool>? tools = null);
}

/// <summary>建好的 agent 與它的呼叫狀態（每次成功的模型呼叫結果都在 <see cref="State"/>）。</summary>
public sealed record AiAgentHandle(AIAgent Agent, AiCallState State, AiSettings Settings);

/// <summary>記帳中介層需要的服務，打包成一份以免建構子參數爆量。</summary>
internal sealed record AiAgentServices(
    ILogger Logger,
    ITokenUsageRecorder TokenUsageRecorder,
    CurrentUserService CurrentUserService,
    IAiCallLogRecorder AiCallLogRecorder,
    IAiQuotaService QuotaService,
    IAuditLogService AuditLogService,
    IOptionsMonitor<SlowOperationSettings>? SlowOptions);

/// <inheritdoc cref="IAiAgentFactory" />
public sealed class AiAgentFactory : IAiAgentFactory
{
    /// <summary>
    /// SDK 送 HTTP 用的具名 client。為什麼用 named client 而非 typed client，
    /// 見 ServiceCollectionExtensions.AddApplicationServices 的註解。
    /// </summary>
    public const string HttpClientName = "AiChatCompletions";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly IOptionsMonitor<AiModelSettings>? modelOptions;
    private readonly ILoggerFactory loggerFactory;
    private readonly AiAgentServices services;

    public AiAgentFactory(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ILoggerFactory loggerFactory,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService,
        IAiCallLogRecorder aiCallLogRecorder,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService,
        IOptionsMonitor<SlowOperationSettings>? slowOptions = null,
        IOptionsMonitor<AiModelSettings>? modelOptions = null)
    {
        this.modelOptions = modelOptions;
        this.httpClientFactory = httpClientFactory;
        this.optionsMonitor = optionsMonitor;
        this.loggerFactory = loggerFactory;
        services = new AiAgentServices(
            loggerFactory.CreateLogger<AiAccountingChatClient>(),
            tokenUsageRecorder,
            currentUserService,
            aiCallLogRecorder,
            quotaService,
            auditLogService,
            slowOptions);
    }

    public AiAgentHandle CreateAgent(AiCallContext context, string? instructions = null, IList<AITool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ⚠️ 在呼叫時才讀設定（CurrentValue），不在建構時快取：Blazor Server 的 DI scope
        // 等於 SignalR circuit，可存活數小時，IOptions 的一次性快照會讓設定變更永遠吃不到。
        var settings = optionsMonitor.CurrentValue;
        var state = new AiCallState();
        // 各功能可在系統參數指定自己的模型（0.9.142 起）；沒設定就用 AiSettings.Model。
        var model = modelOptions?.CurrentValue.Resolve(context.ModelKey, settings.Model) ?? settings.Model;

        var openAIClient = CreateOpenAIClient(settings, state);
        var agentOptions = new ChatClientAgentOptions
        {
            ChatOptions = CreateChatOptions(settings, instructions, tools, context.UseResponsesApi),
        };
        Func<IChatClient, IChatClient> accounting = inner => new AiAccountingChatClient(inner, context, state, settings, model, services);

        // Agent 功能走 Responses API（0.9.149 起，見 AiCallContext.UseResponsesApi），其餘維持 Chat Completions。
        // 兩條路都經過同一個記帳中介層；記帳解析器同時看得懂兩種回應格式。
        var agent = context.UseResponsesApi
            ? openAIClient.GetResponsesClient().AsAIAgent(agentOptions, model, clientFactory: accounting, loggerFactory: loggerFactory)
            : openAIClient.GetChatClient(model).AsAIAgent(agentOptions, clientFactory: accounting, loggerFactory: loggerFactory);

        return new AiAgentHandle(agent, state, settings);
    }

    /// <summary>
    /// 組 OpenAI 官方 SDK 的用戶端。Azure OpenAI 也用它（指向 v1 端點），刻意不用仍是預覽版的 Azure.AI.OpenAI。
    /// </summary>
    private OpenAIClient CreateOpenAIClient(AiSettings settings, AiCallState state)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = AiChatEndpoint.GetBaseUri(settings),

            // ⚠️ SDK 預設對 408／429／5xx 自動重試 3 次。這是使用者主動觸發的單次動作，
            // 自動重試只會讓成本加倍（每次重試上游都可能計費），還會掩蓋設定錯誤。
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),

            // 逾時統一由 AiAccountingChatClient 以 CancelAfter 控管「每一次模型呼叫」，
            // SDK 自己的 100 秒預設（每次網路操作）必須關掉，否則推論模型想久一點就被它砍掉。
            NetworkTimeout = Timeout.InfiniteTimeSpan,

            // 用我們的具名 HttpClient 送出：沿用 IHttpClientFactory 的 handler 池化與 DNS 輪替，
            // 測試也能換成假的 HttpMessageHandler。
            Transport = new HttpClientPipelineTransport(httpClientFactory.CreateClient(HttpClientName)),
        };

        if (settings.GetProvider() == AiProvider.AzureOpenAI)
        {
            options.AddPolicy(new AzureApiKeyPolicy(settings.ApiKey), PipelinePosition.BeforeTransport);
        }

        options.AddPolicy(new AiHttpCapturePolicy(state), PipelinePosition.BeforeTransport);

        return new OpenAIClient(new ApiKeyCredential(settings.ApiKey), options);
    }

    private static ChatOptions CreateChatOptions(AiSettings settings, string? instructions, IList<AITool>? tools, bool useResponsesApi)
        => new()
        {
            Instructions = instructions,
            Tools = tools is { Count: > 0 } ? tools : null,

            // 送出時對應到 max_completion_tokens（推論模型只接受這個）。
            // ⚠️ 預設不送，由模型自己決定：這個額度同時涵蓋推論模型的思考 token，
            // 設太小會在產出任何可見文字之前就耗盡 —— 拿到空回應，費用照付。非正數視同沒設定。
            MaxOutputTokens = settings.MaxOutputTokens is int max && max > 0 ? max : null,

            // 推論模型不接受 temperature，所以設定為 null 時整個欄位都不送。
            Temperature = settings.Temperature is double temperature ? (float)temperature : null,

            // 推論等級只給 Agent 功能（Responses 的 reasoning.effort，0.9.149 起）；分析功能一律不送，由模型決定。
            Reasoning = useResponsesApi && ToReasoningEffort(settings.AgentReasoningEffort) is { } effort
                ? new ReasoningOptions { Effort = effort }
                : null,

            // ⚠️ Responses 一律 store=false：對話不留在雲端（企業機敏資料），歷史由 MAF session 每次整段重送。
            // 推論項目要帶加密內容回來，下一輪才送得回去（store=false 時雲端查不到 id）。
            RawRepresentationFactory = useResponsesApi ? _ => CreateResponseOptions() : null,
        };

    private static CreateResponseOptions CreateResponseOptions()
    {
        var options = new CreateResponseOptions { StoredOutputEnabled = false };
        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        return options;
    }

    private static ReasoningEffort? ToReasoningEffort(string? value)
        => value?.ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" => ReasoningEffort.ExtraHigh,
            _ => null,
        };
}
