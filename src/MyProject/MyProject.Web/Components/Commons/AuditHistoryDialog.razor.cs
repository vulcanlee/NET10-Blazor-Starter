using AntDesign;
using Microsoft.AspNetCore.Components;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Components.Commons;

public partial class AuditHistoryDialog
{
    private const int PageSize = 10;

    [Inject]
    public AuditLogQueryService AuditLogQueryService { get; set; } = default!;

    [Inject]
    public IAuditLogService AuditLogService { get; set; } = default!;

    [Inject]
    public CurrentUserService CurrentUserService { get; set; } = default!;

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
        // 按鈕只給管理員，這裡再擋一次：畫面上的顯示不等於授權。
        if (CurrentUserService.CurrentUser.IsAdmin == false)
        {
            Logger.LogWarning("Audit history denied because the current user is not an administrator. TargetType={TargetType}", targetType);
            return;
        }

        this.targetType = targetType;
        this.targetId = targetId;
        this.label = label;
        pageIndex = 1;
        visible = true;
        await LoadAsync();

        await ViewAudit.WriteSearchAsync(AuditLogService, CurrentUserService, AuditActions.Audit.Search, "AuditLog",
            AuditSearchCondition.Of("targetType", targetType), AuditSearchCondition.Of("targetId", targetId),
            AuditSearchCondition.Of("via", "history"));
    }

    private async Task ChangePageAsync(int page)
    {
        pageIndex = page;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        // 連續換頁或換一筆資料時，只採用最後一次的結果。
        var request = ++loadRequest;
        isLoading = true;
        selected = null;
        content = null;
        StateHasChanged();

        try
        {
            var result = await AuditLogQueryService.GetTargetHistoryAsync(targetType, targetId, pageIndex, PageSize);
            if (request != loadRequest)
            {
                return;
            }

            items = result.Result.ToList();
            total = result.Count;
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

        if (item.ContentFile is not null)
        {
            string text;
            try
            {
                text = await AuditLogQueryService.GetContentAsync(item.Id)
                    ?? "內容檔不存在（可能已被清除，或當初寫檔失敗）。";
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to read audit content file. AuditLogId={AuditLogId}", item.Id);
                text = "讀取詳細內容失敗。" + TraceCode.Suffix(TraceCode.Current);
            }

            // 連點兩筆時，前一筆較晚讀完不可蓋掉目前選的那一筆。
            if (selected?.Id == item.Id)
            {
                content = text;
                isContentLoading = false;
            }
        }

        await ViewAudit.WriteAsync(AuditLogService, CurrentUserService, AuditActions.Audit.View, "AuditLog", item.Id.ToString(), $"action={item.Action}; via=history");
    }
}
