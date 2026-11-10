using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Layout;

/// <summary>WASM 外框側邊欄（展開狀態）的一個選單節點，標記與 Server 版 <c>SidebarMenuNode</c> 相同（0.10.1 起）。</summary>
public partial class ClientSidebarMenuNode : ComponentBase
{
    [Parameter, EditorRequired]
    public ClientMenuItem Item { get; set; } = default!;

    [Parameter]
    public int Level { get; set; }

    [Parameter, EditorRequired]
    public string ItemKey { get; set; } = default!;

    /// <summary>使用者在確認窗按下「取消」時通知上層，把選中狀態拉回目前路由（原因同 Server 版）。</summary>
    [Parameter]
    public EventCallback OnLogoutCancelled { get; set; }

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private ClientSession Session { get; set; } = default!;

    private async Task OnLogoutClickAsync()
    {
        if (await LogoutConfirm.RequestAsync(ModalService, Session.LogoutAsync) == false)
        {
            await OnLogoutCancelled.InvokeAsync();
        }
    }

    private string GetMenuKey() => ItemKey;

    private NavLinkMatch GetMatchMode()
        => string.Equals(Item.Url, "/", StringComparison.Ordinal) ? NavLinkMatch.All : NavLinkMatch.Prefix;

    private string GetMaterialIconKind() => Item.IconKind;
}
