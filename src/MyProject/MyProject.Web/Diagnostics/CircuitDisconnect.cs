using Microsoft.JSInterop;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 「連線已中斷，JS 呼叫送不出去」的判斷（0.9.172 起，瀏覽器人工驗收時發現）。
///
/// 使用者關掉分頁或網路斷線時，還在路上的 JS interop 呼叫（AntDesign 的元件、我們自己的 JS）會一起失敗，
/// 大多是沒人等的 Task，最後變成一批 UnobservedTaskException（實測關一次分頁 13～48 筆）。
/// 連線斷了本來就呼叫不到，這是預期情況（微軟文件也建議忽略），不該進系統例外紀錄、更不該計入暴增告警。
/// 與「使用者取消」（<see cref="OperationCanceledException"/>）同類。
/// </summary>
public static class CircuitDisconnect
{
    /// <summary>
    /// 例外是否「全部」都是連線中斷造成的：本身是 <see cref="JSDisconnectedException"/>，
    /// 或是 <see cref="AggregateException"/> 裡（展開巢狀後）每一個都是。混了別的例外就不算，照常記錄。
    /// </summary>
    public static bool IsOnlyDisconnect(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            JSDisconnectedException => true,
            AggregateException aggregate => aggregate.Flatten().InnerExceptions is { Count: > 0 } inner
                && inner.All(x => x is JSDisconnectedException),
            _ => false,
        };
    }
}
