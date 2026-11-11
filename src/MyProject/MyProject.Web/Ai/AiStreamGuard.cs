using System.Runtime.CompilerServices;

namespace MyProject.Web.Ai;

/// <summary>
/// AI 串流端點的保護層（0.10.5 起，WASM 遷移 M6 獨立審查）：保證序列最後一定有一筆帶 <see cref="AiStreamUpdate.Result"/>。
/// <list type="bullet">
/// <item>使用者中斷（取消）時服務偶爾會丟取消例外（例如剛好在讀取作用中的提示詞）：當成一般的「已取消」結果，稽核照寫。</item>
/// <item>其他例外是服務契約之外的錯誤：記錯誤日誌、回「未預期的錯誤」，並設 <see cref="Unexpected"/>（與 Server 版畫面外層的防護相同，這種情況不寫稽核）。
/// 回應標頭在第一個字之前就送出了，不攔的話串流會直接斷掉，畫面停在「分析中」。</item>
/// </list>
/// </summary>
public sealed class AiStreamGuard
{
    public const string UnexpectedMessage = "AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。";

    public const string CanceledMessage = "已取消本次 AI 分析。";

    /// <summary>服務丟了取消以外的例外。</summary>
    public bool Unexpected { get; private set; }

    public async IAsyncEnumerable<AiStreamUpdate> RunAsync(
        IAsyncEnumerable<AiStreamUpdate> source, ILogger logger, string operation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AiAnalysisResult? result = null;
        await using (var updates = source.GetAsyncEnumerator(cancellationToken))
        {
            while (result is null)
            {
                AiStreamUpdate update;
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }

                    update = updates.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = AiAnalysisResult.Failure(AiAnalysisFailureReason.Canceled, CanceledMessage);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled exception while streaming AI analysis. Operation={Operation}", operation);
                    result = AiAnalysisResult.Failure(AiAnalysisFailureReason.Unexpected, UnexpectedMessage, detail: ex.ToString());
                    Unexpected = true;
                    break;
                }

                if (update.Result is not null)
                {
                    result = update.Result;
                    break;
                }

                yield return update;
            }
        }

        yield return new AiStreamUpdate { Result = result ?? AiAnalysisResult.Failure(AiAnalysisFailureReason.Unexpected, UnexpectedMessage) };
    }
}
