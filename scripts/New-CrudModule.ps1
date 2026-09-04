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
    param([string]$RelativePath, [string]$Content)

    $fullPath = Join-Path $moduleRoot $RelativePath
    $directory = Split-Path -Path $fullPath -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    # 原始碼採 UTF-8 無 BOM；只有 docs/*.md 需要 BOM。
    Set-Content -LiteralPath $fullPath -Value $Content -Encoding utf8NoBOM
}

$lower = $Name.Substring(0, 1).ToLowerInvariant() + $Name.Substring(1)
$permissionConst = "角色_$DisplayName"
$route = "/$($lower)s"

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
    <div class="${lower}-view-toolbar">
        @if (AuthenticationStateHelper.CheckAccessAction(MagicObjectHelper.$permissionConst, PermissionActions.Create))
        {
            <ToolbarIconButton Title="新增" Icon="add" OnClick="OnAddAsync" />
        }
        <ToolbarIconButton Title="重新整理" Icon="refresh" OnClick="OnRefreshAsync" />
    </div>

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
}
"@

New-ScaffoldFile "Web/Components/Views/${Name}s/${Name}View.razor.cs" @"
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
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

    List<${Name}AdapterModel> records = new();

    int _pageIndex = 1;
    int _pageSize = MagicObjectHelper.PageSize;
    int _total = 0;
    string searchText = string.Empty;
    string sortField = string.Empty;
    string sortDirection = "None";
    string RoleMessage = string.Empty;

    ${Name}AdapterModel CurrentRecord = new();

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public ${Name}View(
        ILogger<${Name}View> logger,
        ${Name}Service ${lower}Service,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.${lower}Service = ${lower}Service;
        this.notificationService = notificationService;
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

        // TODO: 開啟維護 Modal（參考 Components/Views/Categories/CategoryViewView 的 Modal + EditForm）。
        // ⚠️ EditForm 上不要加 @onkeydown —— 見下方「尚未產生的部分」。
        return Task.CompletedTask;
    }

    Task OnEditAsync(${Name}AdapterModel record)
    {
        // ⚠️ 一律 Clone()：直接綁定會讓表單的雙向繫結污染表格來源資料。
        CurrentRecord = record.Clone();

        // TODO: 開啟維護 Modal。
        return Task.CompletedTask;
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

## 五、尚未產生的部分

檢視的 OnAddAsync / OnEditAsync 只建立了資料狀態，**維護 Modal 尚未產生**。
請參考 Components/Views/Categories/CategoryViewView.razor 的 Modal + EditForm 區塊補上，
並沿用 ViewNotification 顯示結果訊息。

⚠️ **EditForm／form 上絕不可加 @onkeydown**：keydown 會從表單內任何子元素冒泡上來，
TextArea 換行、Select 選取、DatePicker 確認日期都會變成「存檔並關窗」。
存檔的唯一入口是 <Modal OnOk>，Esc 交給 <Modal Keyboard="true">。
需要捷徑請綁在個別元件上（例如 <Input OnPressEnter="..." />）。
理由見 docs/architecture/開發慣例與限制速查.md §6.3，
由 MyProject.Tests/ModalKeyboardConventionTests.cs 守門。

## 六、驗收

    dotnet build src/MyProject/MyProject.slnx -c Release      # 0 warning（TreatWarningsAsErrors）
    dotnet test src/MyProject/MyProject.slnx
    dotnet format src/MyProject/MyProject.slnx --verify-no-changes

別忘了 **SystemVersion Patch +1** 與同步更新相關文件。
"@

Write-Host "Created CRUD module scaffold at $moduleRoot"
Write-Host "Next: read $moduleRoot/README.md for the required registration steps."
