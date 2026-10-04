# 使用者管理 PRD

- 文件版本：1.11
- 文件狀態：已實作
- 現行系統版本：0.9.104
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/10/04

## 一、目標與範圍

提供管理員維護系統使用者帳號的完整能力：查詢、新增、修改、刪除（0.9.95 起為軟刪除，可還原），並在同一表單指派主要角色、額外角色（多角色，權限取聯集）與直接綁定的團隊。所有指派透過 RBAC 雙寫落地至關聯表，作為 UI 與 API 共用的權限來源。

- **範圍**：`/myusers` 使用者維護頁與 `MyUserService` CRUD、`GetUserAssignmentsAsync` 回填、`SyncAssignmentsAsync` 雙寫、稽核寫入。
- **非範圍**：登入、鎖定、改密碼與 Google 建帳見 [登入與帳號流程](登入與帳號流程-prd.md)；使用者自己改姓名見 [個人資料](個人資料-prd.md)（0.9.102 起；Email 仍只能在這一頁由管理員改）；角色本身與權限矩陣編輯見 [角色管理](角色管理-prd.md)；團隊清單維護見 `/teams`。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/myusers` | 系統管理 → 使用者管理（`Menu.json` id=31）| 僅管理員（`AuthenticationStateHelper.CheckIsAdmin`）| 系統管理員 |

> **0.4.33 起**：`使用者管理` 權限鍵刻意**不列入角色矩陣**（比照「統計與分析」群組）。
> 此前該鍵可被勾選卻永遠無效（頁面以 `CheckIsAdmin` 守門），屬於「死權限」。
> 由 `MyProject.Tests/AdminOnlyPermissionTests.cs` 守門，請勿補上。


- 頁面 `MyUserView` 初始化先執行 `Check`，未通過即導向登出；非管理員顯示「你沒有權限存取此頁面」並停止載入。
- 本頁為 Blazor 元件、無對應的 `MyUser` API 控制器；動作級 `[HasPermission("resource:action")]` 套用於業務資料 API（分類／團隊／專案），使用者維護僅以管理員身分閘控。

## 三、畫面與欄位

- **清單**：遠端分頁 `Table`，欄位 帳號、名稱、Email、角色（`RoleViewName`）、狀態、管理員、建立時間、更新時間，皆可排序；工具列含新增、重新整理、搜尋、清空搜尋。0.9.30 起「狀態」以 `StatusPill` 徽章呈現（啟用＝綠、停用＝灰）；「管理員」僅在為「是」時顯示徽章，為「否」時顯示「—」。搜尋比對帳號／名稱／Email／角色名稱。
  0.9.101 起：被登入鎖定的帳號在「狀態」多一個黃色「鎖定至 HH:mm」徽章（不是今天時顯示 MM-dd HH:mm），操作欄多一個「解鎖」（確認後解除，稽核 `User.Unlock`）。
  0.9.104 起：已開啟兩步驟驗證的帳號在「狀態」多一個「兩步驟驗證」徽章，操作欄（自己那一列除外）多「重設兩步驟驗證」（確認後清除驗證器設定與備用碼，見[兩步驟驗證](兩步驟驗證-prd.md)）。
- **維護表單**（Modal）欄位：
  - 帳號（必填，唯一）、密碼（新增必填；編輯留白＝沿用既有密碼；下方顯示密碼規則）、名稱（必填）、Email。
  - 「下次登入須變更密碼」核取方塊（0.9.101 起，`MustChangePassword`）：新增時預設勾選；編輯時輸入新密碼會自動勾選（仍可取消）；也可以不改密碼只勾選，要求對方下次登入先換密碼。
    ⚠️ 0.9.60 起 **Email 是「忘記密碼」寄信的收件者**：留空的帳號無法自助重設密碼（只記在稽核 `Password.ResetRequested` 的 `reason=InvalidEmail`）。0.9.85 起 `BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查格式（`MailAddress.TryCreate`，與 `PasswordResetService` 判斷能否寄信的規則相同）：有填但不合法時擋下並顯示「Email 格式不正確；不使用可留空。」。既有錯誤資料不自動修正（例如出貨預設 `BootstrapSettings:SupportEmail = "support"`），修改該帳號時須改正或清空。多個帳號可以共用同一個 Email，申請時每個帳號各收一封。
  - 角色（必填，單選主要角色 `RoleViewId`）。
  - 額外角色（多選，`AdditionalRoleIds`，與主要角色權限取聯集）。
  - 團隊（多選團隊名稱，直接綁定此使用者；不設定則沿用其角色的預設團隊）。**非必填**，但留空時儲存前會跳出確認（見 §六）。
  - 啟用（`Status`）、管理員（`IsAdmin`）核取方塊。
- 編輯前以 `Clone()` 複製當前列並載入既有指派回填；新增時預設帶入「預設角色」。

## 四、內部系統運作

View（`MyUserView`）→ `MyUserService` → `BackendDBContext`：

- **新增**（`AddAsync`）：0.9.101 起先以 `IPasswordPolicy.Check` 檢查規則，再由 `ApplyAsync` 雜湊並寫入設定時間、旗標與歷史；存檔後 `SyncAssignmentsAsync` 雙寫角色與團隊；寫 `User.Create` 稽核。
- **修改**（`UpdateAsync`）：以 `Entry(...).State = Modified` 更新；密碼留白時沿用既有 `Password`／`Salt`，否則重新雜湊；再 `SyncAssignmentsAsync`；寫 `User.Update` 稽核。
- **刪除**（`DeleteAsync`，0.9.95 起為軟刪除）：追蹤載入 → 禁止刪除 support（`BootstrapSettings.SupportAccount`，不分大小寫）與自己（`CurrentUser.Id`，0 時不比對）→ `SoftDeleteHelper.MarkDeleted` → 刪掉該使用者的密碼重設 token；`UserRole`／`UserTeam` 保留（還原後原樣回來）。畫面以 `CanDelete` 隱藏 support 與自己那一列的刪除鈕（伺服器端仍是權威）。
- **已刪除清單／還原／永久刪除**（`GetDeletedAsync`／`RestoreAsync`／`PurgeAsync`，0.9.95 起）：工具列「顯示已刪除」切換。還原時擋下三種衝突並說明：與有效使用者同帳號（完全比對，與新增檢查一致）、綁定的 GoogleId 已連到別人、主要角色已被刪除（主要角色為 null 允許）。永久刪除只能對已刪除的使用者，`IgnoreQueryFilters` 追蹤載入後 `Remove`，關聯由資料庫 Cascade 刪除（不可用 `ExecuteDelete`，它也套用過濾器而刪 0 筆）。刪除與還原都會換新版本號；登入、權限判斷、下拉選單都經全域過濾器而看不到已刪除的資料。稽核：`User.Delete`（軟刪除）、`User.Restore`、`User.Purge`。
- **自動永久刪除**（0.9.97 起）：刪除超過 `SoftDeleteSettings:PurgeAfterDays`（預設 90 天，`0`＝不自動）的使用者，由排程作業「已刪除資料清理」（`SoftDeletePurgeService`）永久刪除；系統層級清除，不看團隊範圍。每次有刪到時寫一筆彙總稽核 `User.AutoPurge`（筆數、天數、觸發方式、`#Id 名稱` 清單）。support 帳號（不分大小寫）永遠不刪。⚠️ 永久刪除後同一個 Google 帳號再登入會被當成第一次登入，建立新的停用帳號；要長期擋人請用停用。見 [排程作業 PRD](排程作業-prd.md)。
- **RBAC 雙寫**（`SyncAssignmentsAsync` → `RbacWriteService`）：`SyncUserRolesAsync` 以 `UserRole` 反映主要＋額外角色（去重）；團隊名稱先解析為 `Team.Id`，`SyncUserTeamsAsync` 以 `UserTeam` 差異化增刪。
- **回填**（`GetUserAssignmentsAsync`）：由 `UserRole` join `RoleView`（0.9.95 起，排除已刪除的角色）扣除主要角色得額外角色、由 `UserTeam` join `Team` 得團隊名稱。
- **存檔驗證角色**（0.9.95 起）：新增與修改時，主要角色與額外角色必須存在且未刪除（null 跳過），否則回「選擇的角色已被刪除或不存在，請關閉視窗、重新開啟後再選擇角色。」—— 編輯視窗開著的期間角色可能被刪掉，存進去的話那個人每次換頁都會被登出。`SyncUserRolesAsync` 只在有效角色之間計算差異，指向已刪除角色的連結保留。
- **啟動回填**（`RbacBackfillService.RunAsync`）：開機時將既有 `MyUser.RoleViewId` 補寫成 `UserRole`、角色預設團隊補寫成 `UserTeam`，冪等執行，確保舊資料進入 RBAC 表。
- **有效團隊**：登入後由 `EffectiveTeamResolver` 決定（使用者直綁團隊優先，否則沿用角色預設團隊）。
- **前置檢查**：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查帳號唯一性。
- **稽核 actor**：`ResolveActor` 取目前登入者；未登入時 actor 為 null。

> ℹ️ `RbacWriteService` 是 0.4.36 `IDbContextFactory` 遷移後**唯一仍直接接收 `BackendDBContext`** 的服務。
> 這是刻意的 —— 它必須沿用呼叫端的 context 才能與主要寫入處於同一個工作單元（交易一致性），
> 因此不在 `DataAccessServiceLifetimeTests` 的守門範圍內。

## 五、權限與安全

- 本頁僅管理員可進入（`CheckIsAdmin`）；權限判定以 RBAC 表為單一權威，管理員短路一律通過。
- API 端業務資料以 `HasPermissionAttribute` 判權，無權限回 `ApiResult` 403（管理員短路），與本頁指派結果一致。
- 清單／單筆輸出經 `OtherDependencyData` 將密碼欄位清空；不輸出 `Salt`、雜湊、Token。
- 密碼一律 PBKDF2 雜湊儲存；細節見 [登入與帳號流程](登入與帳號流程-prd.md) 與安全文件。

## 六、錯誤與邊界

- 新增未輸入密碼：前端與 `AddAsync` 皆拒絕（「新增使用者時必須輸入密碼。」）。
- 帳號重複：新增／修改前置檢查回「帳號已存在，無法新增／修改。」
- 修改對象不存在：回「找不到要修改的使用者資料。」；刪除同理。
- **已刪除的使用者**（0.9.95 起）：密碼登入回與密碼錯誤相同的訊息、忘記密碼維持中性訊息、既有 Cookie 在下一次換頁被登出、JWT 更新失敗、API 權限判斷回 false（403）。已知殘留：在**目前這一頁**仍有效（與停用相同）；已簽發的 JWT access token 到期前打 `/api/Auth/me` 仍會回傳 claims（不碰資料庫）。
- 已刪除的使用者用 Google 登入：見 [登入與帳號流程](登入與帳號流程-prd.md)。
- 團隊名稱查無對應 `Team`：該名稱不會產生 `UserTeam`（僅同步存在的團隊）。
- 未設額外角色／團隊：僅保留主要角色與角色預設團隊。
- **團隊留空的儲存前提醒**（0.4.43 起，`ConfirmTeamBindingAsync`）：新增與修改共用同一條路徑，團隊欄位一個都沒選時，先以 `TeamBindingConfirm` 跳出「確認團隊設定」（`仍要儲存` ／ `回去編輯`）。此為**軟性提醒**，不是必填驗證 —— 空團隊在本系統是合法值（有效團隊會沿用角色預設團隊）。訊息依主要＋額外角色的 `DefaultTeams` 聯集分兩種：
  - 角色有預設團隊：「未直接指定團隊，此使用者將沿用其角色的預設團隊（○○、△△）。確定要這樣儲存嗎？」
  - 角色也沒有預設團隊：「未直接指定團隊，且其角色也沒有預設團隊，此使用者將只能看到無團隊標記的公開紀錄。確定要這樣儲存嗎？」
  - 勾選「管理員」時**不提醒**（管理員不受團隊行級過濾）；選「回去編輯」則保持 Modal 開啟且不寫入。
- **並行衝突**（0.9.93 起，樂觀並行）：開啟編輯後若別人先存檔或刪除了同一筆，存檔時回「這筆資料在你編輯期間已被其他人修改或刪除。請關閉視窗、重新開啟後再編輯。」，
  Modal 維持開啟、輸入不會遺失。屬使用者情境（LOG-11）：只記 `Information`，不進「系統例外紀錄」。
  0.9.92 之前這一頁的存檔不看結果，任何失敗都顯示「修改成功」；0.9.93 起失敗會顯示原因並保持 Modal 開啟。
- **修改只更新畫面上的欄位**（0.9.93 起）：帳號、姓名、Email、啟用、管理員、角色，密碼有填才更換。
  登入失敗次數、鎖定到期、兩步驟驗證、Google 綁定不會被管理員的存檔覆蓋 —— 0.9.92 之前是整筆覆蓋，管理員只改姓名，被鎖定的帳號就解鎖了。
  例外：管理員**設定新密碼**時一併解除鎖定（失敗次數歸零、清除鎖定到期），與「忘記密碼」重設後的行為一致。
  0.9.101 起設定新密碼要通過規則與歷史檢查（不可與這位使用者最近 N 次用過的密碼相同），`MustChangePassword` 一律依畫面儲存。
- **強制登出**（`ForceLogoutAsync`，0.9.103 起）：換掉這個人的工作階段版本（`ExecuteUpdate`、不換 `ConcurrencyStamp`、稽核 `User.ForceLogout`），所有已登入的瀏覽器下一次換頁被登出、API 無法再 refresh；自己那一列沒有這個按鈕。
  另外停用、刪除、變更管理員身分或角色、設定新密碼也會換版本；管理員改到自己時經 `/Auths/RefreshSession` 保持這台登入。
- **重設兩步驟驗證**（`ITwoFactorService.ResetAsync`，0.9.104 起）：清掉密鑰、時間步與備用碼並換工作階段版本（對方所有登入失效），稽核 `User.TwoFactorReset`；對方下次登入只需密碼，若必須使用會被帶去重新設定。
- **解鎖**（`UnlockAsync`，0.9.101 起）：只以 `ExecuteUpdate` 清除失敗次數與鎖定到期，**不換版本號**（別人開著這位使用者的編輯窗不會因此衝突）；沒有被鎖定時回「這位使用者目前沒有被鎖定。」。

## 七、驗收與測試

- `MyProject.Tests/MyUserServiceAssignmentTests.cs`：`AddAsync_WithMultipleRolesAndTeams_ShouldPersistUserRoleAndUserTeam`（多角色＋團隊落地 `UserRole`／`UserTeam`）。
- `MyProject.Tests/RbacWriteServiceTests.cs`：`SyncUserRolesAsync`／`SyncUserTeamsAsync` 差異化增刪對帳。
- `MyProject.Tests/RbacBackfillServiceTests.cs`：由 `RoleViewId` 建 `UserRole`、由角色預設團隊建 `UserTeam`、冪等。
- `MyProject.Tests/AuditEventsTests.cs`：`User.Create`／`User.Update`／`User.Delete`（含帳號）與未登入 actor 為 null。
- `MyProject.Tests/PermissionCheckerTests.cs`：多角色聯集有效權限鍵、管理員短路。
- `MyProject.Tests/PasswordPolicyTests.cs`（0.9.101）：新增與修改套用密碼原則、只勾旗標也會存、設定新密碼解鎖、解鎖不換版本號並寫稽核。
- `MyProject.Tests/TwoFactorTests.cs`（0.9.104）：管理員重設清除密鑰與備用碼、換版本、稽核。
- `MyProject.Tests/SoftDeleteUserRoleTests.cs`（0.9.95）：support／自己不可刪、存檔驗證角色、保留指向已刪除角色的連結、還原衝突、永久刪除、已刪除者無法登入。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Components/Pages/Admins/MyUserPage.razor`
- `src/MyProject/MyProject.Web/Components/Views/Admins/MyUserView.razor`、`MyUserView.razor.cs`（編輯回填）、`:405`（多角色／團隊變更）
- `src/MyProject/MyProject.Business/Services/DataAccess/MyUserService.cs`（Add）、`:250`（Update）、`:305`（雙寫）、`:332`（回填）
- `src/MyProject/MyProject.Business/Services/Other/RbacWriteService.cs`（`SyncUserRolesAsync`）、`:64`（`SyncUserTeamsAsync`）
- `src/MyProject/MyProject.Business/Services/Other/RbacBackfillService.cs`、`:123`（啟動回填）
- `src/MyProject/MyProject.Business/Services/Other/EffectiveTeamResolver.cs`（有效團隊）
- RBAC 資料表：`MyUser`、`RoleView`、`UserRole`、`UserTeam`、`RolePermissionMap`、`Permission`（`src/MyProject/MyProject.AccessDatas/Models/`）
- 交叉連結：[登入與帳號流程](登入與帳號流程-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)
