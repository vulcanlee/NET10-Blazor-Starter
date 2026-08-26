# 分類清單 PRD

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：0.4.41
- 首次實作版本：0.3.0
- 最後核對日期：2026/08/26

## 一、目標與範圍

提供「分類（Category）」主資料的維護能力，讓具權限的管理者在 `/categories` 頁面完成分類的查詢、新增、修改、刪除。分類為獨立主資料，無外鍵關聯，`Name` 唯一（不分大小寫）；亦可透過 `GetAllEnabledNamesAsync()` 供其他頁面下拉選用啟用中的分類名稱。

0.4.40 起，分類可指定**適用團隊**（`Teams`，多值），用以限定哪些團隊看得到這個分類，避免使用者的分類下拉清單塞滿用不到的項目。詳細可見性規則見第八節。

非範圍：
- 不做分類的階層／樹狀結構（純平面清單）。
- 不做與其他實體的外鍵關聯或參照完整性檢查（刪除前無被引用檢查，`BeforeDeleteCheckAsync` 直接回成功）。
- 不做匯入／匯出、批次操作、軟刪除（刪除為實體刪除）。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/categories`（`CategoryPage.razor`，`MainLayout`） |
| 選單路徑 | 資料定義（id=5）> 分類清單（id=52 選單項，`url=/categories`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[51] = 角色_分類清單` |
| UI 頁面權限 | 頁面鍵「分類清單」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `分類清單:view` / `分類清單:create` / `分類清單:edit` / `分類清單:delete` |
| 主要使用者 | 具「分類清單」角色權限的後台管理者；系統管理員無條件可存取 |

（選單 `id` 與 `MenuPermissionMap` 索引皆為 51；`Menu.json` 中的顯示項 id 標為 52，url 對應同一頁。）

## 三、畫面與欄位

單頁清單 + Modal 表單（`CategoryViewView`）：

- 搜尋：關鍵字比對 `Name` 或 `Description`（`Contains`）。清空搜尋鈕在有輸入時出現。
- 排序：可排序欄位 `Name`、`IsEnabled`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`，`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：名稱、描述、適用團隊、啟用狀態（啟用／停用）、更新時間、操作（修改／刪除）。
- 新增／編輯表單欄位：
  - 名稱 `Name`（必填，最長 100）
  - 描述 `Description`（選填，最長 2000）
  - 適用團隊 `Teams`（多選，選填；不設定表示所有團隊皆可使用）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）
- 刪除：`ConfirmAsync` 二次確認，提示不可復原。
- 儲存前團隊確認（0.4.40 起；0.4.41 起改排在名稱重複檢查之後）：`ConfirmTeamBindingAsync` 會在兩種情況擇一提出警告，
  確認鈕為「仍要儲存」、取消鈕為「回去編輯」（取消時 Modal 保持開啟、表單內容不消失）：
  1. 完全未指定適用團隊 —— 這筆會成為所有人都看得到的公用分類。
  2. 指定的團隊與自己所屬團隊沒有交集 —— 存檔後自己就會在清單上看不到它。

## 四、內部系統運作

- UI 路徑：`CategoryViewView` →（注入）`CategoryService` → `BackendDBContext`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`CategoryController` → `CategoryRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `Category`（`Id/Name/Description/Teams/IsEnabled/CreatedAt/UpdatedAt`），DbSet 為 `context.Category`。`Teams` 以換行分隔字串儲存（`TagStringHelper`），AutoMapper 以 `ForMember` 在 `List<string>` 與字串間轉換。
- 查詢一律 `AsNoTracking()`；新增前 `CleanTrackingHelper.Clean<Category>` 清追蹤，寫入後再清一次。
- 編輯前於 UI 以 `CurrentRecord = model.Clone()` 複製，避免污染清單資料；`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified/Deleted` 提交。
- 模型變更需在 `MyProject.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`分類清單:view`、`分類清單:create`、`分類清單:edit`、`分類清單:delete`。裸鍵「分類清單」代表該頁全部動作（向後相容）。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 用 Cookie 驗證並以 `CheckAccessPage`（頁面鍵）控制進入頁面，API 用 JWT Bearer 並以動作鍵控制個別操作。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（先以 `NameNormalizer` 去除前後空白，再 `ToLower()` 不分大小寫，修改時排除自身），重複回「分類名稱已存在」。API 端另以 `ExistsByNameAsync` 回 409 Conflict，判定語意與 UI 路徑一致。
- 名稱正規化與唯一索引（0.4.41 起）：寫入前一律 `Trim()`（AutoMapper 的「→ Entity」映射上），資料庫另有 `IX_Category_Name` 唯一索引兜底。前置檢查與寫入不在同一個交易裡，並發時由索引擋下，訊息經 `UniqueConstraintHelper` 轉譯為「分類名稱已存在，無法儲存。」。UI 會檢查 `AddAsync` / `UpdateAsync` 的回傳值後才顯示成功。
  - ⚠️ 沿革：0.4.41 之前是「檢查時 Trim、寫入時不 Trim」，「技術文件 」（尾隨空白）會原樣入庫，之後「技術文件」再也比不到它，兩筆看起來一模一樣的資料同時存在。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的分類資料」；API 回 404 NotFound。
- 驗證失敗：`DataAnnotations`（名稱必填、長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service 以 try/catch 記錄並回 `VerifyRecordResult(false, ...)`；API 以 `ApiServerError` 回 500。

## 七、驗收與測試

對應測試檔 `src/MyProject/MyProject.Tests/CategoryServiceTests.cs`：

- `BeforeAddCheckAsync_WithUniqueName_ShouldSucceed`：唯一名稱可新增。
- `BeforeAddCheckAsync_WithDuplicateName_ShouldFail`：重複名稱被拒。
- `BeforeAddCheckAsync_WithDuplicateNameDifferentCase_ShouldFail`：大小寫不同仍視為重複。
- `BeforeUpdateCheckAsync_WithSameRecordSameName_ShouldSucceed`：同一筆用原名可通過。
- `BeforeUpdateCheckAsync_WithNameUsedByOtherRecord_ShouldFail`：名稱被他筆占用被拒。
- `AddAsync_ShouldPersistCategory`：新增後可查回並保留描述與啟用狀態。
- `AddAsync_WithUntrimmedName_ShouldPersistTrimmedName` / `AddAsync_WithFullWidthSpace_ShouldPersistTrimmedName`：寫入前正規化（含全形空白 U+3000）。
- `BeforeAddCheckAsync_AfterAddingUntrimmedName_ShouldRejectTrimmedName`：0.4.41 修正的破口重現。
- `AddAsync_WithDuplicateName_ShouldReturnFriendlyMessage`：略過前置檢查直接寫，驗證唯一索引兜底與訊息轉譯。

對應測試檔 `src/MyProject/MyProject.Tests/CategoryServiceTeamVisibilityTests.cs`（0.4.40 新增）：

- `GetAsync_Admin_ShouldSeeAllCategories`：管理員看到全部。
- `GetAsync_NonAdminWithoutTeams_ShouldSeeAllCategories`：**沒有團隊的使用者看到全部**（與紀錄相反的規則）。
- `GetAsync_NonAdminWithTeams_ShouldSeeOnlyPublicOrIntersectingCategories`：只看到公用分類與有交集的分類。
- `GetAllEnabledNamesAsync_NonAdminWithTeams_ShouldFilterByTeamAndSkipDisabled`：下拉清單同時受團隊與啟用狀態過濾。
- `GetById_NonAdmin_ShouldDenyCategoryOutsideTeamScope`：單筆守門回空模型。
- `BeforeAddCheckAsync_WithNameOfInvisibleCategory_ShouldStillFail`：名稱唯一性仍為全域比對。
- `AddAsync_ShouldRoundTripTeamsBetweenListAndStoredString` / `UpdateAsync_WithEmptyTeams_ShouldStoreNullAsPublicCategory`：`Teams` 的 List↔字串往返。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper；存取範圍以 `FakeRecordAccessScopeProvider` 替身指定。

## 八、分類的團隊可見性（0.4.40 起）

| 情境 | 結果 |
| --- | --- |
| 分類未指定適用團隊（`Teams` 為 null／空） | 公用分類，所有人可見 |
| 使用者為系統管理員 | 看得到全部分類 |
| 使用者未綁定任何團隊 | 看得到全部分類 |
| 使用者有團隊 | 看得到「公用分類」＋「適用團隊與自己所屬團隊有交集」的分類 |

- 「使用者的團隊」＝其**所有**所屬團隊的聯集，由 `IEffectiveTeamResolver` 計算（`UserTeam` ∪ 角色 `DefaultTeamsJson`），經 `IRecordAccessScopeProvider` 取得。系統沒有「目前團隊／切換團隊」的概念。
- 規則集中在 `CategoryService.ApplyTeamVisibility`（清單／下拉）與 `IsVisible`（單筆），共三個讀取入口。
- ⚠️ **「未綁團隊的使用者看得到全部」與紀錄（Project）的規則相反** —— 紀錄是「只看得到公開紀錄」。分類可見性是下拉清單的便利性過濾，不是安全邊界（安全邊界為 RBAC），故刻意採寬鬆規則。
- 名稱唯一性檢查**不**套團隊過濾，避免「看不到卻建得出同名分類」。
- 專案編輯畫面的分類下拉，會額外把「這筆專案已貼、但目前使用者看不到」的分類列出並加註「（已限定其他團隊）」，避免使用者一存檔就把它靜默清掉。
- Web API 路徑（`CategoryController` / `CategoryRepository`）維持既有分工，**不**做行級過濾。

## 九、相關程式與文件

- `src/MyProject/MyProject.Web/Components/Pages/Categories/CategoryPage.razor:1`
- `src/MyProject/MyProject.Web/Components/Views/Categories/CategoryViewView.razor:1`
- `src/MyProject/MyProject.Web/Components/Views/Categories/CategoryViewView.razor.cs:70`（頁面權限檢查）
- `src/MyProject/MyProject.Web/Controllers/CategoryController.cs:36`（`[HasPermission]` 動作鍵）
- `src/MyProject/MyProject.Business/Services/DataAccess/CategoryService.cs:113`（AddAsync / 前置檢查）
- `src/MyProject/MyProject.AccessDatas/Models/Category.cs:8`（Entity 欄位）
- `src/MyProject/MyProject.Dtos/Models/CategoryCreateUpdateDto.cs:9`、`src/MyProject/MyProject.Dtos/Commons/CategorySearchRequestDto.cs:6`
- `src/MyProject/MyProject.Share/Helpers/MagicObjectHelper.cs:37`、`src/MyProject/MyProject.Share/Helpers/PermissionKeys.cs:9`
- `src/MyProject/MyProject.Web/Components/Layout/SidebarMenuService.cs:27`、`src/MyProject/MyProject.Web/Datas/Menu.json:57`
- `src/MyProject/MyProject.Tests/CategoryServiceTests.cs:1`、`src/MyProject/MyProject.Tests/CategoryServiceTeamVisibilityTests.cs:1`
- `src/MyProject/MyProject.Tests/CategoryTeamRepositoryUniquenessTests.cs:1`、`src/MyProject/MyProject.Tests/CategoryTeamUniqueIndexMigrationTests.cs:1`
- `src/MyProject/MyProject.Business/Helpers/NameNormalizer.cs:1`、`src/MyProject/MyProject.Business/Helpers/UniqueConstraintHelper.cs:1`
- `src/MyProject/MyProject.Web/Components/Commons/TeamBindingConfirm.cs:1`（儲存前團隊確認對話窗，與專案編輯頁共用）
- 交叉連結：[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../superpowers/specs/2026-06-22-category-team-pages-design.md](../superpowers/specs/2026-06-22-category-team-pages-design.md)、[../prd/紀錄分類與團隊權控-prd.md](../prd/紀錄分類與團隊權控-prd.md)
