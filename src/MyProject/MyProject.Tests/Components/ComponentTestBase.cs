using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Tests.Components;

/// <summary>
/// 元件測試（bUnit）的共用基底。每個測試類別繼承它，就有一個可以渲染 Blazor 元件的測試環境。
///
/// 刻意只放兩件事，需要登入身分或其他服務的測試出現時再擴充：
/// <list type="bullet">
/// <item>JSInterop 設為 Loose：AntDesign 的 Button、Tooltip、Modal 內部都會呼叫 JS
/// （量測元素位置、焦點、事件監聽），測試環境沒有瀏覽器，Loose 讓這些呼叫直接回傳預設值，
/// 而不是讓測試因為「沒有設定這個 JS 呼叫」而失敗。</item>
/// <item><c>AddAntDesign()</c>：註冊 ModalService、MessageService、NotificationService 等，
/// 與 Program.cs 的註冊方式相同。</item>
/// </list>
/// </summary>
public abstract class ComponentTestBase : BunitContext, IAsyncLifetime
{
    protected ComponentTestBase()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddAntDesign();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// ⚠️ 先把元件全部 Dispose 完，才讓 xUnit 呼叫 <see cref="BunitContext.Dispose()"/> 釋放服務（2026/11/08 修正偶發失敗）。
    ///
    /// AntDesign 的 <c>IDomEventListener</c> 是 transient，但全部共用同一個 scoped 的訂閱清單，而且清單是沒有鎖的 <c>List</c>。
    /// 元件 Dispose 時會在 renderer 的執行緒上從清單移除訂閱；bUnit 釋放服務時，則在測試執行緒上逐一 Dispose 這些 listener、
    /// 同樣會列舉並修改這個清單。兩邊同時進行就會丟出「Collection was modified」
    /// （ExceptionAiAnalysisModalStreamingTests、FormModalFlowTests 偶發，負載高時約 1/25～1/30）。
    /// xUnit 會在 Dispose 之前呼叫這裡，所以兩件事改成依序發生。
    /// </summary>
    async Task IAsyncLifetime.DisposeAsync() => await DisposeComponentsAsync();
}
