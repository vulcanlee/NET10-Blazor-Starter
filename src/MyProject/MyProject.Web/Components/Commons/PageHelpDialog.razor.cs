using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Web.Components.Layout;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 頂欄的「使用說明」按鈕與說明窗。自己取路徑、自己載索引、自己訂閱導覽事件，
/// MainLayout 只負責把已授權的選單傳進來（供「相關頁面」依權限過濾）。
/// </summary>
/// <remarks>
/// 本元件與 MainLayout 一樣跨導覽存活：<see cref="OnInitializedAsync"/> 一個 circuit 只跑一次，
/// 之後每次導覽都是記憶體內比對。
/// </remarks>
public partial class PageHelpDialog : IDisposable
{
    /// <summary>章節導覽「全部」的識別鍵，刻意用不可能與章節標題相同的字串。</summary>
    private const string AllSectionsKey = "__all__";

    [Inject]
    private PageHelpService PageHelpService { get; set; } = default!;

    [Inject]
    private SidebarMenuService SidebarMenuService { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<PageHelpDialog> Logger { get; set; } = default!;

    /// <summary>此人已授權的選單（與側邊欄同源）。</summary>
    [Parameter]
    public IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];

    private IReadOnlyList<PageHelpTopicModel> topics = [];
    private PageHelpTopicModel? topic;
    private PageHelpDocument? document;
    private List<PageHelpRelatedPage> visibleRelatedPages = [];
    private bool visible;
    private string activeSection = AllSectionsKey;

    private string ModalTitle => topic is null ? "使用說明" : $"{topic.Title}　使用說明";

    private IEnumerable<PageHelpSection> VisibleSections
        => document is null
            ? []
            : activeSection == AllSectionsKey
                ? document.Sections
                : document.Sections.Where(x => x.Heading == activeSection);

    protected override async Task OnInitializedAsync()
    {
        topics = await PageHelpService.LoadTopicsAsync();
        ResolveTopic();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    /// <summary>
    /// 導覽後必須關窗並重新比對路由；元件跨導覽存活，不關的話會繼續顯示上一頁的說明。
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        visible = false;
        document = null;
        activeSection = AllSectionsKey;
        ResolveTopic();
        InvokeAsync(StateHasChanged);
    }

    private void ResolveTopic()
        => topic = PageHelpService.MatchTopic(topics, NavigationManager.ToBaseRelativePath(NavigationManager.Uri));

    private async Task OpenAsync()
    {
        if (topic is null)
        {
            return;
        }

        Logger.LogDebug("Opening page help dialog. Route={Route}", topic.Route);

        activeSection = AllSectionsKey;
        document = await PageHelpService.LoadDocumentAsync(topic);

        var authorizedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SidebarMenuService.CollectUrls(MenuItems, authorizedUrls);
        visibleRelatedPages = PageHelpService.FilterVisibleRelatedPages(
            document.RelatedPages,
            await SidebarMenuService.LoadAllMenuUrlsAsync(),
            authorizedUrls);

        visible = true;
    }

    private void Close() => visible = false;

    private void SelectSection(string sectionKey) => activeSection = sectionKey;

    private string NavCssClass(string sectionKey)
        => activeSection == sectionKey
            ? "page-help-nav-item page-help-nav-item-active"
            : "page-help-nav-item";

    private void NavigateTo(string route)
    {
        // 先關窗再導覽（LocationChanged 也會關，這裡明示語意、不依賴事件順序）。
        visible = false;
        NavigationManager.NavigateTo(route);
    }

    public void Dispose() => NavigationManager.LocationChanged -= OnLocationChanged;
}
