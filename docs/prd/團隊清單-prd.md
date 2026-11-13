# 團隊清單 PRD

- 文件版本：1.17
- 文件狀態：已實作
- 現行系統版本：0.10.10
- 首次實作版本：0.3.0
- 最後核對日期：2026/11/13

## 一、目標與範圍

提供「團隊（Team）」主資料的維護能力，讓具權限的管理者在 `/teams` 頁面完成團隊的查詢、新增、修改、刪除。`Name` 唯一（不分大小寫），`Code` 為選填、有填則須唯一。亦可透過 `GetAllEnabledNamesAsync()` 供其他頁面下拉選用啟用中的團隊名稱。
0.9.105 起團隊可以設定「上層團隊」（`ParentId`）組成團隊樹：上層團隊的成員看得到所有下屬團隊的資料（路線圖 D-13；沿革見 [部門樹 changelog](../changelog/2026-10-04-部門樹.md)）。

使用者決定（2026/10/04）：改名時一併更新所有引用；有下屬團隊時不可刪除；公告／通知的團隊對象與資料可見範圍同一定義；Web API 補上團隊過濾並修正修改時的檢查；非管理員只能指派自己範圍內的團隊。

非範圍：
- 不做組織圖、多個上層（矩陣組織）、團隊主管欄位。
- 不做與其他實體的外鍵關聯或參照完整性檢查（刪除前無被引用檢查，`BeforeDeleteCheckAsync` 直接回成功）。
- 不做匯入、批次操作（0.9.107 起有匯出 Excel，見 §三）。（0.9.94 起刪除為軟刪除，可還原，見 §三。）

> 0.10.2 起（WASM 遷移 M3）本頁改在 **WebAssembly** 執行：畫面放在 `MyProject.Web.Client`，資料一律經 `/api/v1/Team`（`TeamController`）取得與存檔，Server 版頁面已刪除；畫面、欄位、規則與錯誤訊息不變。

## 二、使用者與入口

| 項目 | 內容 |
| --- | --- |
| 路由 | `/teams`（`MyProject.Web.Client` 的 `Pages/TeamPage.razor`，WASM 版外框 `ClientMainLayout`；0.10.2 起） |
| 選單路徑 | 資料定義（id=5）> 團隊清單（id=52 選單項，`url=/teams`） |
| 選單→權限對應 | `SidebarMenuService.MenuPermissionMap[52] = 角色_團隊清單` |
| UI 頁面權限 | 頁面鍵「團隊清單」（0.10.2 起 `PagePermissions.CanAccessPage`，使用者資料來自 `/me`；規則同 `PermissionRules`，管理員短路） |
| API 動作級權限 | `團隊清單:view` / `團隊清單:create` / `團隊清單:edit` / `團隊清單:delete` |
| 主要使用者 | 具「團隊清單」角色權限的後台管理者；系統管理員無條件可存取 |
| 操作說明 | 頂欄頁名旁的「操作說明」按鈕（0.9.66 起；WASM 版為 `ClientPageHelpDialog`）；內容為 `Datas/Help/teams.md`，於 `Datas/HelpTopics.json` 登記 `/teams` |

## 三、畫面與欄位

單頁清單 + Modal 表單（`TeamViewView`）：

- 工具列：新增（需 `團隊清單:create`）、重新整理、匯出 Excel（0.9.107 起；樹狀模式匯出全部並含「上層團隊」，見[通用匯出](通用匯出-prd.md)）；右側為關鍵字輸入、清空搜尋（有輸入時才出現）、搜尋。
- **團隊樹**（0.9.105 起）：沒有搜尋、不是「顯示已刪除」時，一次載入全部團隊在畫面上組樹（`TreeChildren`），下屬收在上層底下、可展開收合；分頁以最上層計，排序作用在最上層（預設依名稱），下屬一律依名稱。上層不存在（資料異常）的團隊當作最上層顯示。
- 搜尋：關鍵字比對 `Name`、`Code` 或 `Description`（`Contains`），結果為平面清單並多一欄「上層團隊」。
- 排序：可排序欄位 `Name`、`Code`、`IsEnabled`、`UpdatedAt`；平面清單預設以 `UpdatedAt` 遞減、再以 `Id` 遞減。
- 分頁：`PageSize` 取自 `MagicObjectHelper.PageSize`（8 筆）；平面清單由服務端分頁。
- 清單欄位：名稱、代號、描述、啟用狀態（`StatusPill` 徽章：啟用／停用；0.9.120 起有 `團隊清單:edit` 時點徽章直接切換，停用先確認，`TeamService.SetEnabledAsync` 只改狀態並換版本號、稽核 `Team.Enable`／`Team.Disable`；已是目標狀態時回成功、不寫稽核；已刪除或不存在時失敗；下屬團隊不連動；沒有修改權限或在「顯示已刪除」時為不可點的一般標籤）、更新時間、操作（修改需 `團隊清單:edit`、刪除需 `團隊清單:delete`，無權限時不顯示按鈕）。
- 新增／編輯表單（`form-modal` 大量資料輸入對話窗，見 [對話窗 UI 設計規範](../architecture/對話窗%20UI%20設計規範.md)；「團隊資料」一區）：
  - 名稱 `Name`（必填，最長 100）
  - 上層團隊 `ParentId`（0.9.105 起，選填＝最上層）：選項排除自己與自己的所有下屬（`TeamService.GetParentCandidatesAsync`）；修改時名稱欄下方提示「改名時，專案、分類與角色預設團隊裡的這個名稱會一起改。」
  - 代號 `Code`（選填，最長 50，有填須唯一）
  - 描述 `Description`（選填，最長 2000，獨占整行）
  - 啟用狀態 `IsEnabled`（Switch，預設啟用）
- 儲存流程（`SaveAsync`）依序為：表單驗證 → 修改模式下若沒有任何變更，提示「沒有任何變更，未進行儲存。」並關窗 →
  儲存確認「確定要儲存這筆紀錄嗎？」（「儲存」／「再檢查」）→ 名稱／代號重複檢查 → 寫入；寫入失敗會顯示原因且不關窗。團隊沒有「團隊確認」這一步（那是分類與專案才有）。
- 取消／✕／ESC：有未儲存變更時先詢問是否放棄（`FormDirtyTracker`）；點遮罩不會關窗。
- 刪除：`ConfirmDialog.AskSoftDeleteRecordAsync` 二次確認「確定要刪除這筆紀錄嗎？刪除後可在工具列的「顯示已刪除」中還原。」，並檢查結果。
- **刪除為軟刪除**（0.9.94 起）：刪除只標記 `IsDeleted`／`DeletedAt`／`DeletedBy`，所有查詢經全域過濾器自動排除；工具列「顯示已刪除」（需刪除權限）列出已刪除的團隊，可「還原」或「永久刪除」。還原時重新檢查唯一性，衝突就擋下並說明（名稱與代號：去空白、不分大小寫）。刪除與還原都會換新版本號（正在編輯的人存檔會得到衝突訊息）；刪除、還原、永久刪除都在伺服器端檢查團隊範圍。稽核：`*.Delete`（軟刪除）、`*.Restore`、`*.Purge`。Web API 的 `DELETE` 也是軟刪除，之後對它的 GET／PUT／DELETE 回 404；API 不提供還原與永久刪除。
- **自動永久刪除**（0.9.97 起）：刪除超過 `SoftDeleteSettings:PurgeAfterDays`（預設 90 天，`0`＝不自動）的團隊，由排程作業「已刪除資料清理」（`SoftDeletePurgeService`）永久刪除；系統層級清除，不看團隊範圍。每次有刪到時寫一筆彙總稽核 `Team.AutoPurge`（筆數、天數、觸發方式、`#Id 名稱` 清單）。使用者的直接綁定隨 Cascade 刪除。見 [排程作業 PRD](排程作業-prd.md)。
- 使用者與團隊的直接綁定（`UserTeam`）在軟刪除期間**保留**但不生效（`EffectiveTeamResolver` 經 `context.Team` Join 自動排除）；編輯使用者時也不會刪掉指向已刪除團隊的綁定（`RbacWriteService.SyncUserTeamsAsync` 只在有效團隊間計算差異），團隊還原後成員關係恢復；永久刪除時綁定隨 Cascade 刪除。⚠️ 角色的「預設團隊」是名稱字串、不比對團隊表，團隊被刪後仍會經由角色預設團隊取得（與 0.9.93 之前的實體刪除相同，刻意維持）。
- 唯一索引 `IX_Team_Name`、`IX_Team_Code` 為部分索引（`WHERE "IsDeleted" = 0`）。
- ⚠️ 停用或刪除團隊不會連動清掉 `Category.Teams`／`Project.Teams` 上已記錄的團隊名稱（兩者以團隊名稱字串比對）。
- **改名同步**（0.9.105 起，使用者決定）：同一個交易內把專案、分類的「團隊」與角色的「預設團隊」裡的舊名稱換成新名稱（都含已刪除的；不分大小寫、去空白比對），被改的列換版本號；稽核 detail 帶 `from=舊名稱; renamedProjects=N; renamedCategories=N; renamedRoles=N`。
  ⚠️ 不可改成**已刪除**團隊用過的名稱（那個團隊留下的資料會被這個團隊的人看到）：「已刪除的團隊用過「X」這個名稱…」。0.9.104 之前改名不同步，改名後原本的紀錄等於失去團隊限制。
- **團隊樹規則**（0.9.105 起，`TeamHierarchy`，畫面與 Web API 共用、在交易內檢查）：上層不可是自己、自己的下屬、已刪除或不存在的團隊（層級上限 32）；**有未刪除的下屬時不可刪除**（「這個團隊底下還有 N 個下屬團隊…」）；還原時上層仍是已刪除就擋下；仍被當作上層（含已刪除的下屬）時不可永久刪除，排程清理由最深層開始，同一次就能清掉整串。
  停用的上層照樣展開下屬（「啟用」只影響下拉選項，不影響權限，與之前相同）。
- **異動歷程**（0.9.173 起）：每列操作欄最後一顆「異動歷程」（`AuditHistoryButton`，圖示 `history`），一般清單與「顯示已刪除」都有；**只有管理員看得到**（有本頁權限的一般使用者也看不到；權限鍵不上架角色矩陣）。按下開「異動歷程：<名稱>」窗（`AuditHistoryDialog.ShowAsync("Team", Id, 名稱)`，標題顯示團隊名稱（`Name`）），左側列出這一筆的異動（排除換頁、搜尋、查看等瀏覽類，每頁 10 筆），右側顯示點選那一筆的摘要與修改前後差異；打開寫 `Audit.Search`、點一筆寫 `Audit.View`（摘要含 `via=history`）。目標類型 `Team` 必須與服務寫稽核的 `TargetType` 相同。詳見 [稽核紀錄 PRD §3.4](稽核紀錄-prd.md)。

## 四、內部系統運作

- UI 路徑（0.10.2 起，WASM）：`TeamViewView` → `TeamApi`（`MyProject.Web.Client/Api`，經 `ApiClient` 帶 Bearer Token）→ `TeamController`。DTO 與畫面模型的轉換集中在 `TeamDtoMapper`（WASM 頁面與 Controller 共用）。
- API 路徑：`TeamController` → `TeamService` → `BackendDBContext`（0.10.2 起與畫面同一個服務：名稱與代號規則、團隊樹規則、並行衝突、軟刪除與稽核只有一份；`TeamRepository` 已刪除），回傳 `ApiResult<T>` / `PagedResult<T>`。
  端點：`GET {id}`（寫查看稽核）、`GET all`（全部團隊，樹狀顯示用）、`GET {id}/parent-candidates`（可選的上層：排除自己與下屬，新增時 id 傳 0）、`POST search`（`Deleted` 顯示已刪除需刪除權限，否則 403；`RecordSearch` 為 true 時寫搜尋稽核）、`POST check`（存檔前名稱與代號檢查）、`POST`、`PUT {id}`、`PUT {id}/enabled`、`DELETE {id}`（軟刪除）、`POST {id}/restore`、`DELETE {id}/purge`、`POST export`。完整清單見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)。
- Entity `Team`（`Id/Name/Code/Description/ParentId/IsEnabled/CreatedAt/UpdatedAt`，另有樂觀並行 `ConcurrencyStamp` 與軟刪除 `IsDeleted/DeletedAt/DeletedBy`），DbSet 為 `context.Team`。`ParentId` 自我參照外鍵（Restrict），沒有導覽屬性；Migration `AddTeamParent` 以單一 `ALTER TABLE … ADD COLUMN … REFERENCES` 加欄位（不重建資料表）。
- 團隊樹快取 `ITeamTreeCache`（singleton，60 秒；本行程的新增、修改、刪除、還原，含 Web API，立即失效），供有效團隊展開、反查與表單選項。
- 查詢一律 `AsNoTracking()`；每個方法以 `IDbContextFactory<BackendDBContext>` 建立獨立 context、用完即棄（0.4.36 起，不再需要清追蹤）。
- 編輯前於 UI 以 `Clone()` 複製記錄；`UpdateAsync` 保留原 `CreatedAt`、更新 `UpdatedAt`，以 `Entry(item).State = Modified` 提交；刪除以 `SoftDeleteHelper.MarkDeleted` 標記（軟刪除），永久刪除才 `Remove`。
- Web API 的 `POST`／`PUT` 接受 `parentId`。0.10.2 起狀態碼依服務回傳的 `VerifyRecordResult.Kind`：上層不合規則（自己、下屬、已刪除或不存在）、有下屬時刪除、改成已刪除團隊用過的名稱都回 400（與 0.10.1 之前經 `TeamRepository` 相同）。
- 團隊清單本身**不**套團隊可見性過濾：有權限者看得到全部團隊；`GetAllEnabledNamesAsync()` 回傳所有啟用中的團隊（供分類、專案、使用者、角色等頁面的團隊下拉）。
- 稽核（0.9.78 起，LOG-14）：新增、修改、刪除成功後各寫一筆 `Team.Create`／`Team.Update`／`Team.Delete`（代碼定義於 `AuditActions`，目標為 `Team`／Id），可在「稽核紀錄」頁查詢；點狀態標籤切換另寫 `Team.Enable`／`Team.Disable`（0.9.120 起），匯出寫 `Team.Export`（0.9.107 起）。
  由 `TeamService` 寫入（操作者取自 `CurrentUserService`，API 請求由 `ApiActorMiddleware` 依存取 Token 填入；內容 `name=團隊名稱`）；0.10.2 起 Web API 與畫面寫的稽核相同（之前由 `TeamController` 另寫，API 刪除不帶名稱）。查看（`GET {id}`）、搜尋（`RecordSearch`）、匯出的稽核由 `TeamController` 寫。寫入失敗（含唯一索引擋下）不留稽核。
- 模型變更需在 `MyProject.AccessDatas/Migrations/` 產生 SQLite migration（本專案只支援 SQLite）。

## 五、權限與安全

- API 一律 `[Authorize(JwtBearer)]`；每個動作以 `[HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.*)]` 做動作級授權。
- 權限鍵組合規則 `頁面:動作`（`PermissionKey.For`）：`團隊清單:view`、`團隊清單:create`、`團隊清單:edit`、`團隊清單:delete`。裸鍵「團隊清單」代表該頁全部動作（向後相容）。
- 無權限回 403，且維持 `ApiResult` 格式；系統管理員短路（不需個別權限）。
- UI 與 API 共用單一 RBAC 權威來源：UI 以 `CanAccessPage`（頁面鍵）控制進入頁面、以 `<RequirePermission>` 元件（0.9.110 起，規則同 `CheckAccessAction(角色_團隊清單, 動作)`）控制新增／修改／刪除按鈕是否顯示；0.10.2 起頁面在 WASM 執行，判斷經 `PagePermissions`（`/me` 回傳的權限），這只是畫面顯示，實際操作一律由 API 以 JWT Bearer 與動作鍵把關。
- 頁面被權限擋下時顯示「你沒有權限存取此頁面」，並寫一筆 `Permission.Denied` 稽核（目標 `Page`／`/teams`，0.9.78 起；0.10.2 起由伺服器在開頁紀錄 `POST /api/v1/shell/page-view` 時寫，內容 `via=WASM`）。

## 六、錯誤與邊界

- 名稱重複：新增／修改前以 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 比對（先以 `NameNormalizer` 去除前後空白，再 `ToLower()` 不分大小寫，修改時排除自身），重複回「團隊名稱已存在，無法新增／修改。」。API 端走同一個檢查，重複回 409 Conflict（0.10.2 起訊息與畫面相同）；修改時先確認資料存在，再比對名稱。
- 代號重複：僅在 `Code` 非空白時檢查唯一（以 `NameNormalizer.NormalizeOptional` 判斷，0.10.2 起 API 走同一個檢查），重複回「團隊代號已存在，無法新增／修改。」/409；空白代號可重複（見測試 `WithEmptyCode...`）。
- 名稱／代號正規化與唯一索引（0.4.41 起）：寫入前一律經 `NameNormalizer` 處理（掛在 AutoMapper 的「→ Entity」映射上），`Name` 去除前後空白、**`Code` 空白一律存為 `null`**。資料庫另有 `IX_Team_Name` 與 `IX_Team_Code` 唯一索引兜底；SQLite 視 NULL 互不相等，因此多筆「未填代號」的團隊仍可共存。並發時由索引擋下，訊息經 `UniqueConstraintHelper` 轉譯為「團隊名稱／代號已存在，無法儲存。」。UI 會檢查 `AddAsync` / `UpdateAsync` 的回傳值後才顯示成功。
  名稱／代號重複屬使用者錯誤（0.9.78 起，LOG-11）：只記 `Information`、不帶例外物件，不會進「系統例外紀錄」。
  - ⚠️ 沿革：0.4.41 之前是「檢查時 Trim、寫入時不 Trim」，「研發部 」（尾隨空白）會原樣入庫，之後「研發部」再也比不到它。`Code` 則可能混雜 `NULL`／`""`／`"   "` 三種「未填」表示法。
  - ⚠️ 團隊名稱的唯一性不只是清單好看：`Category.Teams` 與 `Project.Teams` 以**團隊名稱字串**做精確比對，一旦出現只差空白的兩個團隊，資料可見性會靜默出錯。
- 找不到資料：修改／刪除時查無記錄回「找不到要修改／刪除的團隊資料」；API 回 404 NotFound（`VerifyRecordResult.Kind` 決定狀態碼，見 [Web API 設計慣例](../architecture/Web%20API%20設計慣例.md)）。
- 驗證失敗：`DataAnnotations`（名稱必填、各欄長度上限）由 `EditContext.Validate()` 於 Modal 攔截並逐條通知。
- 路由 ID 與 Payload ID 不一致：API `Update` 回 400 ValidationError。
- 例外：Service try/catch 記 `Error` 並回 `VerifyRecordResult(false, "新增／修改／刪除團隊失敗。")`；API 以 `ApiServerError` 回 500。畫面上未預期的例外由 `FormModalFlow`／刪除流程的 try/catch 攔下，顯示通用錯誤訊息，不會讓整頁出錯。
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
- 另見 `CategoryTeamApiTests.cs`（0.10.2 起，API 路徑：完整生命週期與稽核、名稱或代號重複 409、團隊樹與上層候選、改名同步分類、樹狀匯出含上層名稱且依啟用篩選、只能看的角色寫入 403；取代已刪除的 `CategoryTeamRepositoryUniquenessTests`）與 `CategoryTeamUniqueIndexMigrationTests.cs`（migration 資料清理）。
- `BeforeUpdateCheckAsync_WithSameRecord_ShouldSucceed`：同一筆用原名／原代號可通過。
- `BeforeUpdateCheckAsync_WithCodeUsedByOtherRecord_ShouldFail`：代號被他筆占用被拒。
- `AddAsync_ShouldPersistTeam`：新增後可查回並保留代號與啟用狀態。
- `AddUpdateDelete_ShouldWriteAuditWithCurrentUser`：增刪改各寫一筆稽核，操作者為目前使用者（0.9.78）。
- `AddAsync_WhenRejected_ShouldNotWriteAudit`：被唯一索引擋下的寫入不留稽核（0.9.78）。
- `GetAsync_WithoutSortField_ShouldOrderByUpdatedAtDescending`、`GetAsync_WithUnknownSortField_ShouldFallBackToDefaultOrder`、`GetAsync_WithNullSortDescending_ShouldFallBackToDefaultOrder`：分頁一律有穩定排序（0.4.46）。

- `TeamTreeTests.cs`（0.9.105）：樹的展開與上層、資料循環不會無窮迴圈、快取存活與失效、上層不可是自己／下屬／已刪除、⭐ 並行互設上層不會成環、有下屬不可刪、還原與永久刪除規則、排程由深到淺清理、⭐ 改名同步（含已刪除列、換版本號、不動相似名稱）、不可改成已刪除團隊的名稱、失敗結果帶種類（0.10.2 起 API 依它回 400／404）；migration 保留既有團隊與成員在 `TeamTreeMigrationTests.cs`（0.9.116 起獨立成檔，衍生專案複刻時刪除）。

- `StatusToggleServiceTests.cs`（0.9.120 新增，與分類、使用者共用）：`Team_SetEnabled_ShouldOnlyChangeStatus_BumpStamp_AndAudit`（只改狀態並換版本號、寫稽核）、`Team_SetEnabled_WhenAlreadyInTargetState_ShouldSucceedWithoutAudit`（重複點不反轉、不寫稽核）、`Team_SetEnabled_ThenSavingAStaleEditForm_ShouldConflict`（停用後舊的修改窗存檔得到並行衝突）、`Team_SetEnabled_WhenDeletedOrMissing_ShouldFail`。

測試以 SQLite in-memory + `EnsureCreatedAsync` 建立隔離環境，透過 `AutoMapping` 設定 Mapper。

- `AuditHistoryConventionTests.DataPage_ShouldOfferAuditHistoryOnEveryRow`（0.9.173）：兩個分支各一顆「異動歷程」、頁尾一個對話窗、目標類型為 `Team`；元件行為見 `Components/AuditHistoryTests`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web.Client/Pages/TeamPage.razor`（0.10.2 起，WASM）
- `src/MyProject/MyProject.Web.Client/Views/Teams/TeamViewView.razor`
- `src/MyProject/MyProject.Web.Client/Views/Teams/TeamViewView.razor.cs`（頁面權限檢查）
- `src/MyProject/MyProject.Web.Client/Api/TeamApi.cs`、`CrudApi.cs`、`TeamDtoMapper.cs`（呼叫 API 與 DTO 轉換）
- `src/MyProject/MyProject.Web/Controllers/TeamController.cs`（`[HasPermission]` 動作鍵）
- `src/MyProject/MyProject.Business/Services/DataAccess/TeamService.cs`（AddAsync / 前置檢查含代號唯一 / 團隊樹規則）
- `src/MyProject/MyProject.Business/Helpers/TeamHierarchy.cs`（上層檢查、刪除規則、改名同步）、`Services/Other/TeamTree.cs`（`TeamTree`、`ITeamTreeCache`）
- `src/MyProject/MyProject.AccessDatas/Models/Team.cs`（Entity 欄位）
- `src/MyProject/MyProject.Dtos/Models/TeamCreateUpdateDto.cs`、`TeamDto.cs`、`TeamNodeDto.cs`、`src/MyProject/MyProject.Dtos/Commons/TeamSearchRequestDto.cs`
- `src/MyProject/MyProject.Share/Helpers/MagicObjectHelper.cs`、`src/MyProject/MyProject.Share/Helpers/PermissionKeys.cs`
- `src/MyProject/MyProject.Web/Shell/SidebarMenuService.cs`（0.10.10 起；之前在 `Components/Layout/`）、`src/MyProject/MyProject.Web/Datas/Menu.json`
- `src/MyProject/MyProject.Tests/TeamServiceTests.cs`、`TeamTreeTests.cs`、`TeamTreeMigrationTests.cs`、`StatusToggleServiceTests.cs`
- `src/MyProject/MyProject.Business/Helpers/NameNormalizer.cs`、`src/MyProject/MyProject.Business/Helpers/UniqueConstraintHelper.cs`
- `src/MyProject/MyProject.Business/Helpers/AuditActions.cs`（`Team.*` 稽核代碼：Create／Update／Enable／Disable／Delete／Restore／Purge／AutoPurge／Export）
- `src/MyProject/MyProject.Web/Datas/Help/teams.md`（頁面操作說明）
- 交叉連結：[../architecture/Web API 設計慣例.md](../architecture/Web%20API%20設計慣例.md)、[../architecture/資料模型與資料庫.md](../architecture/資料模型與資料庫.md)、[../prd/紀錄分類與團隊權控-prd.md](../prd/紀錄分類與團隊權控-prd.md)
