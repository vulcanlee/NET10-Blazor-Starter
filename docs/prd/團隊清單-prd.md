# 團隊清單 PRD

- 文件版本：1.5
- 文件狀態：已實作
- 現行系統版本：0.9.87
- 首次實作版本：0.3.0
- 最後核對日期：2026/10/03

## 一、目標與範圍

提供「團隊（Team）」主資料的維護能力，讓具權限的管理者在 `/teams` 頁面完成團隊的查詢、新增、修改、刪除。團隊為獨立主資料，無外鍵關聯；`Name` 唯一（不分大小寫），`Code` 為選填、有填則須唯一。亦可透過 `GetAllEnabledNamesAsync()` 供其他頁面下拉選用啟用中的團隊名稱。

非範圍：
- 不做團隊成員關聯、階層或組織圖（純平面清單）。
- 不做與其他實體的外鍵關聯或參照完整性檢查（刪除前無被引用檢查，`BeforeDeleteCheckAsync` 直接回成功）。
- 不做匯入／匯出、批次操作。（0.9.94 起刪除為軟刪除，可還原，見 §三。）

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/teams`（`TeamPage.razor`，`MainLayout`） |
| 選單路徑 | 資料定義（id=5）> 團隊清單（id=52 選單項，`url=/teams`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[52] = 角色_團隊清單` |
| UI 頁面權限 | 頁面鍵「團隊清單」（`AuthenticationStateHelper.CheckAccessPage`；管理員短路） |
| API 動作級權限 | `團隊清單:view` / `團隊清單:create` / `團隊清單:edit` / `團隊清單:delete` |
| 主要使用者 | 具「團隊清單」角色權限的後台管理者；系統管理員無條件可存取 |
| 操作說明 | 頂欄頁名旁的「操作說明」按鈕（0.9.66 起，`PageHelpDialog`）；內容為 `Datas/Help/teams.md`，於 `Datas/HelpTopics.json` 登記 `/teams` |

## 三、畫面與欄位

單頁清單 + Modal 表單（`TeamViewView`）：

- 工具列：新增（需 `團隊清單:create`）、重新整理；右側為關鍵字輸入、清空搜尋（有輸入時才出現）、搜尋。
- 搜尋：關鍵字比對 `Name`、`Code` 或 `Description`（`Contains`）。
- 排序：可排序欄位 `Name`、`Code`、`IsEnabled`、`UpdatedAt`；預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`（8 筆），`RemoteDataSource=true` 由服務端分頁。
- 清單欄位：名稱、代號、描述、啟用狀態（`StatusPill` 徽章：啟用／停用）、更新時間、操作（修改需 `團隊清單:edit`、刪除需 `團隊清單:delete`，無權限時不顯示按鈕）。
- 新增／編輯表單（`form-modal` 大量資料輸入對話窗，見 [對話窗 UI 設計規範](../architecture/對話窗%20UI%20設計規範.md)；「團隊資料」一區）：
  - 名稱 `Name`（必填，最長 100）
  - 代號 `Code`（選填，最長 50，有填須唯一）
  - 描述 `Description`（選填，最長 2000，獨占整行）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）
- 儲存流程（`SaveAsync`）依序為：表單驗證 → 修改模式下若沒有任何變更，提示「沒有任何變更，未進行儲存。」並關窗 →
  儲存確認「確定要儲存這筆記錄嗎？」（「儲存」／「再檢查」）→ 名稱／代號重複檢查 → 寫入；寫入失敗會顯示原因且不關窗。團隊沒有「團隊確認」這一步（那是分類與專案才有）。
- 取消／✕／ESC：有未儲存變更時先詢問是否放棄（`FormDirtyTracker`）；點遮罩不會關窗。
- 刪除：`ConfirmDialog.AskSoftDeleteRecordAsync` 二次確認「確定要刪除這筆紀錄嗎？刪除後可在工具列的「顯示已刪除」中還原。」，並檢查結果。
- **刪除為軟刪除**（0.9.94 起）：刪除只標記 `IsDeleted`／`DeletedAt`／`DeletedBy`，所有查詢經全域過濾器自動排除；工具列「顯示已刪除」（需刪除權限）列出已刪除的團隊，可「還原」或「永久刪除」。還原時重新檢查唯一性，衝突就擋下並說明（名稱與代號：去空白、不分大小寫）。刪除與還原都會換新版本號（正在編輯的人存檔會得到衝突訊息）；刪除、還原、永久刪除都在伺服器端檢查團隊範圍。稽核：`*.Delete`（軟刪除）、`*.Restore`、`*.Purge`。Web API 的 `DELETE` 也是軟刪除，之後對它的 GET／PUT／DELETE 回 404；API 不提供還原與永久刪除。
- 使用者與團隊的直接綁定（`UserTeam`）在軟刪除期間**保留**但不生效（`EffectiveTeamResolver` 經 `context.Team` Join 自動排除）；編輯使用者時也不會刪掉指向已刪除團隊的綁定（`RbacWriteService.SyncUserTeamsAsync` 只在有效團隊間計算差異），團隊還原後成員關係恢復；永久刪除時綁定隨 Cascade 刪除。⚠️ 角色的「預設團隊」是名稱字串、不比對團隊表，團隊被刪後仍會經由角色預設團隊取得（與 0.9.93 之前的實體刪除相同，刻意維持）。
- 唯一索引 `IX_Team_Name`、`IX_Team_Code` 為部分索引（`WHERE "IsDeleted" = 0`）。
- ⚠️ 停用或刪除團隊不會連動清掉 `Category.Teams`／`Project.Teams` 上已記錄的團隊名稱；團隊改名也不會同步更新它們（兩者以團隊名稱字串比對）。

## 四、內部系統運作

- UI 路徑：`TeamViewView` →（注入）`TeamService` → `BackendDBContext`（Blazor Server 直接呼叫服務，不經 HTTP）。
- API 路徑：`TeamController` → `TeamRepository` → `BackendDBContext`，回傳 `ApiResult<T>` / `PagedResult<T>`。
- Entity `Team`（`Id/Name/Code/Description/IsEnabled/CreatedAt/UpdatedAt`），DbSet 為 `context.Team`。
- 查詢一律 `AsNoTracking()`；每個方法以 `IDbContextFactory<BackendDBContext>` 建立獨立 context、用完即棄（0.4.36 起，不再需要清追蹤）。
- 編輯前於 UI 以 `Clone()` 複製記錄；`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified/Deleted` 提交。
- 團隊清單本身**不**套團隊可見性過濾：有權限者看得到全部團隊；`GetAllEnabledNamesAsync()` 回傳所有啟用中的團隊（供分類、專案、使用者、角色等頁面的團隊下拉）。
- 稽核（0.9.78 起，LOG-14）：新增、修改、刪除成功後各寫一筆 `Team.Create`／`Team.Update`／`Team.Delete`（代碼定義於 `AuditActions`，目標為 `Team`／Id），可在「稽核紀錄」頁查詢。
  畫面路徑由 `TeamService` 寫入（操作者取自 `CurrentUserService`，內容 `name=團隊名稱`），Web API 路徑由 `TeamController` 寫入（API 刪除不帶名稱）；寫入失敗（含唯一索引擋下）不留稽核。
- 模型變更需在 `MyProject.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`團隊清單:view`、`團隊清單:create`、`團隊清單:edit`、`團隊清單:delete`。裸鍵「團隊清單」代表該頁全部動作（向後相容）。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 用 Cookie 驗證並以 `CheckAccessPage`（頁面鍵）控制進入頁面、以 `CheckAccessAction(角色_團隊清單, 動作)` 控制新增／修改／刪除按鈕是否顯示；API 用 JWT Bearer 並以動作鍵控制個別操作。
- 頁面被權限擋下時顯示「你沒有權限存取此頁面」，並寫一筆 `Permission.Denied` 稽核（目標 `Page`／`/teams`，0.9.78 起）。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（先以 `NameNormalizer` 去除前後空白，再 `ToLower()` 不分大小寫，修改時排除自身），重複回「團隊名稱已存在，無法新增／修改。」。API 端另以 `ExistsByNameAsync` 回 409 Conflict，判定語意與 UI 路徑一致。
- 代號重複：僅在 `Code` 非空白時檢查唯一（UI 路徑以 `NameNormalizer.NormalizeOptional` 判斷、API 路徑為 `ExistsByCodeAsync`），重複回「團隊代號已存在，無法新增／修改。」/409；空白代號可重複（見測試 `WithEmptyCode...`）。
- 名稱／代號正規化與唯一索引（0.4.41 起）：寫入前一律經 `NameNormalizer` 處理（掛在 AutoMapper 的「→ Entity」映射上），`Name` 去除前後空白、**`Code` 空白一律存為 `null`**。資料庫另有 `IX_Team_Name` 與 `IX_Team_Code` 唯一索引兜底；SQLite 視 NULL 互不相等，因此多筆「未填代號」的團隊仍可共存。並發時由索引擋下，訊息經 `UniqueConstraintHelper` 轉譯為「團隊名稱／代號已存在，無法儲存。」。UI 會檢查 `AddAsync` / `UpdateAsync` 的回傳值後才顯示成功。
  名稱／代號重複屬使用者錯誤（0.9.78 起，LOG-11）：只記 `Information`、不帶例外物件，不會進「系統例外紀錄」。
  - ⚠️ 沿革：0.4.41 之前是「檢查時 Trim、寫入時不 Trim」，「研發部 」（尾隨空白）會原樣入庫，之後「研發部」再也比不到它。`Code` 則可能混雜 `NULL`／`""`／`"   "` 三種「未填」表示法。
  - ⚠️ 團隊名稱的唯一性不只是清單好看：`Category.Teams` 與 `Project.Teams` 以**團隊名稱字串**做精確比對，一旦出現只差空白的兩個團隊，資料可見性會靜默出錯。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的團隊資料」；API 回 404 NotFound。
- 驗證失敗：`DataAnnotations`（名稱必填、各欄長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service try/catch 記 `Error` 並回 `VerifyRecordResult(false, "新增／修改／刪除團隊失敗。")`；API 以 `ApiServerError` 回 500。畫面上未預期的例外由 `FormModalFlow`／刪除流程的 try/catch 攔下，顯示通用錯誤訊息，不會拆掉 Blazor circuit。
- **並行衝突**（0.9.93 起，樂觀並行）：開啟編輯後若別人先存檔或刪除了同一筆，存檔時回「這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。」，
  Modal 維持開啟、輸入不會遺失。API 的 `PUT` 必須帶 GET 取得的 `ConcurrencyStamp`：沒帶回 400、與資料庫不符回 409。
  屬使用者情境（LOG-11）：只記 `Information`，不進「系統例外紀錄」。

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
- `AddUpdateDelete_ShouldWriteAuditWithCurrentUser`：增刪改各寫一筆稽核，操作者為目前使用者（0.9.78）。
- `AddAsync_WhenRejected_ShouldNotWriteAudit`：被唯一索引擋下的寫入不留稽核（0.9.78）。
- `GetAsync_WithoutSortField_ShouldOrderByUpdatedAtDescending`、`GetAsync_WithUnknownSortField_ShouldFallBackToDefaultOrder`、`GetAsync_WithNullSortDescending_ShouldFallBackToDefaultOrder`：分頁一律有穩定排序（0.4.46）。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Components/Pages/Teams/TeamPage.razor`
- `src/MyProject/MyProject.Web/Components/Views/Teams/TeamViewView.razor`
- `src/MyProject/MyProject.Web/Components/Views/Teams/TeamViewView.razor.cs`（頁面權限檢查）
- `src/MyProject/MyProject.Web/Controllers/TeamController.cs`（`[HasPermission]` 動作鍵）
- `src/MyProject/MyProject.Business/Services/DataAccess/TeamService.cs`（AddAsync / 前置檢查含代號唯一）
- `src/MyProject/MyProject.AccessDatas/Models/Team.cs`（Entity 欄位）
- `src/MyProject/MyProject.Dtos/Models/TeamCreateUpdateDto.cs`、`src/MyProject/MyProject.Dtos/Commons/TeamSearchRequestDto.cs`
- `src/MyProject/MyProject.Share/Helpers/MagicObjectHelper.cs`、`src/MyProject/MyProject.Share/Helpers/PermissionKeys.cs`
- `src/MyProject/MyProject.Web/Components/Layout/SidebarMenuService.cs`、`src/MyProject/MyProject.Web/Datas/Menu.json`
- `src/MyProject/MyProject.Tests/TeamServiceTests.cs`
- `src/MyProject/MyProject.Business/Helpers/NameNormalizer.cs`、`src/MyProject/MyProject.Business/Helpers/UniqueConstraintHelper.cs`
- `src/MyProject/MyProject.Business/Helpers/AuditActions.cs`（`Team.Create／Update／Delete` 稽核代碼）
- `src/MyProject/MyProject.Web/Datas/Help/teams.md`（頁面操作說明）
- 交叉連結：[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../superpowers/specs/2026-06-22-category-team-pages-design.md](../superpowers/specs/2026-06-22-category-team-pages-design.md)、[../prd/紀錄分類與團隊權控-prd.md](../prd/紀錄分類與團隊權控-prd.md)
