using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// 0.9.140 起 AI 呼叫改走 MAF agent（<see cref="AiAgentFactory"/>），記帳與 HTTP 都在 agent 管線上。
/// 這裡的工廠方法沿用 0.9.139 以前 <c>AiChatCompletionClient</c>／<c>AiHealthProbe</c> 建構子的參數順序，
/// 讓既有測試只換掉建立方式、斷言一行都不必改 —— 這正是「換底層、行為不變」的證明。
/// </summary>
internal static class AiTestAgents
{
    public static AiAgentFactory CreateFactory(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService,
        IAiCallLogRecorder aiCallLogRecorder,
        IAiQuotaService? quotaService = null,
        IAuditLogService? auditLogService = null,
        IOptionsMonitor<AiReasoningSettings>? reasoningOptions = null)
        => new(
            httpClientFactory,
            optionsMonitor,
            NullLoggerFactory.Instance,
            tokenUsageRecorder,
            currentUserService,
            aiCallLogRecorder,
            quotaService ?? new StubAiQuotaService(),
            auditLogService ?? new RecordingAuditLogService(),
            reasoningOptions: reasoningOptions);

    public static AiChatCompletionClient CreateChatClient(
        ILogger<AiChatCompletionClient> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService,
        IAiCallLogRecorder aiCallLogRecorder,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService)
        => new(
            logger,
            optionsMonitor,
            CreateFactory(
                httpClientFactory,
                optionsMonitor,
                tokenUsageRecorder,
                currentUserService,
                aiCallLogRecorder,
                quotaService,
                auditLogService));

    public static AiHealthProbe CreateProbe(
        ILogger<AiHealthProbe> logger,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> optionsMonitor,
        ITokenUsageRecorder tokenUsageRecorder,
        CurrentUserService currentUserService,
        IAiCallLogRecorder aiCallLogRecorder)
        => new(
            logger,
            optionsMonitor,
            CreateFactory(httpClientFactory, optionsMonitor, tokenUsageRecorder, currentUserService, aiCallLogRecorder));
}
