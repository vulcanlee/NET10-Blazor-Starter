# 建立一個新 CRUD 操作網頁

- 文件版本：2.9
- 文件狀態：已實作
- 現行系統版本：0.10.18
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/11/14

> 0.9.110 起一行指令產生並登記完整的 CRUD 模組（路線圖 G-21）。不要再複製既有檢視 —— 0.4.27 的 emoji 回歸（六個檢視共 22 個按鈕）正是複製貼上造成的。
> 慣例與踩雷點見[開發慣例與限制速查](../architecture/開發慣例與限制速查.md)（§6.27 是產生器本身）。
> 0.10.2 起（WASM 遷移 M3）產生器**只產生 WASM 頁面**（放在 `MyProject.Web.Client`，資料經 Web API），沒有 Server 版選項；Web API 改走資料服務，不再產生 Repository。
> 衍生專案以前產生的 Server 版模組在 0.10.8 以前照常可用；0.10.9 起（WASM 遷移 M10）主機已移除 Server 互動模式，這類模組要改寫成 WASM 頁面才能使用（不另提供轉換指引）。WASM 頁面的規則見速查 §6.33。

---

## 1. 一行指令

```powershell
pwsh ./scripts/New-CrudModule.ps1 -Name Equipment -DisplayName 設備清單
```

- 先提交或暫存手上的變更：產生器預設要求工作目錄乾淨，讓它的異動可以單獨檢視（`-Force` 略過）。
- 跑完會列出寫入的檔案、每一項登記（「已登記」或「略過（已存在）」）與 migration 結果。
- 要先看產出長什麼樣子：加 `-Preview`，只寫到 `output/crud-modules/<Name>/`，不改專案。

## 2. 參數

| 參數 | 預設 | 說明 |
|---|---|---|
| `-Name` | （必填） | 實體名稱，PascalCase，例如 `Equipment` |
| `-DisplayName` | 同 `-Name` | 顯示名稱，同時是權限鍵（`MagicObjectHelper.角色_設備清單 = "設備清單"`），不可與既有頁面重複 |
| `-Plural` | `<Name>s` | 頁面與檢視的資料夾／命名空間 |
| `-Route` | `/<plural 小寫>` | 頁面路由；說明檔名同規則（`equipments.md`） |
| `-MenuGroupId` | `5`（資料定義） | 掛在 `Menu.json` 的哪個群組；非管理員專屬只能 `2`（專案管理）或 `5`，因為角色權限矩陣只有這兩組。管理員專屬掛在「系統管理」的子群組：`34`（帳號與權限）、`6`（監控與診斷）、`7`（AI 管理）、`8`（系統設定與維運）、`10`（管理助理，0.9.155 起）；**不可**用 `3`（系統管理那一層只放子群組，0.9.113 起產生器會擋下） |
| `-Icon` | `description` | 選單圖示（classic Material Icons），不在 `MenuIconTests` 允許清單時自動加入 |
| `-WithTeams` | 關 | 加上「團隊」欄位與團隊範圍：沒有團隊（公開）或與授權團隊有交集才看得到；非管理員只能指定自己範圍內的團隊 |
| `-AdminOnly` | 關 | 管理員專屬：權限鍵不進角色矩陣，檢視以 `Permissions.IsAdmin` 判斷（配合上列子群組，例如 `-MenuGroupId 8`） |
| `-SkipMigration` | 關 | 不自動產生 migration |
| `-Preview` | 關 | 只產生檔案到 `-OutputPath`，不登記、不產生 migration |
| `-OutputPath` | `output/crud-modules` | `-Preview` 的輸出目錄（相對 repo 根目錄），檔案寫在 `<OutputPath>/<Name>/` 底下 |
| `-Force` | 關 | 略過工作目錄檢查；之前產生過的檔案保留不覆蓋（用來重跑登記） |
| `-CheckAnchors` | 關 | 只檢查每一項登記都找得到錨點，不寫任何檔案、不產生 migration；`-Name`／`-Icon` 須是不存在的名稱（遇到「已存在」當作失敗）。品質關卡的「CRUD anchors」步驟用它（0.9.153 起） |

## 3. 產生了哪些檔案

樣板在 `scripts/crud-templates/`（行首 `#IF TEAMS`／`#IF ADMIN`／`#ELSE`／`#ENDIF` 控制條件區塊）。以 `-Name Equipment -DisplayName 設備清單` 為例：

| 檔案 | 內容 |
|---|---|
| `AccessDatas/Models/Equipment.cs` | 實體：名稱、描述、啟用（、團隊）；`IConcurrencyStamped`（樂觀並行）＋`ISoftDeletable`（刪除可還原） |
| `Models/AdapterModel/EquipmentAdapterModel.cs` | 畫面模型，`Clone()` |
| `Dtos/Models/EquipmentDto.cs`、`EquipmentCreateUpdateDto.cs`、`Dtos/Commons/EquipmentSearchRequestDto.cs` | Web API 的 DTO（PUT 必須帶 `concurrencyStamp`；0.10.2 起 `EquipmentDto` 含 `IsDeleted`／`DeletedAt`／`DeletedBy`，搜尋可帶 `Deleted`、`IsEnabled`、`RecordSearch`） |
| `Business/Services/DataAccess/EquipmentService.cs` | 資料服務（`IDbContextFactory`），規則與稽核只有這一份，0.10.2 起 Web API 也走它：清單（可篩啟用狀態）、已刪除清單、新增（存檔後回填 Id 與版本號）、修改（並行比對）、只改啟用狀態的 `SetEnabledAsync`（0.9.120 起：換版本號、已是目標狀態不寫稽核）、刪除、還原、永久刪除、名稱重複檢查、稽核；失敗以 `VerifyRecordResult.Kind` 標示種類 |
| `Web/Controllers/EquipmentController.cs` | 0.10.2 起走 `EquipmentService`：`GET {id}`、`POST search`、`POST check`（存檔前的名稱檢查）、`POST`、`PUT {id}`、`PUT {id}/enabled`、`DELETE {id}`（軟刪除）、`POST {id}/restore`、`DELETE {id}/purge`、`POST export`（Excel），`-WithTeams` 另有 `GET options`（團隊選項）；各自 `[HasPermission]`，回 `ApiResult`，失敗依種類回 404／409／400／500（`FromVerify`）；「顯示已刪除」要刪除權限，沒有回 403（`ForbidUnless`） |
| `Web.Client/Api/EquipmentApi.cs` | 0.10.2 起：WASM 的 API 用戶端（繼承 `CrudApi`）＋ `EquipmentDtoMapper`（DTO ↔ 畫面模型，Controller 也用同一份） |
| `Web.Client/Pages/EquipmentPage.razor` | `@page`，以 WebAssembly 執行（版面 `ClientMainLayout` 由 `ClientRoutes` 指定，它就是登入閘門；0.10.8 起頁面**不可**標 `[Authorize]`，見速查表 §5.1） |
| `Web.Client/Views/Equipments/EquipmentViewView.razor(.cs)` | 權限閘門（`isAccessChecked`；`Permissions.CanAccessPage`，管理員專屬用 `Permissions.IsAdmin`）、`<RequirePermission>` 依動作顯示按鈕、搜尋排序分頁、勾選多筆「刪除選取」（0.9.132 起，`BatchDelete`；`BatchDeleteConventionTests` 守門）、顯示已刪除／還原／永久刪除、匯出 Excel、`form-modal` 表單（未儲存確認、儲存確認）；「啟用狀態」欄的 `StatusPill` 有編輯權限時可點（0.9.120 起：停用先確認、啟用直接切換；沒有權限經 `<RequirePermission>` 的 `Fallback` 顯示不可點的標籤；顯示已刪除時不可點）；每列最後的「異動歷程」（0.9.173 起，`AuditHistoryButton`＋一頁一個 `AuditHistoryDialog`，目標類型＝模組名稱 `Equipment`，只有管理員看得到；`AuditHistoryConventionTests` 守門） |
| `Web/Datas/Help/equipments.md` | 操作說明初稿（固定七段，UTF-8 含 BOM） |
| `Tests/EquipmentServiceTests.cs` | 新增修改與稽核、名稱重複、並行衝突、切換啟用狀態（只改狀態、換版本號、稽核）、軟刪除／還原／永久刪除、還原撞名、搜尋（、團隊範圍） |
| `Tests/EquipmentApiTests.cs` | 0.10.2 起：經 Web API 走完整流程（新增、查看、修改、版本衝突、切換啟用、刪除、顯示已刪除、還原、永久刪除），每個動作的稽核只記一筆；重名 409（不分大小寫）；匯出 Excel；沒有權限時寫入回 403 |

工具列與表格容器用 `MyProject.Web.Client/wwwroot/theme.css`（0.10.10 起；以前在 `MyProject.Web/wwwroot`）的共用類別（`.view-toolbar`、`.view-table-wrap`），不另產生 `.razor.css`。

產出的 WASM 檢視已照下列兩條規則寫好，補欄位時要維持（速查 §6.33）：

- ⚠️ 日期時間欄位一律指定 `Format="@ClientCulture.DateTimeFormat"`：不指定時用的是瀏覽器內建的 zh-TW 格式（「上午9:00:00」），與伺服器（「上午 09:00:00」）不同。
- ⚠️ `isAccessChecked = true` 要等「決定勾選欄（`<Selection />`）要不要出現」的權限與選項都拿到之後才設：表格先畫出來、之後才加上 `<Selection />`，AntDesign 會在 `Selection.Dispose` 丟 `NullReferenceException`（頁面出現錯誤列）。

0.10.15 起另有兩條「畫面選項資料一致性」的規則（速查 §6.33 第 126～128 點），新增下拉選項時照做：

- ⚠️ 表單的選項（`-WithTeams` 的團隊）由 `LoadFormOptionsAsync()` 讀，`OnAddAsync`／`OnEditAsync` 第一行就呼叫、存檔失敗後再呼叫一次——別人可能剛新增或停用了選項；服務的 `BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 以 `SelectedOptionGuard` 擋下新選的、已停用的團隊（400）。自己加的下拉照這個寫法（`FormOptionsConventionTests` 守門）。
- 名稱欄位有 `<AsyncFieldCheck For="() => CurrentRecord.Name" Check="CheckNameAvailableAsync" SaveContext="editContext" />`：輸入後不必按儲存，就在欄位下方看到「名稱已存在」（呼叫 `POST check`，只認 409）。⚠️ `SaveContext` 不可拿掉（外層 `EditForm` 的 `Context`；`AntDesign.Form` 另有自己的 `EditContext`，沒給的話儲存不會等檢查）。

## 4. 自動登記了哪些地方

重跑不會重複插入（已存在就略過並列出）；找不到登記位置時停止並說明是哪一項。

| 位置 | 內容 |
|---|---|
| `BackendDBContext` | `DbSet`、名稱唯一索引（只約束未刪除的資料） |
| `AutoMapping` | 實體 ↔ 畫面模型／DTO（名稱正規化、`IgnoreSoftDeleteFields`、團隊字串轉換） |
| `ServiceCollectionExtensions` | 服務的 DI（0.10.2 起不再產生 Repository） |
| `MagicObjectHelper` | 權限鍵常數 |
| `AuditActions` | `Equipment.Create／Update／Enable／Disable／Delete／Restore／Purge／AutoPurge／Export`（`Enable`／`Disable` 0.9.120 起） |
| `Menu.json` | 指定群組的最後一項，id＝目前最大 id＋1 |
| `SidebarMenuService.MenuPermissionMap`（`MyProject.Web/Shell/SidebarMenuService.cs`，0.10.10 起；0.10.9 以前在 `Components/Layout/`） | 選單 id → 權限鍵 |
| `RolePermissionService`（一般）或 `AdminOnlyPermissionTests`＋`MenuPermissionConsistencyTests.AdminOnlyViews`（管理員專屬） | 角色權限矩陣；預設角色在下次啟動時自動加入新頁面 |
| `MenuPermissionConsistencyTests.ViewToMenuId`（一般） | 檢視檔與選單 id 的對照 |
| `HelpTopics.json` | 操作說明目錄 |
| `MyProject.Web.Client/Program.cs` | WASM 的 API 用戶端 `EquipmentApi`（0.10.2 起；取代原本 `_Imports.razor` 的檢視命名空間登記，頁面自己 `@using` 檢視） |
| `docs/guides/新手完整操作指南.md` | 使用手冊第六章最後加一節「6.N 顯示名稱 `/路由`」（樣板 `scripts/crud-manual-section.md.tmpl`，0.9.165 起），`UserManualCoverageTests` 要求每個有操作說明的路由都有手冊章節 |
| `SoftDeletePurgeService`、`SoftDeletePurgeJob` | 排程作業「已刪除資料清理」與它的稽核；服務裡有三處：清除順序 `EntityTypes`、只清一種的 switch、資料保留頁的統計（0.9.152 起產生器三處都登記） |
| `DataRetentionOverviewService`、`DataRetentionOverviewServiceTests`、`MaintenanceApiTests` | 「資料保留與清理」頁的「已刪除的…」一列、自訂清除與它的稽核（`<Name>.Purge`）；「前往」連到一般清單（產生的頁面不接 `?deleted=1`）（0.9.152 起）。`MaintenanceApiTests` 的資料保留種類清單 0.10.10 起才登記（0.10.6～0.10.9 產生新模組後這個測試會失敗） |
| `OptimisticConcurrencyTests`、`SoftDeleteUserRoleTests`、`SoftDeletePurgeTests` | 並行與軟刪除的實體清單、清理測試 |
| `DataAccessServiceLifetimeTests`、`AdapterModelCloneTests`、`ApiIntegrationTests`、`MenuIconTests` | 服務生命週期、Clone、API DI 解析、選單圖示 |

最後在隔離的暫存路徑執行 `dotnet build` 與 `dotnet ef migrations add Add<Name>`（不寫進 `C:\temp` 或正式路徑）。

## 5. 產生之後的檢查清單

1. [ ] **補上實際欄位**：實體、畫面模型、兩個 DTO、`AutoMapping`（有需要轉換時）、表單（短欄位自動兩欄，多行或多選加 `Class="form-field-full"`）、表格欄位、`ExportColumns`、服務的搜尋與排序、Controller 的 `SortableFields`、`EquipmentDtoMapper`（DTO ↔ 畫面模型）、測試。
2. [ ] **改了欄位就重新產生 migration**：刪掉產生器建立的 `Add<Name>` 再執行 `dotnet ef migrations add Add<Name>`（隔離路徑的做法見[開發指引](腳手架開發指引.md)），或另外加一個 migration。
3. [ ] **操作說明**：把 `Datas/Help/<路由>.md` 第二段（這個頁面在做什麼）與第四段（建議操作）改成實際的業務說明；改了按鈕或欄位要同步改第三段（[速查 §6.14](../architecture/開發慣例與限制速查.md)）。
   **使用手冊**：改寫產生器在《新手完整操作指南》第六章加的那一節（「為什麼需要它」、欄位表與按鈕表），並把畫面加進 3.2 全畫面總覽表（[使用手冊寫作規範](使用手冊寫作規範.md)）。
   **異動歷程**：類別改名或服務寫稽核的 `TargetType` 改了，`historyDialog.ShowAsync("…")` 的第一個參數要一起改，否則按鈕打開的歷程永遠是空的（0.9.173 起）。新增稽核動作時，瀏覽類動作的常數必須叫 `Search`／`View`，改了資料的動作值不可以 `.Search`／`.View` 結尾（產生器內建的動作已符合）（[速查 §6.6](../architecture/開發慣例與限制速查.md)）。
4. [ ] **被其他資料引用時**：在服務的刪除與永久刪除加上「仍被使用就不能刪」的檢查，並在 `SoftDeletePurgeService` 的這一行加上 `isInUse`。
5. [ ] **權限**：一般頁面在「角色管理」把新頁面的動作授予需要的角色（預設角色已自動加入）。
6. [ ] `pwsh ./scripts/Invoke-QualityGate.ps1` 全綠。
7. [ ] 實機：選單出現、可新增、修改、刪除、還原、永久刪除、匯出；沒有權限的人看不到按鈕、直接開網址會被擋、直接呼叫 API 回 403。
8. [ ] 文件：PRD、畫面字典、changelog、版本號 Patch +1。

## 6. 改了產生器或樣板之後

```powershell
pwsh ./scripts/Test-CrudGenerator.ps1            # 數分鐘；-KeepWorktree 保留工作目錄以便啟動網站實際操作；-SkipRealRun 略過實跑
```

在暫存的 git worktree（以目前工作目錄為準，含未提交的變更；不動你的 index）產生三個範例模組（一般、`-WithTeams`、`-AdminOnly`），
建置、格式檢查、全部測試、文件編碼；0.10.2 起再**實跑**：在 worktree 以 `Start-E2EHost.ps1` 啟動隔離的網站，用 Playwright 打開三個產生出來的 WASM 頁面，
各做一次新增、修改、刪除、顯示已刪除、還原（`scripts/e2e/crud-generated.mjs`）。實跑需要環境變數 `PLAYWRIGHT_CORE_DIR`（見 [`scripts/e2e/README.md`](../../scripts/e2e/README.md)），
沒有安裝 playwright-core 的機器加 `-SkipRealRun`；support 的暫時密碼只經環境變數 `E2E_SUPPORT_PASSWORD` 傳給子行程，不放在命令列。最後以 `-Force` 重跑確認沒有任何檔案再被改動。它不在品質關卡裡（太慢），**修改產生器、樣板或任何被登記的檔案的錨點之後必跑**。

⚠️ 登記是以既有程式碼為錨點插入的（共 25 項，0.10.10 起多了 `MaintenanceApiTests`；例如 `services.AddScoped<CategoryService>();`、`builder.Services.AddScoped<TeamApi>();` 之後）。改動這些錨點行時，同步更新 `New-CrudModule.ps1`。0.9.153 起**品質關卡會擋下**（「CRUD anchors」步驟以 `-CheckAnchors` 跑一般、`-WithTeams`、`-AdminOnly` 三種，約 1～2 秒），失敗訊息指名是哪一項登記；它只確認插得進去，產出的模組能不能建置、通過測試，仍要跑 `Test-CrudGenerator.ps1`。

## 7. 已知限制

- 範例欄位只有名稱、描述、啟用（、團隊），名稱全系統唯一（不分大小寫、只看未刪除的資料）。
- 一般頁面只能掛在「專案管理」或「資料定義」群組（角色權限矩陣只有這兩組）。
- 不產生子表、檔案上傳或外鍵關聯；需要時參考 `ProjectService`（附件）或 `TeamService`（自我參照）。
- 產生的模組不含 PRD 與畫面字典條目，請自行補上。
