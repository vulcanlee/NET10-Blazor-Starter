# 團隊清單 PRD

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：0.4.42
- 首次實作版本：0.3.0
- 最後核對日期：2026/08/26

## 一、目標與範圍

提供「團隊（Team）」主資料的維護能力，讓具權限的管理者在 `/teams` 頁面完成團隊的查詢、新增、修改、刪除。團隊為獨立主資料，無外鍵關聯；`Name` 唯一（不分大小寫），`Code` 為選填、有填則須唯一。亦可透過 `GetAllEnabledNamesAsync()` 供其他頁面下拉選用啟用中的團隊名稱。

非範圍：
- 不做團隊成員關聯、階層或組織圖（純平面清單）。
- 不做與其他實體的外鍵關聯或參照完整性檢查（刪除前無被引用檢查，`BeforeDeleteCheckAsync` 直接回成功）。
- 不做匯入／匯出、批次操作、軟刪除（刪除為實體刪除）。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/teams`（`TeamPage.razor`，`MainLayout`） |
| 選單路徑 | 資料定義（id=5）> 團隊清單（`url=/teams`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[52] = 角色_團隊清單` |
| UI 頁面權限 | 頁面鍵「團隊清單」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `團隊清單:view` / `團隊清單:create` / `團隊清單:edit` / `團隊清單:delete` |
| 主要使用者 | 具「團隊清單」角色權限的後台管理者；系統管理員無條件可存取 |

## 三、畫面與欄位

單頁清單 + Modal 表單（`TeamViewView`）：

- 搜尋：關鍵字比對 `Name`、`Code` 或 `Description`（`Contains`）。
- 排序：可排序欄位 `Name`、`Code`、`IsEnabled`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`，`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：名稱、代號、描述、啟用狀態、更新時間、操作（修改／刪除）。
- 新增／編輯表單欄位：
  - 名稱 `Name`（必填，最長 100）
  - 代號 `Code`（選填，最長 50，有填須唯一）
  - 描述 `Description`（選填，最長 2000）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）
- 刪除：二次確認，提示不可復原。

## 四、內部系統運作

- UI 路徑：`TeamViewView` →（注入）`TeamService` → `BackendDBContext`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`TeamController` → `TeamRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `Team`（`Id/Name/Code/Description/IsEnabled/CreatedAt/UpdatedAt`），DbSet 為 `context.Team`。
- 查詢一律 `AsNoTracking()`；每個方法以 `IDbContextFactory<BackendDBContext>` 建立獨立 context、用完即棄（0.4.36 起，不再需要清追蹤）。
- 編輯前於 UI 以 `Clone()` 複製記錄；`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified/Deleted` 提交。
- 模型變更需在 `MyProject.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`團隊清單:view`、`團隊清單:create`、`團隊清單:edit`、`團隊清單:delete`。裸鍵「團隊清單」代表該頁全部動作（向後相容）。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 用 Cookie 驗證並以 `CheckAccessPage`（頁面鍵）控制進入頁面，API 用 JWT Bearer 並以動作鍵控制個別操作。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（先以 `NameNormalizer` 去除前後空白，再 `ToLower()` 不分大小寫，修改時排除自身），重複回「團隊名稱已存在」。API 端另以 `ExistsByNameAsync` 回 409 Conflict，判定語意與 UI 路徑一致。
- 代號重複：僅在 `Code` 非空白時檢查唯一（`ExistsByCodeAsync`），重複回「團隊代號已存在」/409；空白代號可重複（見測試 `WithEmptyCode...`）。
- 名稱／代號正規化與唯一索引（0.4.41 起）：寫入前一律經 `NameNormalizer` 處理（掛在 AutoMapper 的「→ Entity」映射上），`Name` 去除前後空白、**`Code` 空白一律存為 `null`**。資料庫另有 `IX_Team_Name` 與 `IX_Team_Code` 唯一索引兜底；SQLite 視 NULL 互不相等，因此多筆「未填代號」的團隊仍可共存。並發時由索引擋下，訊息經 `UniqueConstraintHelper` 轉譯為「團隊名稱／代號已存在，無法儲存。」。UI 會檢查 `AddAsync` / `UpdateAsync` 的回傳值後才顯示成功。
  - ⚠️ 沿革：0.4.41 之前是「檢查時 Trim、寫入時不 Trim」，「研發部 」（尾隨空白）會原樣入庫，之後「研發部」再也比不到它。`Code` 則可能混雜 `NULL`／`""`／`"   "` 三種「未填」表示法。
  - ⚠️ 團隊名稱的唯一性不只是清單好看：`Category.Teams` 與 `Project.Teams` 以**團隊名稱字串**做精確比對，一旦出現只差空白的兩個團隊，資料可見性會靜默出錯。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的團隊資料」；API 回 404 NotFound。
- 驗證失敗：`DataAnnotations`（名稱必填、各欄長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service try/catch 回 `VerifyRecordResult(false, ...)`；API 以 `ApiServerError` 回 500。

## 七、驗收與測試

對應測試檔 `src/MyProject/MyProject.Tests/TeamServiceTests.cs`：

- `BeforeAddCheckAsync_WithUniqueNameAndCode_ShouldSucceed`：名稱與代號皆唯一可新增。
- `BeforeAddCheckAsync_WithDuplicateName_ShouldFail`：名稱重複被拒。
- `BeforeAddCheckAsync_WithDuplicateCode_ShouldFail`：代號重複被拒。
- `BeforeAddCheckAsync_WithEmptyCode_ShouldSucceedEvenIfAnotherEmptyCodeExists`：空代號不觸發唯一檢查。
- `BeforeAddCheckAsync_WithDuplicateCodeDifferentCase_ShouldFail`：代號比對同樣不分大小寫。
- `AddAsync_WithUntrimmedNameAndCode_ShouldPersistTrimmedValues`：寫入前正規化。
- `AddAsync_WithBlankCode_ShouldPersistNull` / `AddAsync_TwoTeamsWithoutCode_ShouldBothSucceed`：空白代號歸一成 `null`，多筆未填代號可共存。
- `BeforeAddCheckAsync_AfterAddingUntrimmedName_ShouldRejectTrimmedName`：0.4.41 修正的破口重現。
- `AddAsync_WithDuplicateName_ShouldReturnFriendlyMessage` / `AddAsync_WithDuplicateCode_ShouldReturnFriendlyMessage`：唯一索引兜底與訊息轉譯。
- 另見 `CategoryTeamRepositoryUniquenessTests.cs`（API 路徑的判定語意）與 `CategoryTeamUniqueIndexMigrationTests.cs`（migration 資料清理）。
- `BeforeUpdateCheckAsync_WithSameRecord_ShouldSucceed`：同一筆用原名／原代號可通過。
- `BeforeUpdateCheckAsync_WithCodeUsedByOtherRecord_ShouldFail`：代號被他筆占用被拒。
- `AddAsync_ShouldPersistTeam`：新增後可查回並保留代號與啟用狀態。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Components/Pages/Teams/TeamPage.razor`
- `src/MyProject/MyProject.Web/Components/Views/Teams/TeamViewView.razor.cs`（頁面權限檢查）
- `src/MyProject/MyProject.Web/Controllers/TeamController.cs`（`[HasPermission]` 動作鍵）
- `src/MyProject/MyProject.Business/Services/DataAccess/TeamService.cs`（AddAsync / 前置檢查含代號唯一）
- `src/MyProject/MyProject.AccessDatas/Models/Team.cs`（Entity 欄位）
- `src/MyProject/MyProject.Dtos/Models/TeamCreateUpdateDto.cs`、`src/MyProject/MyProject.Dtos/Commons/TeamSearchRequestDto.cs`
- `src/MyProject/MyProject.Share/Helpers/MagicObjectHelper.cs`、`src/MyProject/MyProject.Share/Helpers/PermissionKeys.cs`
- `src/MyProject/MyProject.Web/Components/Layout/SidebarMenuService.cs`、`src/MyProject/MyProject.Web/Datas/Menu.json`
- `src/MyProject/MyProject.Tests/TeamServiceTests.cs`
- 交叉連結：[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../superpowers/specs/2026-06-22-category-team-pages-design.md](../superpowers/specs/2026-06-22-category-team-pages-design.md)、[../prd/紀錄分類與團隊權控-prd.md](../prd/紀錄分類與團隊權控-prd.md)
