using MyProject.Business.Services.DataAccess;

namespace MyProject.Web.Ai;

/// <summary>AI 分析送出的系統提示詞（0.9.108 起）：作用中的版本或內建預設，再接上固定規則。</summary>
public interface IAiSystemPromptProvider
{
    /// <summary>⚠️ 不丟例外：讀不到資料庫時改用內建預設（並寫錯誤日誌），AI 分析照常進行。</summary>
    Task<string> GetAsync(string templateKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 作用中的版本或內建預設，<b>不</b>接上固定規則（0.9.155 起）：給要把好幾段提示詞組成一份的呼叫端（系統管理助理），
    /// 組好之後再以 <see cref="AiPromptGuardrails.Compose"/> 接上一次規則。同樣不丟例外。
    /// </summary>
    Task<string> GetInstructionsAsync(string templateKey, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiSystemPromptProvider" />
public sealed class AiSystemPromptProvider : IAiSystemPromptProvider
{
    private readonly PromptTemplateService promptTemplateService;
    private readonly ILogger<AiSystemPromptProvider> logger;

    public AiSystemPromptProvider(PromptTemplateService promptTemplateService, ILogger<AiSystemPromptProvider> logger)
    {
        this.promptTemplateService = promptTemplateService;
        this.logger = logger;
    }

    public async Task<string> GetAsync(string templateKey, CancellationToken cancellationToken = default)
        => AiPromptGuardrails.Compose(templateKey, await GetInstructionsAsync(templateKey, cancellationToken));

    public async Task<string> GetInstructionsAsync(string templateKey, CancellationToken cancellationToken = default)
    {
        string? content = null;
        try
        {
            // 每次分析都重新讀取：管理員切換版本後下一次分析立即生效，不必重新整理或重啟。
            content = await promptTemplateService.GetActiveContentAsync(templateKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to read the active prompt template; using the built-in default. TemplateKey={TemplateKey}", templateKey);
        }

        return content ?? AiPromptGuardrails.DefaultInstructions(templateKey);
    }
}
