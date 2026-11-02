using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai;

/// <inheritdoc cref="IAiHealthProbe" />
public sealed class AiHealthProbe : IAiHealthProbe
{
    /// <summary>
    /// 探測用的逾時。⚠️ 刻意<b>不</b>沿用 <c>AiSettings.TimeoutSeconds</c>（預設 600 秒）——
    /// 那是給「使用者主動按下分析、願意等」的情境用的。健康監控頁是自動載入，
    /// 端點一旦沒回應就會把整個管理頁卡住十分鐘。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>探測提示詞。刻意極短，把每次探測的成本壓到幾十個 token。</summary>
    private const string ProbeSystemPrompt = "You are a health probe. Reply with a single word.";
    private const string ProbeUserMessage = "hello";

    private readonly ILogger<AiHealthProbe> logger;
    private readonly IOptionsMonitor<AiSettings> optionsMonitor;
    private readonly IAiAgentFactory agentFactory;

    public AiHealthProbe(
        ILogger<AiHealthProbe> logger,
        IOptionsMonitor<AiSettings> optionsMonitor,
        IAiAgentFactory agentFactory)
    {
        this.logger = logger;
        this.optionsMonitor = optionsMonitor;
        this.agentFactory = agentFactory;
    }

    public async Task<AiHealthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var settings = optionsMonitor.CurrentValue;

        // 一定要走 Validate：它會接住 provider 打錯字的例外並翻成中文訊息。
        // 直接呼叫 settings.GetProvider() 會讓一個錯字炸掉整份健康報告。
        var invalid = AiChatEndpoint.Validate(settings);
        if (invalid is not null)
        {
            // 未設定不發請求、不記帳 —— 與 AiLogAnalysisService 的 NotConfigured 一致。
            return AiHealthProbeResult.NotConfigured(invalid);
        }

        var endpointHost = AiChatEndpoint.GetBaseUri(settings).Host;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 記帳與 AI 對話紀錄由 agent 管線上的記帳中介層處理（作業「系統健康檢測」）。
            // 自己的 30 秒逾時也在那裡；呼叫端的取消仍然有效。
            var handle = agentFactory.CreateAgent(new AiCallContext
            {
                Operation = TokenUsageOperations.SystemHealthCheck,
                Timeout = ProbeTimeout,
                RelatedInfo = "系統健康監控 AI 連線探測",

                // 探測是系統自發、成本固定極低的呼叫；被用量上限擋下會讓健康頁誤報 AI 服務故障。
                EnforceQuota = false,
            });

            await handle.Agent.RunAsync(
                [new ChatMessage(ChatRole.System, ProbeSystemPrompt), new ChatMessage(ChatRole.User, ProbeUserMessage)],
                cancellationToken: cancellationToken);

            stopwatch.Stop();
            var outcome = handle.State.LastOutcome;
            logger.LogInformation(
                "AI health probe completed. HasContent={HasContent}, ElapsedMs={ElapsedMs}",
                true,
                stopwatch.ElapsedMilliseconds);

            return new AiHealthProbeResult
            {
                IsConfigured = true,
                Success = true,
                ModelName = outcome?.ModelName ?? settings.Model,
                EndpointHost = endpointHost,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動放棄（例如使用者離開頁面）。不是故障，比照既有慣例不記帳。
            stopwatch.Stop();
            return Failed("已取消本次探測。");
        }
        catch (AiCallException ex)
        {
            stopwatch.Stop();
            logger.LogError(
                "AI health probe failed. Reason={Reason}, StatusCode={StatusCode}, ElapsedMs={ElapsedMs}",
                ex.Reason,
                ex.HttpStatus,
                stopwatch.ElapsedMilliseconds);

            return Failed(DescribeFailure(ex));
        }
        catch (Exception ex)
        {
            // 健康檢查的單一項目絕不能讓整份報告掛掉。
            stopwatch.Stop();
            logger.LogError(ex, "AI health probe failed unexpectedly. ElapsedMs={ElapsedMs}", stopwatch.ElapsedMilliseconds);
            return Failed($"無法連線到 AI 服務（{ex.GetType().Name}）。");
        }

        AiHealthProbeResult Failed(string message) => new()
        {
            IsConfigured = true,
            Success = false,
            Message = message,
            ModelName = settings.Model,
            EndpointHost = endpointHost,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// 健康頁的簡述。⚠️ 只取 HTTP 狀態與 error.code，絕不把上游 body 放進訊息。
    /// </summary>
    private static string DescribeFailure(AiCallException ex)
    {
        if (ex.Reason == AiAnalysisFailureReason.EmptyResponse)
        {
            return "上游回應成功，但內容為空。";
        }

        if (ex.Reason == AiAnalysisFailureReason.Timeout)
        {
            return $"探測逾時（超過 {ProbeTimeout.TotalSeconds:N0} 秒）。";
        }

        if (ex.HttpStatus is int status)
        {
            var code = string.IsNullOrEmpty(ex.Upstream.Code) ? string.Empty : $"（{ex.Upstream.Code}）";
            return $"上游回應 HTTP {status}{code}。";
        }

        return $"無法連線到 AI 服務（{ex.ExceptionType ?? ex.GetType().Name}）。";
    }
}
