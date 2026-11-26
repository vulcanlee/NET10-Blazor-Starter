namespace MyProject.Web.Ai;

/// <summary>
/// AI 畫面逐段顯示共用的常數與等待計時（0.10.45 起，整併自日誌檢視、AI 例外分析、系統管理助理三份相同的寫法）。
/// 各頁的串流流程（停止與關窗的差別、追問、對話）不同，仍由各頁自己處理。
/// </summary>
public static class AiStreaming
{
    /// <summary>伺服器 <c>AiAnalysisFailureReason.Canceled</c> 的名稱（串流結果的 <c>AiResultDto.Reason</c>）。</summary>
    public const string CanceledReason = "Canceled";

    /// <summary>串流中重新轉 HTML 的節流：太頻繁會讓長報告每一段都整份重轉、重送。</summary>
    public static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// 等待中每秒呼叫一次 <paramref name="onSecond"/>（各頁在裡面把秒數加一並 <c>InvokeAsync(StateHasChanged)</c>）。
    /// 轉圈圈只證明瀏覽器還活著，跳動的秒數才證明這次呼叫還在進行中。token 取消（呼叫結束或使用者放棄）或元件已釋放時安靜結束。
    /// </summary>
    public static async Task RunElapsedTickerAsync(Func<Task> onSecond, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                await onSecond();
            }
        }
        catch (OperationCanceledException)
        {
            // 呼叫結束或使用者放棄，正常收工。
        }
        catch (ObjectDisposedException)
        {
            // 元件已經釋放，沒有畫面可以更新了。
        }
    }
}
