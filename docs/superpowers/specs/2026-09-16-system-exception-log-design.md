# 系統例外紀錄（ExceptionLog）設計規格

- 文件版本：1.0
- 文件狀態：設計完成，待實作
- 目標系統版本：0.9.11
- 設計日期：2026/09/16

> 本文為 brainstorming 流程的產出，記錄「為什麼這樣做」。實作後的現況請以
> [`docs/prd/`](../../prd/README.md) 為準，變更結果見 [`docs/changelog/`](../../changelog/README.md)。

---

## 一、目標與問題

### 使用者的原話

> 記錄系統執行時拋出的例外：是哪個使用者、在哪一頁、按了哪顆按鈕、屬於哪個背景作業時出的錯。
> 使用者回報「系統怪怪的」時，可搭配日誌下載頁一起查。本頁僅管理員可見。
>
> 相同的例外會合併成一列並累加次數 —— 大語言模型主機不可用或逾時，往往在短時間內重複數百次，
> 不合併的話會把少見而更值得注意的例外淹沒。「次數」欄就是這段期間內重複發生的次數。
>
> 本頁收錄的是程式碼有記錄下來的例外。少數刻意忽略的情況（例如使用者取消、正常關機）不會出現在這裡，
> 完整的逐行日誌仍請由「日誌下載」取得。

### 現況（探索結果）

| 事實 | 影響 |
|---|---|
| **例外沒有任何進資料庫的機制**。唯一持久化去處是 NLog 檔案（`C:\temp\Logs\MyProject.Web\*.log`，保留 30 天） | 這是全新子系統 |
| `AuditLog` 存在，但刻意只承擔「業務稽核軌跡」，沒有 Exception／StackTrace 欄位 | **不重用** `AuditLog` |
| 全專案 **75 個** `catch (Exception ex)`，多數在 Service 層 `LogError` 後回傳 `VerifyRecordResult`，**例外不再往上拋** | 只掛「未處理例外」的做法（error boundary、API filter）會漏掉主戰場 |
| 專案**沒有任何** `IHostedService` / `BackgroundService` | 「背景作業」欄位今天無來源 |
| 專案**沒有任何**按鈕追蹤機制 | 「按了哪顆按鈕」無現成來源 |
| `LoggingErrorBoundary` 已記錄 Path／Account／UserId；`ApplicationCircuitHandler` 已追蹤 circuit 的 path／account／userId | 情境資訊有現成基礎可接 |

---

## 二、已確認的決策

| 決策點 | 結論 | 理由 |
|---|---|---|
| **合併鍵** | 例外類型 ＋ 例外訊息 ＋ 頁面 ＋ 操作 | 保留「在哪一頁、哪個動作出錯」的診斷價值；同頁不同按鈕的同名錯誤會分開 |
| **統計期間** | 從首次發生起一直累加，永不重置 | 與 Sentry／Application Insights 一致，最單純 |
| **「按了哪顆按鈕」** | 用**既有日誌訊息樣板**當「操作」 | 零侵入。`Failed to create category.` 實質就是「他按了新增」 |
| **「背景作業」** | 改成「**來源**」欄位（畫面／WebAPI／系統啟動／背景作業） | 現在能填前三種，日後真有背景作業不用改 schema |
| **捕捉機制** | 自訂 `ILoggerProvider` | 掛在 `ILogger` 管線，75 個 catch 零修改，日後新寫的 catch 也自動被收 |
| **使用者／頁面** | 允許修改 `ApplicationCircuitHandler` | 用 `CreateInboundActivityHandler`（.NET 8+ 官方 API）設定 ambient context |
| **堆疊儲存位置** | **檔案系統**，路徑由 `appsettings.json` 定義 | 使用者在看過「DB 只會佔 100 KB～幾 MB」的數字後仍選擇檔案，已確認為其決定 |
| **列數失控防護** | 總列數上限 5000 | 避免帶參數的例外訊息塞爆資料庫 |
| **清除** | 手動：刪單列、清空全部、清除 90 天未再發生；另可匯出 CSV | 使用者確認「不自動，只留手動按鈕」 |
| **預設排序** | 最後發生時間 desc | 使用者回報「剛剛怪怪的」時最需要看剛發生的；次數大的噪音不會永遠佔第一頁 |
| **寫入方式** | 有界 Channel ＋ `IHostedService` 消費者 | logger 只入列即返回，不阻塞使用者；序列化寫入自然避開並發 upsert 競爭 |

### 刻意不做（YAGNI）

- **不做 Web API controller**：內部管理員診斷頁，未被要求。
- **不動那 75 個 `catch` 區塊**。
- **不對例外訊息做遮罩**：本頁與「日誌檢視」同級（皆管理員專屬、皆看得到原始內容），與既有設計一致。
- **不做自動清除**、**不做保留天數設定鍵**（90 天為程式常數）。
- **不做告警／通知**。

---

## 三、資料模型

### 3.1 Entity `ExceptionLog`

放在 `MyProject.AccessDatas/Models/ExceptionLog.cs`，與既有 `AuditLog` 平行。

```csharp
/// <summary>
/// 系統例外紀錄：相同簽章的例外合併為一列並累加次數。
/// 完整堆疊存放於檔案系統（見 StackTraceFile），本表只保留診斷摘要。
/// </summary>
public class ExceptionLog
{
    public int Id { get; set; }

    /// <summary>合併鍵的 SHA-256（64 字元小寫十六進位）。唯一索引。</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>例外型別全名，例如 System.NullReferenceException。</summary>
    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>ex.Message，最多保留 1000 字元。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>來源：畫面／WebAPI／系統啟動／背景作業／未知。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>頁面或請求路徑（已去除查詢字串）。</summary>
    public string? Page { get; set; }

    /// <summary>操作＝日誌訊息「樣板」，例如 Failed to create category. Name={CategoryName}。</summary>
    public string? Operation { get; set; }

    /// <summary>記錄器名稱，例如 MyProject.Business.Services.DataAccess.CategoryService。</summary>
    public string? LoggerName { get; set; }

    /// <summary>首次遇到這個例外的使用者帳號（非姓名／Email）。</summary>
    public string? Account { get; set; }

    public int? UserId { get; set; }

    /// <summary>堆疊檔案相對路徑，例如 202609/ab12cd34ef567890.txt。寫檔失敗時為 null。</summary>
    public string? StackTraceFile { get; set; }

    public long OccurrenceCount { get; set; }

    public DateTime FirstOccurredAt { get; set; }
    public DateTime LastOccurredAt { get; set; }
}
```

### 3.2 索引（`BackendDBContext.OnModelCreating`）

```csharp
modelBuilder.Entity<ExceptionLog>(entity =>
{
    // 合併的唯一依據。並發寫入時由它兜底（寫入器已序列化，這是第二道防線）。
    entity.HasIndex(x => x.Signature).IsUnique();

    // 預設排序（最後發生 desc）與「清除 90 天未再發生」都吃這個索引。
    entity.HasIndex(x => x.LastOccurredAt);
});
```

### 3.3 Migration

`dotnet ef migrations add AddExceptionLog --project src/MyProject/MyProject.AccessDatas --startup-project src/MyProject/MyProject.Web`

⚠️ 新表無既有資料，不需要像 `AddCategoryTeamNameUniqueIndex` 那樣先清理資料。

### 3.4 簽章計算

```
Signature = SHA256_Hex( ExceptionType + "\n" + Message + "\n" + (Page ?? "") + "\n" + (Operation ?? "") )
```

⚠️ **`Operation` 必須是訊息「樣板」而非「算好的訊息」。**
`ILogger.Log<TState>` 的 `TState` 是 `FormattedLogValues`，其中帶有 `{OriginalFormat}` 這一項，取它才拿得到
`Failed to create category. Name={CategoryName}`。若改用 `formatter(state, exception)` 算出來的字串，
會得到 `Failed to create category. Name=技術文件` —— 每個分類名稱都變成一個新簽章，列數立刻爆掉。

---

## 四、捕捉管線

```
logger.LogError(ex, "Failed to create category. Name={CategoryName}", name)   ← 現有 75 處，零修改
        │
        ▼
ExceptionLogProvider : ILoggerProvider          （新，Singleton）
  └─ ExceptionLogLogger : ILogger
        │  收錄條件：logLevel >= Error 且 exception != null 且未被排除
        ▼
ExceptionContextAccessor                        （新，AsyncLocal）
        │  讀出 來源／頁面／帳號／UserId
        ▼
Channel<ExceptionLogEntry>（有界 1000，滿了丟棄）
        │
        ▼
ExceptionLogWriter : IHostedService              （新，專案第一個）
        │  單一消費者，序列化 upsert
        ▼
ExceptionLogService（Business 層）→ SQLite ＋ ExceptionStackFileStore → 檔案系統
```

### 4.1 收錄條件

**收**：`logLevel >= LogLevel.Error`（Error、Critical）**且** `exception != null`。

**不收**（對應使用者說的「少數刻意忽略的情況」）：

- `OperationCanceledException` 及其子型別（含 `TaskCanceledException`）—— 使用者取消、正常關機。
- 記錄器名稱屬於本子系統自己（`ExceptionLogWriter`、`ExceptionLogService`、`ExceptionStackFileStore`）—— 防遞迴。
- `AsyncLocal` 抑制旗標為 true 時（寫入器執行期間全程開啟）—— 防遞迴的第二道保險。

> **為什麼是 Error 以上**：`LogWarning(ex, …)` 多半是「已處理、降級可用」的情況（例如 `AuditLogService`
> 寫稽核失敗），收進來只會製造噪音。此門檻為程式常數，刻意不做成設定鍵（YAGNI）。

### 4.2 Ambient context

新增 `ExceptionContextAccessor`（Singleton，內部 `AsyncLocal<ExceptionContext?>`）：

```csharp
public sealed record ExceptionContext(string Source, string? Page, string? Account, int? UserId);
```

四個設定點：

| 路徑 | 設定位置 | Source |
|---|---|---|
| Blazor 互動 | `ApplicationCircuitHandler.CreateInboundActivityHandler`（**既有檔案，新增覆寫**） | `畫面` |
| HTTP／API | `ApplicationBuilderExtensions.UseHttpRequestLogging`（**既有 middleware，新增設定**） | 路徑以 `/api` 開頭 → `WebAPI`，否則 `畫面` |
| 系統啟動 | `Program.cs` 的 migrate／RBAC 回填區段外圍 | `系統啟動` |
| 其他 | 未設定時的預設值 | `未知` |

> `CreateInboundActivityHandler` 是 .NET 8 引入的官方 API，讓 `CircuitHandler` 包住**每一個** inbound
> circuit activity（按鈕點擊、表單送出、生命週期回呼）。這正是本場景需要的掛點：`AsyncLocal` 在此設定，
> 該次 activity 內所有 `LogError` 都讀得到。`ApplicationCircuitHandler` 已經在追 account／userId／path，
> 資料是現成的。

⚠️ **這是全站每一次互動都會經過的路徑**，實作後必須實跑驗證沒有效能或行為退化。

### 4.3 寫入器

`ExceptionLogWriter : BackgroundService`（`MyProject.Web/Diagnostics/`）：

- 從 `Channel` 讀取，每筆建立一個 DI scope 取得 `ExceptionLogService`。
- 全程以 `try/catch` 包住；自身失敗時**不得**經由 `ILogger` 回報（改用 `NLog.Common.InternalLogger` 或直接吞掉並計數），否則遞迴。
- `Channel` 建立為 `BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true }`。
  丟棄時累加一個計數器，並在停止時輸出總丟棄數。
  > 與 `nlog.config` 既有的 `AsyncWrapper overflowAction="Discard"` 同一種取捨：**寧可漏記，不可拖垮主流程**。

---

## 五、堆疊檔案

### 5.1 設定鍵（新增）

`appsettings.json` 的 `SystemSettings.ExternalFileSystem` 新增一項，與既有四個路徑並列：

```json
"ExternalFileSystem": {
  "DatabasePath": "C:\\temp\\MyProject\\DB",
  "DownloadPath": "C:\\temp\\MyProject\\Download",
  "UploadPath": "C:\\temp\\MyProject\\Upload",
  "ProjectFilePath": "C:\\temp\\MyProject\\ProjectFile",
  "ExceptionPath": "C:\\temp\\MyProject\\Exception"
}
```

對應 `MyProject.Models/Systems/SystemSettings.cs` 的 `ExternalFileSystem` 類別新增 `ExceptionPath` 屬性。

### 5.2 `ExceptionStackFileStore`

`MyProject.Business/Services/Other/ExceptionStackFileStore.cs`，**檔案生命週期集中在這一個類別**。

- 相對路徑：`{yyyyMM}/{Signature 前 16 碼}.txt`（年月目錄，比照附件的既有慣例）
- 內容：`ex.ToString()` 全文（含 InnerException 與堆疊），**不截斷**
- **只在首次建立資料列時寫一次**。重複發生只累加次數，不重寫檔案 → 寫檔成本與發生次數無關
- 寫檔失敗：**不影響資料列建立**，`StackTraceFile` 留 `null`
- 讀檔失敗／檔案不存在：UI 顯示「堆疊檔案不存在（可能已被清除）」
- 刪除：`DeleteAsync(relativePath)`、`DeleteAllAsync()`（清空整個 `ExceptionPath` 目錄內容）

⚠️ **三條刪除路徑都必須經過本類別**：刪單列、清空全部、清除 90 天未再發生。

> 沿革警告：專案已被檔案／DB 不同步咬過一次 —— 專案附件的「資料表紀錄由 EF Cascade 處理，
> 但**實體檔案仍須由 Service 層手動移除**」（見 [檔案上傳機制](../../features/檔案上傳機制.md)）。
> 本設計刻意把三條路徑收斂到單一入口，就是為了不重蹈覆轍。

---

## 六、服務層

`MyProject.Business/Services/DataAccess/ExceptionLogService.cs`，注入 `IDbContextFactory<BackendDBContext>`
（⚠️ **不得**注入 scoped `BackendDBContext`，由 `DataAccessServiceLifetimeTests` 守門）。

| 方法 | 說明 |
|---|---|
| `RecordAsync(ExceptionLogEntry)` | upsert：依 `Signature` 找 → 找到就 `OccurrenceCount++`、更新 `LastOccurredAt`；沒找到就新增並寫堆疊檔 |
| `GetAsync(ExceptionLogQuery)` | 伺服器端分頁查詢，回 `DataRequestResult<ExceptionLogAdapterModel>` |
| `GetStackTraceAsync(int id)` | 讀堆疊檔全文 |
| `DeleteAsync(int id)` | 刪列 ＋ 刪檔 |
| `ClearAllAsync()` | 清空資料表 ＋ 清空 `ExceptionPath` |
| `PurgeAsync(int days)` | 刪除 `LastOccurredAt` 早於 N 天前者 ＋ 刪其檔案 |

### 6.1 列數上限

常數 `MaxRows = 5000`。`RecordAsync` 新增列之前先 `CountAsync()`：
達上限時**不新增列**，改以固定哨兵簽章 `__OVERFLOW__` 累加一列「其他例外（已達列數上限）」，
並輸出一則警告（經 `InternalLogger`，不走 `ILogger`）。

### 6.2 分頁排序 ⚠️

`Skip`／`Take` 之前**必定**要有 `OrderBy`，且以 `ThenBy(x => x.Id)` 收尾
（見 [開發慣例與限制速查 §3.1](../../architecture/開發慣例與限制速查.md)）。預設：

```csharp
dataSource = sorted ?? dataSource.OrderByDescending(x => x.LastOccurredAt).ThenByDescending(x => x.Id);
```

測試的 `TestDbContextFactory` 會把漏排序升級為例外。

### 6.3 查詢型別

`ExceptionLogQuery`（比照 `LogQueryRequest` 的既有做法，**不**污染共用的 `DataRequest`）：

```csharp
public sealed class ExceptionLogQuery
{
    public DateTime? StartTime { get; set; }   // 比對 LastOccurredAt
    public DateTime? EndTime { get; set; }
    public string? Source { get; set; }
    public string? Account { get; set; }
    public string Keyword { get; set; } = string.Empty;  // 比對 類型／訊息／頁面／操作
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = MagicObjectHelper.PageSize;
    public string SortField { get; set; } = string.Empty;
    public bool? SortDescending { get; set; }
}
```

---

## 七、頁面

### 7.1 檔案

| 檔案 | 說明 |
|---|---|
| `Components/Pages/Admins/ExceptionLogPage.razor` | 路由薄殼 `@page "/system-exceptions"` |
| `Components/Views/Admins/ExceptionLogView.razor` | 檢視 UI |
| `Components/Views/Admins/ExceptionLogView.razor.cs` | Code-behind |
| `Components/Views/Admins/ExceptionLogView.razor.css` | CSS isolation |

### 7.2 權限（管理員專屬，**五方**一致）

比照既有 5 個管理員頁（`MyUserView`／`RoleViewView`／`LogViewerView`／`DatabaseUsageView`／`LogLevelSettingView`）：

1. `MagicObjectHelper` 新增 `角色_系統例外紀錄 = "系統例外紀錄"`，附 `<inheritdoc cref="角色_系統管理"/>`
2. `Menu.json` 在「系統管理」（id=3）下新增 **id=33**，`url` = `/system-exceptions`，`icon` = `bug_report`
3. `SidebarMenuService.MenuPermissionMap` 加 `[33] = MagicObjectHelper.角色_系統例外紀錄`
4. 檢視用 `CheckIsAdmin()`，**不得**出現 `CheckAccessPage(`
5. `MenuIconTests.AllowedIcons` 加 `bug_report`；`AdminOnlyPermissionTests.AdminOnlyPermissionKeys` 加該鍵；
   `MenuPermissionConsistencyTests.AdminOnlyViews` 加 `ExceptionLogView.razor.cs`

⚠️ **絕對不要**把權限鍵加進 `RolePermissionService.GetRoleListPermissionAllName()`
（由 `AdminOnlyPermissionTests` 擋下）。

### 7.3 版面

- 三段式：`RoleMessage` 非空 → 紅色 alert；否則顯示內容（與所有既有檢視一致）
- 工具列左側：起始時間／結束時間 `DatePicker`、來源 `Select`、使用者帳號 `Input`、關鍵字 `Input`
- 工具列右側 `ToolbarIconButton`（classic Material Icons，**不得用 emoji**）：

  | 功能 | Icon |
  |---|---|
  | 查詢 | `search` |
  | 重新整理 | `refresh` |
  | 匯出 CSV | `file_download` |
  | 清除 90 天未再發生 | `auto_delete` |
  | 清空全部 | `delete_sweep` |

  ⚠️ `auto_delete` 與 `delete_sweep` 須目視確認能渲染（工具列圖示**不受** `MenuIconTests` 保護）。
  渲染不出來就改用 `history` 與 `delete_forever`。

- 表格（AntDesign `Table`，伺服器端分頁排序，比照 `MyUserView`）：
  最後發生｜次數｜類型｜訊息｜來源｜頁面｜操作｜使用者｜首次發生｜操作欄（`CrudActionButton` 刪除）
- `ExpandTemplate`：`<pre>` 顯示完整堆疊（點擊時才讀檔）
- 兩個破壞性按鈕（清除、清空）以 `ModalService.ConfirmAsync` 二次確認
- 匯出 CSV 沿用既有的 `appFileDownload.downloadFromStream`，**加 UTF-8 BOM**（否則 Excel 開繁中亂碼）

---

## 八、設定與註冊

| 檔案 | 異動 |
|---|---|
| `appsettings.json` | `ExternalFileSystem.ExceptionPath`；`SystemVersion` Patch +1 |
| `MyProject.Models/Systems/SystemSettings.cs` | `ExternalFileSystem.ExceptionPath` 屬性 |
| `Extensions/ServiceCollectionExtensions.cs` | `AddScoped<ExceptionLogService>()`、`AddSingleton<ExceptionContextAccessor>()`、`AddScoped<ExceptionStackFileStore>()`、Channel 註冊 |
| `Program.cs` | `builder.Logging.AddProvider(...)`、`AddHostedService<ExceptionLogWriter>()`、啟動區段標記 `系統啟動`；目錄建立**重用既有的 `EnsureDirectoryExists(path, name)` 區域函式**（`Program.cs:438`），只需在其呼叫清單加一行 |
| `Components/_Imports.razor` | 不需改（`Views.Admins` 已匯入） |
| `MyProject.Business/Models/AutoMapping.cs` | `CreateMap<ExceptionLog, ExceptionLogAdapterModel>()` |

⚠️ `builder.Logging.ClearProviders()` 之後才 `UseNLog()`，本 provider 要加在 `UseNLog()` **之後**，
兩者並存、互不干擾（NLog 仍照常寫檔）。

---

## 九、測試

| 測試 | 驗什麼 |
|---|---|
| `ExceptionSignatureTests` | 相同類型＋訊息＋頁面＋操作 → 同簽章；任一不同 → 不同簽章；**訊息樣板而非算好的訊息** |
| `ExceptionLogServiceTests` | 首次新增寫檔；重複只累加次數且**不重寫檔**；刪列同時刪檔；清空全部清掉目錄；`PurgeAsync` 只刪超過天數者 |
| `ExceptionLogServiceTests`（上限） | 達 5000 列後不新增列，改累加哨兵列 |
| `ExceptionLogProviderTests` | Warning 不收；無例外不收；`OperationCanceledException` 不收；自身記錄器不收（防遞迴） |
| `ExceptionLogWriterTests` | Channel 滿載時丟棄不拋例外；寫入失敗不回流管線 |
| `DataAccessServiceLifetimeTests` | `ExceptionLogService` 加入 `DataAccessServices`，驗證注入工廠而非 scoped context |
| `AdminOnlyPermissionTests` | 新權限鍵不在角色矩陣 |
| `MenuPermissionConsistencyTests` | `AdminOnlyViews` 含 `ExceptionLogView.razor.cs`；Menu.json id 集合與 map 一致 |
| `MenuIconTests` | `bug_report` 在白名單 |
| `LoggingConventionTests` | 新程式碼的日誌訊息為英文 PascalCase 佔位、不含敏感欄位 |

---

## 十、文件

| 檔案 | 異動 |
|---|---|
| `docs/prd/系統例外紀錄-prd.md` | **新增** |
| `docs/prd/README.md`、`docs/README.md` | 索引 |
| `docs/changelog/2026-09-16-系統例外紀錄.md` | **新增** |
| `docs/architecture/開發慣例與限制速查.md` | 新增一節：例外自動記錄管線與防遞迴紅線；版本標頭 |
| `docs/architecture/資料模型與資料庫.md` | 新增 `ExceptionLog` 資料表 |
| `docs/operations/日誌與設定檔說明.md` | `ExceptionPath` 設定鍵；本頁與 nlog 檔案的分工 |
| `docs/superpowers/specs/README.md` | 索引加入本文件 |

---

## 十一、風險與緩解

| 風險 | 緩解 |
|---|---|
| **遞迴**：寫入失敗 → 記錯誤 → 再寫入 | 三道防線：記錄器名稱短路、`AsyncLocal` 抑制旗標、寫入器內部不使用 `ILogger` |
| **拖慢主流程**：AI 主機逾時短時間重複數百次 | Channel 入列即返回；滿載丟棄；堆疊只寫一次 |
| **`CreateInboundActivityHandler` 影響全站互動** | 實作後必須實跑全站頁面驗證；handler 內部全程 try/catch，絕不讓它拋出 |
| **列數爆炸**：帶參數的例外訊息 | 5000 列上限 ＋ 哨兵彙總列 |
| **檔案／DB 不同步** | 三條刪除路徑收斂到 `ExceptionStackFileStore` 單一入口；讀不到檔案時 UI 友善降級 |
| **Migration 失敗導致啟動失敗** | 新表無既有資料，不需資料清理；`Program.cs` 啟動時 `Database.Migrate()` 本就無條件執行 |

---

## 十二、驗收

### 自動

```powershell
dotnet build src/MyProject/MyProject.slnx -v:minimal --no-incremental   # 0 警告（TreatWarningsAsErrors）
dotnet format src/MyProject/MyProject.slnx --verify-no-changes
dotnet test  src/MyProject/MyProject.slnx
pwsh scripts/Test-DocsEncoding.ps1
```

### 實跑

1. 以 `support` 登入 → 系統管理 →「系統例外紀錄」，頁面可開、非管理員被擋。
2. **人為製造例外**：在分類清單以重複名稱觸發寫入失敗，回到本頁確認出現一列，
   來源＝`畫面`、頁面＝`/categories`、操作＝`Failed to create category. Name={CategoryName}`、
   使用者＝`support`、次數＝1。
3. **重複同一個操作 3 次** → 仍是一列，次數變 3，`LastOccurredAt` 更新，**堆疊檔案的修改時間不變**。
4. 展開列可看到完整堆疊；到 `ExceptionPath` 確認檔案存在且內容完整。
5. 刪除該列 → 檔案一併消失。
6. 「清空全部」→ 資料表與目錄皆空。
7. 匯出 CSV → Excel 開啟繁中不亂碼。
8. **全站回歸**：因為動到 `ApplicationCircuitHandler`，逐頁點過專案／分類／團隊／使用者／角色／日誌檢視，
   確認互動正常、無 console 錯誤、無效能退化。
9. 400px 窄螢幕下工具列換行正常、表格可水平捲動。

---

## 十三、實作順序建議

1. Entity ＋ `BackendDBContext` ＋ migration → 驗證 `dotnet ef` 產出且啟動不失敗
2. `SystemSettings.ExceptionPath` ＋ `ExceptionStackFileStore` ＋ 其測試
3. `ExceptionLogService`（含 upsert、上限、purge）＋ 其測試
4. `ExceptionContextAccessor` ＋ `ExceptionLogProvider` ＋ 其測試（此時尚未接上 circuit）
5. `ExceptionLogWriter` ＋ Channel ＋ 註冊 → 此時已能記錄，但使用者／頁面為「未知」
6. `ApplicationCircuitHandler` ＋ `UseHttpRequestLogging` ＋ `Program.cs` 接上 ambient context → **全站回歸**
7. 頁面 ＋ 選單 ＋ 權限五方 ＋ 守門測試
8. 文件 ＋ 版本號
