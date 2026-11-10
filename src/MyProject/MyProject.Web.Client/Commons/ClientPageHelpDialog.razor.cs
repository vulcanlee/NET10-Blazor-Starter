using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// WASM 外框的操作說明視窗（0.10.1 起，WASM 遷移 M2）。標記、樣式、字體大小與 Server 版 <c>PageHelpDialog</c> 相同；
/// 哪個路由有說明由版面傳入的主題清單決定，內容在打開時才向 <c>/api/v1/shell/help</c> 取得。
/// </summary>
public partial class ClientPageHelpDialog : IDisposable
{
    /// <summary>「相關頁面」章節的標題（與 Server 版的 <c>PageHelpMarkdownParser.RelatedPagesHeading</c> 相同）。</summary>
    internal const string RelatedPagesHeading = "六、相關頁面";

    private const string AllSectionsKey = "__all__";

    [Inject]
    private ApiClient Api { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<ClientPageHelpDialog> Logger { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>每個頁面的說明主題（由版面以 <c>/api/v1/shell/info</c> 取得）。</summary>
    [Parameter]
    public IReadOnlyList<HelpTopicDto> Topics { get; set; } = [];

    private HelpTopicDto? topic;
    private HelpDocumentDto? document;
    private bool visible;
    private string activeSection = AllSectionsKey;
    private PageHelpFontSize fontSize = PageHelpFontSize.Medium;
    private bool fontSizeLoaded;

    private string ModalTitle => topic is null ? "操作說明" : $"{topic.Title}・操作說明";

    private IEnumerable<HelpSectionDto> VisibleSections
        => document is null
            ? []
            : activeSection == AllSectionsKey
                ? document.Sections
                : document.Sections.Where(x => x.Heading == activeSection);

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet() => ResolveTopic();

    /// <summary>依路由找說明主題：路徑去掉 query 與錨點、前後的斜線，不分大小寫完全相同（與 Server 版相同的比對）。</summary>
    internal static HelpTopicDto? MatchTopic(IReadOnlyList<HelpTopicDto> topics, string? path)
    {
        var normalized = Normalize(path);
        return topics.FirstOrDefault(x => string.Equals(Normalize(x.Route), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? path)
        => (path ?? string.Empty).Split('?', '#')[0].Trim().Trim('/');

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        visible = false;
        document = null;
        activeSection = AllSectionsKey;
        ResolveTopic();
        InvokeAsync(StateHasChanged);
    }

    private void ResolveTopic()
        => topic = MatchTopic(Topics, NavigationManager.ToBaseRelativePath(NavigationManager.Uri));

    private async Task OpenAsync()
    {
        if (topic is null)
        {
            return;
        }

        activeSection = AllSectionsKey;
        var result = await Api.GetAsync<HelpDocumentDto>($"api/v1/shell/help?route={Uri.EscapeDataString(topic.Route)}");
        if (!result.Success || result.Data is null)
        {
            Logger.LogWarning("Page help could not be loaded. Route={Route}, Status={Status}", topic.Route, result.StatusCode);
            return;
        }

        document = result.Data;
        await LoadFontSizeAsync();
        visible = true;
    }

    private void Close() => visible = false;

    private async Task LoadFontSizeAsync()
    {
        if (fontSizeLoaded)
        {
            return;
        }

        fontSizeLoaded = true;
        try
        {
            fontSize = PageHelpFontSizes.Parse(await JS.InvokeAsync<string?>("localStorage.getItem", PageHelpFontSizes.StorageKey));
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Page help font size could not be read; using the default.");
        }
    }

    private async Task SetFontSizeAsync(PageHelpFontSize size)
    {
        fontSize = size;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", PageHelpFontSizes.StorageKey, PageHelpFontSizes.ToStored(size));
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Page help font size could not be saved.");
        }
    }

    private string FontButtonCssClass(PageHelpFontSize size)
        => fontSize == size ? "page-help-font-button page-help-font-button-active" : "page-help-font-button";

    private void SelectSection(string sectionKey) => activeSection = sectionKey;

    private string NavCssClass(string sectionKey)
        => activeSection == sectionKey ? "page-help-nav-item page-help-nav-item-active" : "page-help-nav-item";

    private void NavigateTo(string route)
    {
        visible = false;
        NavigationManager.NavigateTo(route);
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        GC.SuppressFinalize(this);
    }
}
