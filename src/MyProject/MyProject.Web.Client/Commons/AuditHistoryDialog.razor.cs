using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Models.AdapterModel;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Components.Commons;

public partial class AuditHistoryDialog
{
    private const int PageSize = 10;

    [Inject]
    public IAuditHistorySource Source { get; set; } = default!;

    [Inject]
    public PagePermissions Permissions { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<AuditHistoryDialog> Logger { get; set; } = default!;

    private bool visible;
    private bool isLoading;
    private string targetType = string.Empty;
    private string targetId = string.Empty;
    private string label = string.Empty;
    private int pageIndex = 1;
    private int total;
    private List<AuditLogAdapterModel> items = [];
    private AuditLogAdapterModel? selected;
    private string? content;
    private bool isContentLoading;
    private int loadRequest;

    private string ContentText => selected?.ContentFile is null
        ? "（這一筆沒有詳細內容。修改前後差異只在「修改」、而且系統參數「稽核：記錄修改前後差異」開啟時才會記錄）"
        : isContentLoading ? "讀取中…" : content ?? string.Empty;

    /// <summary>
    /// 打開某一筆資料的異動歷程。<paramref name="targetType"/> 要與服務寫稽核時的 TargetType 相同
    /// （<c>Category</c>、<c>Project</c>、<c>Team</c>、<c>MyUser</c>、<c>RoleView</c>，CRUD 產生的模組是類別名稱）。
    /// </summary>
    public async Task ShowAsync(string targetType, string targetId, string label)
    {
        // 按鈕只給管理員，這裡再擋一次：畫面上的顯示不等於授權（資料來源在伺服器端還會再檢查）。
        if (Permissions.IsAdmin == false)
        {
            Logger.LogWarning("Audit history denied because the current user is not an administrator. TargetType={TargetType}", targetType);
            return;
        }

        this.targetType = targetType;
        this.targetId = targetId;
        this.label = label;
        pageIndex = 1;
        visible = true;
        await LoadAsync(opening: true);
    }

    private async Task ChangePageAsync(int page)
    {
        pageIndex = page;
        await LoadAsync(opening: false);
    }

    private async Task LoadAsync(bool opening)
    {
        // 連續換頁或換一筆資料時，只採用最後一次的結果。
        var request = ++loadRequest;
        isLoading = true;
        selected = null;
        content = null;
        StateHasChanged();

        try
        {
            var result = await Source.LoadAsync(targetType, targetId, pageIndex, PageSize, opening);
            if (request != loadRequest)
            {
                return;
            }

            items = result.Items.ToList();
            total = result.Total;
        }
        catch (AuditHistoryUnavailableException ex)
        {
            Logger.LogWarning("Audit history unavailable. TargetType={TargetType}, TargetId={TargetId}, Reason={Reason}", targetType, targetId, ex.Message);
            if (request != loadRequest)
            {
                return;
            }

            ViewNotification.Error(NotificationService, ex.Message);
            items = [];
            total = 0;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load audit history. TargetType={TargetType}, TargetId={TargetId}", targetType, targetId);
            if (request != loadRequest)
            {
                return;
            }

            ViewNotification.UnexpectedError(NotificationService, $"載入異動歷程失敗：{ex.GetType().Name}。", ex);
            items = [];
            total = 0;
        }
        finally
        {
            if (request == loadRequest)
            {
                isLoading = false;
                StateHasChanged();
            }
        }
    }

    /// <summary>點一筆：右邊顯示摘要，再讀詳細內容檔（修改前後差異；清單查詢不讀檔）。</summary>
    private async Task SelectAsync(AuditLogAdapterModel item)
    {
        selected = item;
        content = null;
        isContentLoading = item.ContentFile is not null;
        StateHasChanged();

        var text = await Source.ReadContentAsync(item);

        // 連點兩筆時，前一筆較晚讀完不可蓋掉目前選的那一筆。
        if (selected?.Id == item.Id && item.ContentFile is not null)
        {
            content = text;
            isContentLoading = false;
        }
    }
}
