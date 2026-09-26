<#
.SYNOPSIS
    產生一個符合本專案現行慣例的 CRUD 模組骨架。

.DESCRIPTION
    產出檔案落在 -OutputPath（預設 output/crud-modules/<Name>），依方案結構分資料夾，
    需人工搬進 src/MyProject/ 對應位置，再依產出的 README.md 完成註冊。

    ⚠️ 這個產生器存在的意義，是讓新模組**不必靠複製既有檢視**。
    0.4.27 的 emoji 回歸（六個檢視共 22 個按鈕）正是複製貼上造成的。
    因此樣板必須與現行慣例同步；改了慣例請一併改這裡，
    否則產生器會再度淪為「看起來有工具、實際沒人用」的擺設。

    產出的程式碼已對齊下列慣例：
      - Blazor 服務注入 IDbContextFactory（0.4.36 起；不再需要 CleanTrackingHelper）
      - Web API 回 ApiResult<T> / PagedResult<T>，並以 [HasPermission] 做動作級授權
      - 例外一律走 this.ApiServerError（遵守 Security:ReturnExceptionDetails）
      - 檢視使用 ToolbarIconButton / CrudActionButton / TableSortHelper / ViewNotification
      - 編輯前 Clone()；權限用 CheckAccessPage + CheckAccessAction
      - Skip/Take 必搭 OrderBy；分頁在資料庫端執行
      - 附頁面使用說明初稿（Datas/Help，結構符合 PageHelpCatalogTests）

.PARAMETER Name
    模組（實體）名稱，PascalCase，例如 Equipment。

.PARAMETER DisplayName
    顯示名稱與權限鍵，預設同 Name。

.EXAMPLE
    ./scripts/New-CrudModule.ps1 -Name Equipment -DisplayName 設備清單
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9]*$')]
    [string]$Name,

    [string]$DisplayName,

    [string]$OutputPath = "output/crud-modules",

    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not $DisplayName) { $DisplayName = $Name }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$moduleRoot = Join-Path (Join-Path $repoRoot $OutputPath) $Name

if ((Test-Path -LiteralPath $moduleRoot) -and -not $Force) {
    throw "CRUD module scaffold already exists. Use -Force to overwrite: $moduleRoot"
}
if (Test-Path -LiteralPath $moduleRoot) {
    Remove-Item -LiteralPath $moduleRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $moduleRoot | Out-Null

function New-ScaffoldFile {
    param([string]$RelativePath, [string]$Content, [switch]$WithBom)

    $fullPath = Join-Path $moduleRoot $RelativePath
    $directory = Split-Path -Path $fullPath -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    # 原始碼採 UTF-8 無 BOM；docs/*.md 與頁面使用說明（Datas/Help/*.md）需要 BOM。
    $encoding = if ($WithBom) { "utf8BOM" } else { "utf8NoBOM" }
    Set-Content -LiteralPath $fullPath -Value $Content -Encoding $encoding
}

$lower = $Name.Substring(0, 1).ToLowerInvariant() + $Name.Substring(1)
$permissionConst = "角色_$DisplayName"
$route = "/$($lower)s"
# 與 PageHelpService.ToSlugFileName 相同的規則：去頭尾斜線、斜線換成 -、轉小寫。
$helpFile = "$($route.Trim('/').Replace('/', '-').ToLowerInvariant()).md"

New-ScaffoldFile "AccessDatas/Models/$Name.cs" @"
namespace MyProject.AccessDatas.Models;

/// <summary>$DisplayName</summary>
public class $Name
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Status { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
"@

New-ScaffoldFile "Dtos/Models/${Name}Dto.cs" @"
namespace MyProject.Dtos.Models;

public class ${Name}Dto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
"@

New-ScaffoldFile "Dtos/Models/${Name}CreateUpdateDto.cs" @"
using System.ComponentModel.DataAnnotations;

namespace MyProject.Dtos.Models;

public class ${Name}CreateUpdateDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "名稱不可為空白。")]
    [StringLength(100, ErrorMessage = "名稱不可超過 100 個字。")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "說明不可超過 500 個字。")]
    public string? Description { get; set; }

    public bool Status { get; set; } = true;
}
"@

New-ScaffoldFile "Dtos/Commons/${Name}SearchRequestDto.cs" @"
namespace MyProject.Dtos.Commons;

/// <summary>
/// 分頁參數的上下限由 <see cref="SearchRequestBaseDto"/> 的 [Range] 保護，
/// 違規由 ApiValidationFilter 回 400，不要在這裡重複驗證。
/// </summary>
public class ${Name}SearchRequestDto : SearchRequestBaseDto
{
    public bool? Status { get; set; }
}
"@

New-ScaffoldFile "Models/AdapterModel/${Name}AdapterModel.cs" @"
using System.ComponentModel.DataAnnotations;

namespace MyProject.Models.AdapterModel;

public class ${Name}AdapterModel
{
    public int Id { get; set; }

    [Required(ErrorMessage = "名稱 不可為空白")]
    [StringLength(100, ErrorMessage = "名稱 不可超過 100 個字")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "說明 不可超過 500 個字")]
    public string? Description { get; set; }

    public bool Status { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>⚠️ 檢視編輯前一律先 Clone()，避免雙向繫結污染表格來源資料。</summary>
    public ${Name}AdapterModel Clone() => (${Name}AdapterModel)MemberwiseClone();
}
"@

New-ScaffoldFile "Business/Repositories/${Name}Repository.cs" @"
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Dtos.Commons;

namespace MyProject.Business.Repositories;

/// <summary>
/// API 路徑用。這裡維持注入 scoped BackendDBContext ——
/// Controller 的 scope ＝ 單次 HTTP 請求，本來就正確，不需要改用 IDbContextFactory。
/// </summary>
public class ${Name}Repository
{
    private readonly BackendDBContext context;

    public ${Name}Repository(BackendDBContext context)
    {
        this.context = context;
    }

    public Task<${Name}?> GetByIdAsync(int id)
    {
        return context.Set<$Name>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<PagedResult<$Name>> GetPagedAsync(${Name}SearchRequestDto request)
    {
        var query = context.Set<$Name>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            query = query.Where(x => x.Name.Contains(request.Keyword));
        }

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        var totalCount = await query.CountAsync();

        // Skip/Take 必須搭配 OrderBy，否則分頁結果不穩定。
        var items = await query
            .OrderByDescending(x => x.Id)
            .Skip((request.PageIndex - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResult<$Name>
        {
            Items = items,
            TotalCount = totalCount,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize)
        };
    }
}
"@

New-ScaffoldFile "Business/Services/DataAccess/${Name}Service.cs" @"
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// ⚠️ 注入 IDbContextFactory 而非 BackendDBContext：
/// Blazor Server 的 DI scope ＝ SignalR circuit（可存活數小時），
/// scoped 的 DbContext 會累積追蹤實體，並在並行事件時拋
/// 「A second operation was started on this context」。
/// 由 DataAccessServiceLifetimeTests 守門。
/// </summary>
public class ${Name}Service
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;

    public IMapper Mapper { get; }
    public ILogger<${Name}Service> Logger { get; }

    public ${Name}Service(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<${Name}Service> logger)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
    }

    public async Task<DataRequestResult<${Name}AdapterModel>> GetAsync(DataRequest dataRequest)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        DataRequestResult<${Name}AdapterModel> result = new();
        IQueryable<$Name> dataSource = context.Set<$Name>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            dataSource = dataSource.Where(x =>
                x.Name.Contains(dataRequest.Search) ||
                (x.Description != null && x.Description.Contains(dataRequest.Search)));
        }

        // Skip/Take 一定要搭配 OrderBy，否則分頁結果不穩定。
        dataSource = dataRequest.SortDescending == true
            ? dataSource.OrderByDescending(x => x.Id)
            : dataSource.OrderBy(x => x.Id);

        // 分頁在資料庫端執行，不要先 ToList 再切。
        // DataRequest 用 CurrentPage/PageSize（沒有 Skip 屬性），與既有服務一致。
        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        var items = await dataSource.ToListAsync();

        result.Result = Mapper.Map<List<${Name}AdapterModel>>(items);
        return result;
    }

    public async Task<${Name}AdapterModel> GetAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        ${Name}? item = await context.Set<$Name>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? new ${Name}AdapterModel() : Mapper.Map<${Name}AdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(${Name}AdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            $Name item = Mapper.Map<$Name>(paraObject);
            item.CreatedAt = DateTime.Now;
            item.UpdatedAt = DateTime.Now;

            await context.Set<$Name>().AddAsync(item);
            await context.SaveChangesAsync();

            Logger.LogInformation("$Name created successfully. ${Name}Id={${Name}Id}", item.Id);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create $lower.");
            return VerifyRecordResultFactory.Build(false, "新增失敗");
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(${Name}AdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            ${Name}? item = await context.Set<$Name>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == paraObject.Id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到指定的紀錄");
            }

            $Name itemData = Mapper.Map<$Name>(paraObject);
            itemData.CreatedAt = item.CreatedAt;
            itemData.UpdatedAt = DateTime.Now;

            context.Entry(itemData).State = EntityState.Modified;
            await context.SaveChangesAsync();

            Logger.LogInformation("$Name updated successfully. ${Name}Id={${Name}Id}", itemData.Id);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update $lower.");
            return VerifyRecordResultFactory.Build(false, "修改失敗");
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            ${Name}? item = await context.Set<$Name>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (item is null)
            {
                return VerifyRecordResultFactory.Build(false, "找不到指定的紀錄");
            }

            context.Entry(item).State = EntityState.Deleted;
            await context.SaveChangesAsync();

            Logger.LogInformation("$Name deleted successfully. ${Name}Id={${Name}Id}", id);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete $lower.");
            return VerifyRecordResultFactory.Build(false, "刪除失敗");
        }
    }

    public async Task<VerifyRecordResult> BeforeAddCheckAsync(${Name}AdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var duplicated = await context.Set<$Name>().AsNoTracking().AnyAsync(x => x.Name == paraObject.Name);
        return duplicated
            ? VerifyRecordResultFactory.Build(false, "已經存在相同的名稱")
            : VerifyRecordResultFactory.Build(true);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(${Name}AdapterModel paraObject)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var duplicated = await context.Set<$Name>().AsNoTracking()
            .AnyAsync(x => x.Name == paraObject.Name && x.Id != paraObject.Id);
        return duplicated
            ? VerifyRecordResultFactory.Build(false, "已經存在相同的名稱")
            : VerifyRecordResultFactory.Build(true);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(${Name}AdapterModel paraObject)
    {
        // 若本模組被其他資料表參照，請在此加上參照檢查後再允許刪除。
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }
}
"@

New-ScaffoldFile "Web/Controllers/${Name}Controller.cs" @"
using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Repositories;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Share.Helpers;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ${Name}Controller : ControllerBase
{
    private readonly ILogger<${Name}Controller> logger;
    private readonly ${Name}Repository ${lower}Repository;
    private readonly IMapper mapper;

    public ${Name}Controller(
        ILogger<${Name}Controller> logger,
        ${Name}Repository ${lower}Repository,
        IMapper mapper)
    {
        this.logger = logger;
        this.${lower}Repository = ${lower}Repository;
        this.mapper = mapper;
    }

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.$permissionConst, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<${Name}Dto>>> GetById(int id)
    {
        try
        {
            var item = await ${lower}Repository.GetByIdAsync(id);
            if (item is null)
            {
                return NotFound(ApiResult<${Name}Dto>.NotFoundResult(`$"找不到 ID 為 {id} 的$DisplayName"));
            }

            return Ok(ApiResult<${Name}Dto>.SuccessResult(mapper.Map<${Name}Dto>(item), "查詢成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get $lower. ${Name}Id={${Name}Id}", id);
            // 一律用 this.ApiServerError：它依 Security:ReturnExceptionDetails 決定是否
            // 夾帶例外細節。不要自己組 ApiResult.ServerErrorResult(message, exception)，
            // 那個多載會無條件回傳堆疊追蹤，Production 會外洩。
            return this.ApiServerError<${Name}Dto>("查詢失敗", ex);
        }
    }

    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.$permissionConst, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<${Name}Dto>>>> Search([FromBody] ${Name}SearchRequestDto request)
    {
        try
        {
            var paged = await ${lower}Repository.GetPagedAsync(request);

            var result = new PagedResult<${Name}Dto>
            {
                Items = mapper.Map<List<${Name}Dto>>(paged.Items),
                TotalCount = paged.TotalCount,
                PageIndex = paged.PageIndex,
                PageSize = paged.PageSize,
                TotalPages = paged.TotalPages
            };

            return Ok(ApiResult<PagedResult<${Name}Dto>>.SuccessResult(result, "搜尋成功"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search $lower records.");
            return this.ApiServerError<PagedResult<${Name}Dto>>("搜尋失敗", ex);
        }
    }
}
"@

New-ScaffoldFile "Web/Components/Pages/${Name}Page.razor" @"
@page "$route"

<PageTitle>$DisplayName</PageTitle>

<${Name}View />
"@

New-ScaffoldFile "Web/Components/Views/${Name}s/${Name}View.razor" @"
@if (!string.IsNullOrWhiteSpace(RoleMessage))
{
    <div class="alert alert-danger" role="alert">@RoleMessage</div>
}
else
{
    @*
        工具列與表格容器用全域共用類別（wwwroot/theme.css），不要再各自寫一份 .razor.css。
        表格本身的視覺（表頭、斑馬、hover、排序箭頭、分頁）也由 theme.css 全站統一提供。
        既有的 10 個檢視仍帶自己的 <前綴>-view-toolbar，那是歷史包袱，新模組不要跟進。
    *@
    <div class="view-toolbar">
        <div class="view-toolbar-left">
            @if (AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.$permissionConst, PermissionActions.Create))
            {
                <ToolbarIconButton Title="新增" Icon="add" OnClick="OnAddAsync" />
            }
            <ToolbarIconButton Title="重新整理" Icon="refresh" OnClick="OnRefreshAsync" />
        </div>
    </div>

    <div class="view-table-wrap">
    <Table TItem="${Name}AdapterModel"
           DataSource="@records"
           Total="_total"
           @bind-PageIndex="_pageIndex"
           @bind-PageSize="_pageSize"
           RemoteDataSource
           OnChange="OnTableChange"
           RowKey="x => x.Id.ToString()">
        <Column TData="string" DataIndex="@nameof(${Name}AdapterModel.Name)" Title="名稱" Sortable />
        <Column TData="string" DataIndex="@nameof(${Name}AdapterModel.Description)" Title="說明" />
        <ActionColumn Title="操作">
            @if (AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.$permissionConst, PermissionActions.Edit))
            {
                <CrudActionButton Title="修改" Icon="edit" OnClick="() => OnEditAsync(context)" />
            }
            @if (AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.$permissionConst, PermissionActions.Delete))
            {
                <CrudActionButton Title="刪除" Icon="delete" Danger OnClick="() => OnDeleteAsync(context)" />
            }
        </ActionColumn>
    </Table>
    </div>

    @*
        大量資料輸入對話窗骨架。規範見 docs/architecture/對話窗 UI 設計規範.md：
        接近滿版、欄位 2 欄、區塊分組、未儲存二次確認、粉梅暖雪果凍視覺。

        ⚠️ Class 一定要有 form-modal（尺寸與版型的唯一來源），不要改用 Modal 的 Width 參數。
        ⚠️ EditForm／form 上絕不可掛鍵盤事件 —— keydown 會從 TextArea／Select／DatePicker
           冒泡上來，變成「輸入還沒完成就存檔關窗」。存檔唯一入口是 Modal 的 OnOk。
        ⚠️ 不適合 2 欄的欄位（多行文字、檔案上傳、清單、權限矩陣）加 Class="form-field-full"。
        以上三點都由 MyProject.Tests/FormModalConventionTests.cs 與 ModalKeyboardConventionTests.cs 守門。

        ⚠️ 樣式要寫哪裡，判準是 DOM 位置，不是元件名稱（速查表 §6.9）：
           渲染在 AntContainer 底下（Modal／Confirm／Notification／Message）→ OverlayStyles.razor
           渲染在頁面 DOM 內（Table／Pagination／Input／Select／Tag）→ wwwroot/theme.css
           顏色一律用 var(--app-*)，不要寫死色碼（ThemeConventionTests 守門）。
    *@
    <Modal Title="@modalTitle"
           Class="form-modal"
           @bind-Visible="@modalVisible"
           Keyboard="true"
           MaskClosable="false"
           OkText="@("儲存")"
           CancelText="@("取消")"
           OnOk="OnModalOKHandleAsync"
           OnCancel="OnModalCancelHandleAsync">
        <EditForm Model="@CurrentRecord" Context="editContext">
            <DataAnnotationsValidator />
            <ValidationSummary />
            <InputWatcher EditContextActionChanged="OnEditContestChanged" />

            <AntDesign.Form TModel="${Name}AdapterModel" Model="@CurrentRecord" Layout="FormLayout.Vertical" Context="formContext">
                <FormSection Title="基本資料">
                    <FormItem Label="名稱" Required>
                        <Input @bind-Value="CurrentRecord.Name" Placeholder="請輸入名稱" />
                        <ValidationMessage For="() => CurrentRecord.Name" />
                    </FormItem>

                    @* TODO: 其餘欄位依實際模型補上；短欄位放這裡即可自動 2 欄排列。 *@

                    <FormItem Label="說明" Class="form-field-full">
                        <TextArea @bind-Value="CurrentRecord.Description" Rows="4" Placeholder="請輸入說明" />
                        <ValidationMessage For="() => CurrentRecord.Description" />
                    </FormItem>
                </FormSection>
            </AntDesign.Form>
        </EditForm>
    </Modal>
}
"@

New-ScaffoldFile "Web/Components/Views/${Name}s/${Name}View.razor.cs" @"
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.${Name}s;

public partial class ${Name}View
{
    private readonly ILogger<${Name}View> logger;
    private readonly ${Name}Service ${lower}Service;
    private readonly NotificationService notificationService;
    private readonly ModalService modalService;

    List<${Name}AdapterModel> records = new();

    int _pageIndex = 1;
    int _pageSize = MagicObjectHelper.PageSize;
    int _total = 0;
    string searchText = string.Empty;
    string sortField = string.Empty;
    string sortDirection = "None";
    string RoleMessage = string.Empty;

    ${Name}AdapterModel CurrentRecord = new();

    string modalTitle = "$DisplayName維護";
    bool modalVisible = false;
    bool isNewRecordMode;
    public EditContext? LocalEditContext { get; set; }

    /// <summary>
    /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
    /// 「改了又改回原值」視為無變更，不會白問使用者一次。
    /// ⚠️ 不要改用 EditContext.IsModified()：AntDesign 的輸入元件不會通知外層 EditContext，
    ///    它會恆為 false，未儲存提示永遠不跳，而且不會有任何徵兆。
    /// </summary>
    private readonly FormDirtyTracker dirtyTracker = new();

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public ${Name}View(
        ILogger<${Name}View> logger,
        ${Name}Service ${lower}Service,
        NotificationService notificationService,
        ModalService modalService)
    {
        this.logger = logger;
        this.${lower}Service = ${lower}Service;
        this.notificationService = notificationService;
        this.modalService = modalService;
    }

    protected override async Task OnInitializedAsync()
    {
        await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);

        // ⚠️ 這裡的權限鍵必須與 SidebarMenuService.MenuPermissionMap 中該路由的鍵一致，
        // 否則使用者會「看得到選單、點進去被踢」。
        // 新增檢視時請於 MenuPermissionConsistencyTests.ViewToMenuId 登錄，測試會驗證。
        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.$permissionConst) == false)
        {
            RoleMessage = "你沒有權限存取此頁面";
            return;
        }

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var result = await ${lower}Service.GetAsync(new DataRequest
        {
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = _pageSize,
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "descend" ? true : sortDirection == "ascend" ? false : null,
        });

        records = result.Result.ToList();
        _total = result.Count;
        StateHasChanged();
    }

    async Task OnTableChange(QueryModel<${Name}AdapterModel> args)
    {
        // ⚠️ 排序解析一律走 TableSortHelper：它以 reflection 讀 AntDesign 內部屬性，
        // 對套件升版脆弱，因此全專案只保留一份。不要複製回本檔。
        var sortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
        sortField = TableSortHelper.ResolveSortFieldName(sortModel);
        sortDirection = TableSortHelper.HasSortDirection(sortModel.SortDirection)
            ? sortModel.SortDirection.ToString() ?? "None"
            : "None";

        _pageIndex = args.PageIndex;
        _pageSize = args.PageSize;
        await ReloadAsync();
    }

    async Task OnRefreshAsync()
    {
        await ReloadAsync();
        ViewNotification.Warning(notificationService, "已更新最新資料");
    }

    Task OnAddAsync()
    {
        CurrentRecord = new ${Name}AdapterModel();
        isNewRecordMode = true;
        modalTitle = "新增$DisplayName";

        // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
        dirtyTracker.Capture(CurrentRecord);

        modalVisible = true;
        return Task.CompletedTask;
    }

    Task OnEditAsync(${Name}AdapterModel record)
    {
        // ⚠️ 一律 Clone()：直接綁定會讓表單的雙向繫結污染表格來源資料。
        CurrentRecord = record.Clone();
        isNewRecordMode = false;
        modalTitle = "修改$DisplayName";

        // ⚠️ 必須是開窗前的最後一步。
        dirtyTracker.Capture(CurrentRecord);

        modalVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 「儲存」按鈕。
    ///
    /// ⚠️ 第一行的 modalVisible = true 不可移動，也不可在它之前 await：
    /// AntDesign 在呼叫本方法**之前**就已送出 VisibleChanged(false)，這一行是在同一個
    /// render batch 內把它搶回來（那個 false 從來不會被畫出來，所以不會閃爍）。
    /// 之後的開關一律由 FormModalFlow 的回傳值決定 —— 失敗路徑只要 return false。
    /// ⚠️ args 可能是 null，不要解參考它。
    /// </summary>
    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.RunOkAsync(
            SaveAsync,
            logger,
            notificationService,
            "儲存$DisplayName時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
    }

    /// <summary>
    /// 實際存檔。回傳 true 表示已完成、可以關窗；
    /// 任何驗證失敗或使用者中止都 return false，不必再碰 modalVisible。
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (LocalEditContext?.Validate() == false)
        {
            foreach (var error in LocalEditContext.GetValidationMessages())
            {
                ViewNotification.ValidationError(notificationService, error);
            }

            return false;
        }

        // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
        if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
        {
            ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");
            return true;
        }

        // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
        // 就不必白跑一次資料庫來回。
        if (await FormEditConfirm.AskSaveAsync(modalService) == false)
        {
            return false;
        }

        var actionResult = isNewRecordMode
            ? await ${lower}Service.AddAsync(CurrentRecord)
            : await ${lower}Service.UpdateAsync(CurrentRecord);

        // 前置檢查通過不代表寫得進去：唯一索引在並發時仍會擋下，
        // 忽略這個回傳值會讓失敗的儲存顯示成「新增成功」。
        if (!actionResult.Success)
        {
            ViewNotification.Error(notificationService, actionResult.Message);
            return false;
        }

        ViewNotification.Warning(notificationService, isNewRecordMode ? "新增成功" : "修改成功");

        await ReloadAsync();
        dirtyTracker.Clear();

        return true;
    }

    /// <summary>
    /// 取消／✕／ESC 的共用出口（遮罩已由 MaskClosable="false" 擋掉，不會走到這裡）。
    /// 有未儲存變更時先問過使用者；無變更直接關閉，不打擾。
    ///
    /// ⚠️ 第一行的 modalVisible = true 不可移動：AntDesign 呼叫本方法前已送出
    /// VisibleChanged(false)，要讓「繼續編輯」留住整窗輸入就得在這裡搶回來。
    /// ⚠️ args 可能是 null（ESC／✕ 走 Config.OnCancel.Invoke(null)），不要解參考它。
    /// </summary>
    private async Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, dirtyTracker.IsDirty(CurrentRecord));

        if (modalVisible == false)
        {
            dirtyTracker.Clear();
        }
    }

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    async Task OnDeleteAsync(${Name}AdapterModel record)
    {
        var checkResult = await ${lower}Service.BeforeDeleteCheckAsync(record);
        if (!checkResult.Success)
        {
            ViewNotification.Error(notificationService, checkResult.Message);
            return;
        }

        var result = await ${lower}Service.DeleteAsync(record.Id);
        if (!result.Success)
        {
            ViewNotification.Error(notificationService, result.Message);
            return;
        }

        logger.LogInformation("$Name deleted from view. ${Name}Id={${Name}Id}", record.Id);
        await ReloadAsync();
        ViewNotification.Warning(notificationService, "刪除成功");
    }
}
"@

New-ScaffoldFile "Tests/${Name}ServiceTests.cs" @"
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Tests;

public sealed class ${Name}ServiceTests
{
    [Fact]
    public async Task AddAsync_ShouldPersistRecord()
    {
        await using var fixture = await ${Name}ServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddAsync(new ${Name}AdapterModel { Name = "甲" });

        Assert.True(result.Success);
        Assert.Equal(1, await fixture.Context.Set<$Name>().CountAsync());
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicatedName_ShouldFail()
    {
        await using var fixture = await ${Name}ServiceFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.AddAsync(new ${Name}AdapterModel { Name = "甲" });

        var result = await service.BeforeAddCheckAsync(new ${Name}AdapterModel { Name = "甲" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRecord()
    {
        await using var fixture = await ${Name}ServiceFixture.CreateAsync();
        var service = fixture.CreateService();
        await service.AddAsync(new ${Name}AdapterModel { Name = "甲" });
        var id = await fixture.Context.Set<$Name>().Select(x => x.Id).SingleAsync();

        var result = await service.DeleteAsync(id);

        Assert.True(result.Success);
        Assert.Equal(0, await fixture.Context.Set<$Name>().CountAsync());
    }

    private sealed class ${Name}ServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private ${Name}ServiceFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;
            loggerFactory = LoggerFactory.Create(_ => { });
            var configuration = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory);
            mapper = configuration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<${Name}ServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();
            return new ${Name}ServiceFixture(connection, context);
        }

        // ⚠️ 服務注入 IDbContextFactory，測試也要用工廠（在同一條連線上開新 context），
        // 才能真實反映正式環境「每次操作各拿一個乾淨 context」的行為。
        public ${Name}Service CreateService()
            => new(new TestDbContextFactory(connection), mapper, loggerFactory.CreateLogger<${Name}Service>());

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
"@

# 頁面使用說明初稿：結構已符合 PageHelpCatalogTests（七段、一分鐘看懂、相關頁面格式），
# 內容是通用 CRUD 文字，搬進專案後請依實際欄位修潤。
# ⚠️ 這段是 PowerShell 的雙引號 here-string，反引號是跳脫字元，所以內文不用 Markdown 行內 code。
New-ScaffoldFile "Web/Datas/Help/$helpFile" -WithBom @"
# $DisplayName

在這一頁**新增、修改、刪除**「$DisplayName」的資料，並用清單瀏覽既有紀錄。

### 一分鐘看懂這一頁

- **解決什麼問題**：集中維護「$DisplayName」的資料，不必各自記在不同地方。
- **誰會用到**：被授予「$DisplayName」權限的使用者；新增、修改、刪除按鈕會依你的權限個別顯示。
- **它在系統的哪個位置**：從左側選單的「$DisplayName」進入；清單每一列右側有修改與刪除按鈕。
- **開始前要準備**：先確認要輸入的名稱，名稱是必填欄位，說明可以之後再補。
- **做完會得到**：一筆會出現在清單中、可以排序與修改的「$DisplayName」紀錄。
- **它不做什麼**：這一頁不負責權限設定，誰能看到、誰能修改由管理員在角色管理中決定。

## 一、功能摘要

這一頁以清單列出所有「$DisplayName」紀錄，可以依名稱排序、分頁瀏覽。
有權限時可以新增一筆紀錄、修改既有紀錄的名稱與說明，或刪除不再需要的紀錄。

## 二、這個頁面在做什麼

清單會向伺服器分頁查詢資料，每次只取目前這一頁的紀錄，資料再多也不會拖慢畫面。
新增與修改都在同一個對話窗裡完成，儲存前會檢查必填欄位；
如果你改了內容卻按取消，系統會先詢問是否放棄變更，避免不小心遺失輸入。

## 三、畫面上有哪些按鈕、各自做什麼

| 按鈕／欄位 | 做什麼 |
|---|---|
| 新增 | 開啟空白的編輯對話窗；只有擁有新增權限時才會出現 |
| 重新整理 | 重新向伺服器讀取清單，看到其他人剛做的異動 |
| 名稱（欄位標題） | 點欄位標題可以切換遞增／遞減排序 |
| 修改 | 每一列右側的按鈕，開啟這筆紀錄的編輯對話窗；需要修改權限 |
| 刪除 | 每一列右側的紅色按鈕，確認後刪除這筆紀錄；需要刪除權限 |
| 儲存／取消 | 編輯對話窗下方的按鈕；名稱未填時無法儲存 |

## 四、建議這樣操作，會得到什麼

### 新增一筆紀錄

1. 按工具列的「新增」。
2. 輸入名稱，視需要補上說明。
3. 按「儲存」，清單會出現這筆新紀錄。

### 修改既有紀錄

1. 在清單找到要改的那一列，按右側的「修改」。
2. 調整內容後按「儲存」；不想改了就按「取消」並確認放棄變更。

## 五、名詞解釋

| 名詞 | 白話解釋 |
|---|---|
| 名稱 | 這筆紀錄給人辨識用的名字，是必填欄位 |
| 說明 | 補充這筆紀錄用途的文字，可以留空 |
| 權限 | 決定你能不能看到這一頁，以及能不能新增、修改、刪除，由管理員在角色管理中設定 |

## 六、相關頁面

以下列出與這一頁搭配使用的頁面；你沒有權限進入的頁面不會顯示。

- [首頁](/App)：登入後的落地頁，可以從快速入口回到常用功能。
- [角色管理](/roleviews)：管理員在這裡決定哪些角色可以檢視、新增、修改、刪除這一頁的資料。

## 七、常見問題

**為什麼我看不到「新增」按鈕？**
新增、修改、刪除按鈕會依你的權限個別顯示。需要這些動作時，請洽管理員調整你的角色權限。

**刪除之後可以復原嗎？**
不行，刪除會直接移除這筆紀錄。不確定時請先改用修改，或向管理員確認後再刪除。
"@

New-ScaffoldFile "README.md" @"
# $DisplayName（$Name）模組整合說明

本目錄是骨架，**尚未**加入方案。請依下列步驟搬移並完成註冊。
每一步都對應專案的既有不變量，漏掉會有守門測試或執行期錯誤提醒你。

## 一、搬移檔案

| 產出 | 目的地 |
|------|--------|
| AccessDatas/Models/$Name.cs | src/MyProject/MyProject.AccessDatas/Models/ |
| Dtos/ | src/MyProject/MyProject.Dtos/ |
| Models/AdapterModel/ | src/MyProject/MyProject.Models/AdapterModel/ |
| Business/ | src/MyProject/MyProject.Business/ |
| Web/ | src/MyProject/MyProject.Web/ |

搬入 Web/ 之後，`Components/_Imports.razor` 需加入：
`@using MyProject.Web.Components.Views.${Name}s`
（既有檢視都放在自己的子命名空間，Page 薄殼才找得到元件）。
| Tests/ | src/MyProject/MyProject.Tests/ |

## 二、資料層註冊

1. BackendDBContext 加入：public virtual DbSet<$Name> $Name { get; set; }
2. MyProject.Business/Models/AutoMapping.cs 加入對應：
   CreateMap<$Name, ${Name}AdapterModel>().ReverseMap();
   CreateMap<$Name, ${Name}Dto>();
3. **產生 SQLite migration**（不可略過，否則程式與 schema 不一致）：
   dotnet ef migrations add Add$Name --project src/MyProject/MyProject.AccessDatas --startup-project src/MyProject/MyProject.Web

## 三、DI 註冊

MyProject.Web/Extensions/ServiceCollectionExtensions.cs：

    services.AddScoped<${Name}Service>();
    services.AddScoped<${Name}Repository>();

## 四、權限與選單（四方一致）⚠️

1. MagicObjectHelper 新增權限鍵常數：
   public const string $permissionConst = "$DisplayName";
   **不得帶前後空白**（MenuPermissionConsistencyTests 會擋）。
2. Datas/Menu.json 新增項目並**給定唯一 id**（下例用 99，請改成實際值）：
   { "id": 99, "name": "$DisplayName", "icon": "category", "url": "$route" }
   icon 必須是有效的 **classic** Material Icons 名稱（非 Material Symbols），
   並加入 MenuIconTests.AllowedIcons。
3. SidebarMenuService.MenuPermissionMap 加入：[99] = MagicObjectHelper.$permissionConst,
4. RolePermissionService.GetRoleListPermissionAllName() 把權限鍵放進對應群組。
   若本頁要做成**管理員專屬**，則反過來：不要放進矩陣，改在檢視用 CheckIsAdmin()，
   並把權限鍵加入 AdminOnlyPermissionTests 白名單。
5. MenuPermissionConsistencyTests.ViewToMenuId 加入：["${Name}View.razor.cs"] = 99,
   這條會驗證檢視實際使用的權限鍵與選單對應一致 ——
   對不上就是「看得到選單、點進去被踢」。
6. **頁面使用說明**：Web/Datas/Help/$helpFile 已隨 Web/ 一起搬入（UTF-8 含 BOM，檔名不可改）。
   在 Datas/HelpTopics.json 加入：
   { "route": "$route", "title": "$DisplayName", "file": "$helpFile" }
   title 要與 Menu.json 的 name 一致。初稿是通用 CRUD 文字，請依實際欄位修潤第三段
   「畫面上有哪些按鈕、各自做什麼」。漏登記或格式不符，PageHelpCatalogTests 會擋。

## 五、尚未產生的部分

維護 Modal 的**骨架已經產生**（接近滿版、2 欄版型、未儲存二次確認、果凍視覺都已接好），
但裡面只有「名稱」與「說明」兩個示意欄位，請依實際模型補齊其餘欄位：

- 短欄位直接放進 FormSection，會自動 2 欄由左而右排列。
- 不適合 2 欄的欄位（多行文字、檔案上傳、清單、權限矩陣）加 Class="form-field-full" 獨占整行。
- 欄位一多就再開一個 FormSection 分組，別讓使用者在一長串欄位裡找東西。
- 不在模型裡的暫存狀態（待上傳檔案等）要傳指紋給 dirtyTracker.Capture 的第二個參數，
  否則「只加了檔案、沒動欄位」會被判定為無變更而直接關窗。

規範全文見 docs/architecture/對話窗 UI 設計規範.md，
由 MyProject.Tests/FormModalConventionTests.cs 守門。

⚠️ **EditForm／form 上絕不可加 @onkeydown**：keydown 會從表單內任何子元素冒泡上來，
TextArea 換行、Select 選取、DatePicker 確認日期都會變成「存檔並關窗」。
存檔的唯一入口是 <Modal OnOk>，Esc 交給 <Modal Keyboard="true">。
需要捷徑請綁在個別元件上（例如 <Input OnPressEnter="..." />）。
理由見 docs/architecture/開發慣例與限制速查.md §6.3，
由 MyProject.Tests/ModalKeyboardConventionTests.cs 守門。

⚠️ **不要把 modalVisible = true 補在每條失敗路徑**：handler 第一行已經搶回 Visible，
失敗路徑只要 return false。舊寫法只要漏補一次，症狀就是「驗證失敗 → 窗關了 → 輸入全丟」。

## 六、驗收

    dotnet build src/MyProject/MyProject.slnx -c Release      # 0 warning（TreatWarningsAsErrors）
    dotnet test src/MyProject/MyProject.slnx
    dotnet format src/MyProject/MyProject.slnx --verify-no-changes

別忘了 **SystemVersion Patch +1** 與同步更新相關文件。
"@

Write-Host "Created CRUD module scaffold at $moduleRoot"
Write-Host "Next: read $moduleRoot/README.md for the required registration steps."
