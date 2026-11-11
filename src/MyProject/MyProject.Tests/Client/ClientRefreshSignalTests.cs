using MyProject.Web.Client.Layout;

namespace MyProject.Tests.Client;

/// <summary>
/// WASM 外框的「立即重讀」訊號（0.10.6 起，WASM 遷移 M7）：呼叫端自己已經重讀過時可以排除自己，只請其他區塊重讀
/// （例如在鈴鐺標已讀後，只請首頁「我的通知」重讀，鈴鐺不再多打一次 API）。
/// </summary>
public sealed class ClientRefreshSignalTests
{
    [Fact]
    public void RequestRefresh_ShouldCallEverySubscriber_ExceptTheCaller()
    {
        var signal = new ClientNotificationSignal();
        var bell = new Subscriber();
        var widget = new Subscriber();
        signal.RefreshRequested += bell.OnRefreshAsync;
        signal.RefreshRequested += widget.OnRefreshAsync;

        signal.RequestRefresh(except: bell.OnRefreshAsync);
        signal.RequestRefresh();

        Assert.Equal(1, bell.Calls);
        Assert.Equal(2, widget.Calls);
    }

    [Fact]
    public void RequestRefresh_WithoutSubscribers_ShouldDoNothing()
    {
        new ClientAnnouncementSignal().RequestRefresh();
    }

    private sealed class Subscriber
    {
        public int Calls { get; private set; }

        public Task OnRefreshAsync()
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
