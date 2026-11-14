# 使用者管理 PRD

- 文件版本：1.23
- 文件狀態：已實作
- 現行系統版本：0.10.18
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/11/14

## 一、目標與範圍

提供管理員維護系統使用者帳號的完整能力：查詢、新增、修改、刪除（0.9.95 起為軟刪除，可還原），並在同一表單指派主要角色、額外角色（多角色，權限取聯集）與直接綁定的團隊。所有指派透過 RBAC 雙寫落地至關聯表，作為 UI 與 API 共用的權限來源。

- **範圍**：`/myusers` 使用者維護頁與 `MyUserService` CRUD、`GetUserAssignmentsAsync` 回填、`SyncAssignmentsAsync` 雙寫、稽核寫入。
- **非範圍**：登入、鎖定、改密碼與 Google 建帳見 [登入與帳號流程](登入與帳號流程-prd.md)；使用者自己改姓名見 [個人資料](個人資料-prd.md)（0.9.102 起；Email 仍只能在這一頁由管理員改）；角色本身與權限矩陣編輯見 [角色管理](角色管理-prd.md)；團隊清單維護見 `/teams`。

## 二、使用者與入口

| 路由 | 選單 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/myusers` | 系統管理 → 帳號與權限 → 使用者管理（`Menu.json` id=31；0.9.113 起移入「帳號與權限」子群組 id=34）| 僅管理員（0.10.4 起 WASM 頁以 `PagePermissions.IsAdmin` 判斷、API 以 `[AdminOnly]` 擋；之前是 `AuthenticationStateHelper.CheckIsAdmin`）| 系統管理員 |

> **0.4.33 起**：`使用者管理` 權限鍵刻意**不列入角色矩陣**（比照「統計與分析」群組，0.9.113 起改名「監控與診斷」；同次新增的子群組鍵 `角色_帳號與權限` 同樣不列入）。
> 此前該鍵可被勾選卻永遠無效（頁面以 `CheckIsAdmin` 守門），屬於「死權限」。
> 由 `MyProject.Tests/AdminOnlyPermissionTests.cs` 守門，請勿補上。


- 0.10.4 起本頁以 WebAssembly 執行（WASM 遷移 M5，`MyProject.Web.Client/Pages/MyUserPage.razor`，版面 `ClientMainLayout`；Server 版已刪除），畫面、按鈕、對話窗與訊息和之前相同。登入檢查由版面完成；`MyUserView` 以 `PagePermissions.IsAdmin` 判斷，非管理員顯示「你沒有權限存取此頁面」並停止載入，`Permission.Denied` 稽核由伺服器在 `POST /api/v1/shell/page-view` 寫。
- 0.10.4 起有管理員專用的 Web API `api/v1/users`（`UsersController`，JWT Bearer＋`[AdminOnly]`；非管理員回 403 並寫一筆 `Permission.Denied`，detail「需要管理員」），走與畫面相同的 `MyUserService`／`ITwoFactorService`，端點見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)。失敗依 `VerifyRecordResult.Kind` 對應：找不到 404、重複／衝突 409、規則不符 400、未預期 500。伺服器端另外驗證：`[ApiValidationFilter]`（每頁筆數 1～200、頁碼 ≥ 1）與帳號、名稱、角色必填（400）；產生密碼 `GET password/generate` 的回應帶 `Cache-Control: no-store`；「複製」建立時與 Server 版同樣順序：先 `POST api/v1/users/check` 檢查帳號等規則，再問「Email 已有人使用」，最後才新增。
  0.10.3 之前本頁為 Blazor Server 元件、沒有 `MyUser` API 控制器。權限不變：仍只以管理員身分閘控，權限鍵不放進角色矩陣。

## 三、畫面與欄位

- **清單**：遠端分頁 `Table`，欄位 帳號、名稱、Email、角色（`RoleViewName`）、狀態、管理員、建立時間、更新時間，皆可排序；工具列含新增、重新整理、匯出 Excel（0.9.107 起，不含密碼與金鑰欄位，見[通用匯出](通用匯出-prd.md)）、搜尋（0.10.17 起在搜尋框按 Enter 就搜尋，`ListSearchEnterConventionTests`）、清空搜尋，以及「顯示已刪除」切換。0.9.120 起點「啟用／停用」徽章直接切換（停用先確認「要停用「帳號」嗎？他所有已登入的瀏覽器與 API 會被登出…」，啟用直接切換，結果以右下角提示「已停用／已啟用「帳號」」顯示；`MyUserService.SetStatusAsync` 以 `ExecuteUpdateAsync` 只改狀態與更新時間，換工作階段版本 `SecurityStamp` 與版本號 `ConcurrencyStamp`、不動角色與團隊，已經是目標值時回成功、不寫稽核，稽核 `User.Enable`／`User.Disable`（detail `account=…`）；support 與自己不可停用，`CanToggleStatus` 為假時那一列徽章不可點（啟用中的 support 與自己），「顯示已刪除」清單中也不可點）；「管理員」僅在為「是」時顯示徽章，為「否」時顯示「—」。搜尋比對帳號／名稱／Email／角色名稱。
  0.9.101 起：被登入鎖定的帳號在「狀態」多一個黃色「鎖定至 HH:mm」徽章（不是今天時顯示 MM-dd HH:mm；0.10.4 起這段文字由伺服器依伺服器時區算好、放在 `UserDto.LockedText` 送來，瀏覽器時區不同也不會顯示錯），操作欄多一個「解鎖」（確認後解除，稽核 `User.Unlock`）。
  0.9.104 起：已開啟兩步驟驗證的帳號在「狀態」多一個「兩步驟驗證」徽章，操作欄（自己那一列除外）多「重設兩步驟驗證」（確認後清除驗證器設定與備用碼，見[兩步驟驗證](兩步驟驗證-prd.md)）。
- **維護表單**（Modal）欄位：
  - 帳號（必填，唯一；0.9.171 起去頭尾空白、不分大小寫；0.10.17 起只能用英文字母、數字與 `- _ . @`（`AccountRules`），只在新增或改帳號時檢查，既有帳號（例如 Google 登入以 Email 建立、含 `+` 的）不改帳號時照樣能存，違反回 400「帳號只能使用英文字母、數字與 - _ . @。」；0.10.15 起新增與複製窗的帳號欄位即時檢查重複（`AsyncFieldCheck` 呼叫 `UserApi.AccountConflictAsync`，0.10.17 起也顯示格式訊息））、密碼（新增必填；編輯留白＝沿用既有密碼；下方顯示密碼規則）、名稱（必填）、Email。
  - 「下次登入須變更密碼」核取方塊（0.9.101 起，`MustChangePassword`）：新增時預設勾選；編輯時輸入新密碼會自動勾選（仍可取消）；也可以不改密碼只勾選，要求對方下次登入先換密碼。
    ⚠️ 0.9.60 起 **Email 是「忘記密碼」寄信的收件者**：留空的帳號無法自助重設密碼（只記在稽核 `Password.ResetRequested` 的 `reason=InvalidEmail`）。0.9.85 起 `BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查格式（`MailAddress.TryCreate`，與 `PasswordResetService` 判斷能否寄信的規則相同）：有填但不合法時擋下並顯示「Email 格式不正確；不使用可留空。」。既有錯誤資料不自動修正（例如出貨預設 `BootstrapSettings:SupportEmail = "support"`），修改該帳號時須改正或清空。多個帳號可以共用同一個 Email，申請時每個帳號各收一封。
  - 角色（必填，單選主要角色 `RoleViewId`）。
  - 額外角色（多選，`AdditionalRoleIds`，與主要角色權限取聯集）。
  - 團隊（多選團隊名稱，直接綁定此使用者；不設定則沿用其角色的預設團隊）。**非必填**，但留空時儲存前會跳出確認（見 §六）。
  - 0.10.15 起每次開窗（新增、修改、複製）與存檔失敗後都重讀角色、團隊選項（`LoadFormOptionsAsync`，`FormOptionsConventionTests`）；存檔時伺服器擋下**新選的**已停用或已刪除團隊，回 400「所選的團隊「X」已不存在或已停用，請重新選擇（選項已更新）。」（`SelectedOptionGuard`；原本就掛著的不擋）。複製窗的團隊是唯讀的，被擋時把不能用的團隊從要建立的資料拿掉，再按一次就能建立。
  - 啟用（`Status`）、管理員（`IsAdmin`）核取方塊。
- 編輯前以 `Clone()` 複製當前列並載入既有指派回填；新增時預設帶入「預設角色」。
- **複製**（0.9.127 起，操作欄「複製」，support 那一列沒有、已刪除清單沒有）：以這一列為範本開精簡窗（`form-modal`，獨立的 `copyRecord`，不共用修改窗的 `CurrentRecord`）。
  - 要填：帳號、名稱、密碼（旁邊「產生」鈕呼叫 `IPasswordPolicy.Generate()` 並改顯示明碼）、Email、「下次登入須變更密碼」（預設勾選）。
  - 唯讀帶入：主要角色＋額外角色、直接綁定的團隊（已停用的不帶並註明）、啟用狀態、管理員身分；Google 綁定、兩步驟驗證、鎖定與密碼不帶。要調整請建立後再「修改」。
  - 「建立」**省略**一般新增的「確認儲存」，只在三種情況多問：範本是管理員（「建立管理員帳號」）、團隊留空（同 `ConfirmTeamBindingAsync`）、Email 已被其他未刪除帳號使用（「Email 已有人使用」，只提醒不擋）。
  - 成功後關窗並開「已建立使用者」交付窗（`.user-copy-result-modal`）：登入網址（0.9.128 起為系統根網址 `NavigationManager.BaseUri`：未登入開啟時由首頁開場畫面導到登入頁）、帳號、密碼（勾了須變更密碼時加一行提醒），開窗時自動 `appClipboard.copyText`；非使用者手勢觸發的寫入可能被瀏覽器拒絕，故保留「複製」鈕。關窗即清除明碼。
- **異動歷程**（0.9.173 起）：每列操作欄最後一顆「異動歷程」（`AuditHistoryButton`，圖示 `history`），一般清單與「顯示已刪除」都有；**只有管理員看得到**（這一頁本來就只有管理員能進來）。按下開「異動歷程：<名稱>」窗（`AuditHistoryDialog.ShowAsync("MyUser", Id, 名稱)`，標題顯示帳號（`Account`，不顯示姓名）），左側列出這一筆的異動（排除換頁、搜尋、查看等瀏覽類，每頁 10 筆），右側顯示點選那一筆的摘要與修改前後差異；打開寫 `Audit.Search`、點一筆寫 `Audit.View`（摘要含 `via=history`）。目標類型 `MyUser` 必須與服務寫稽核的 `TargetType` 相同。詳見 [稽核紀錄 PRD §3.4](稽核紀錄-prd.md)。

## 四、內部系統運作

WASM View（`MyUserView`）→ `UserApi` → `UsersController`（`api/v1/users`）→ `MyUserService` → `BackendDBContext`：

- **新增**（`AddAsync`）：0.9.101 起先以 `IPasswordPolicy.Check` 檢查規則，再由 `ApplyAsync` 雜湊並寫入設定時間、旗標與歷史；存檔後 `SyncAssignmentsAsync` 雙寫角色與團隊；寫 `User.Create` 稽核。0.9.127 起選填參數 `copiedFromAccount`（複製建立時傳範本帳號），稽核 Detail 末尾加 `; copiedFrom=帳號`。
- **複製範本**（0.9.127 起）：`BuildCopyDraftAsync(sourceUserId)` 讀來源與 `GetUserAssignmentsAsync`，回傳全新的 `MyUserAdapterModel`（Id 0、個人欄位空、新 List）與被略過的停用團隊；來源不存在或為 support（`CanCopy`，不分大小寫）回 null。`GetAccountsUsingEmailAsync(email)` 以 Trim＋不分大小寫比對未刪除使用者，供 Email 重複提醒。
- **產生密碼**（`IPasswordPolicy.Generate`，0.9.127 起）：`RandomNumberGenerator`，長度 `Max(MinimumLength, 12)`，大小寫與數字各至少一個、`RequireSymbol` 時加符號，排除 0/O/1/l/I；必定通過 `Check`。
- **修改**（`UpdateAsync`）：以 `Entry(...).State = Modified` 更新；密碼留白時沿用既有 `Password`／`Salt`，否則重新雜湊；再 `SyncAssignmentsAsync`；寫 `User.Update` 稽核。
- **刪除**（`DeleteAsync`，0.9.95 起為軟刪除）：追蹤載入 → 禁止刪除 support（`BootstrapSettings.SupportAccount`，不分大小寫）與自己（`CurrentUser.Id`，0 時不比對）→ `SoftDeleteHelper.MarkDeleted` → 刪掉該使用者的密碼重設 token；`UserRole`／`UserTeam` 保留（還原後原樣回來）。畫面以 `CanDelete` 隱藏 support 與自己那一列的刪除鈕（伺服器端仍是權威）。
- **已刪除清單／還原／永久刪除**（`GetDeletedAsync`／`RestoreAsync`／`PurgeAsync`，0.9.95 起）：工具列「顯示已刪除」切換。還原時擋下三種衝突並說明：與有效使用者同帳號（完全比對，與新增檢查一致）、綁定的 GoogleId 已連到別人、主要角色已被刪除（主要角色為 null 允許）。永久刪除只能對已刪除的使用者，`IgnoreQueryFilters` 追蹤載入後 `Remove`，關聯由資料庫 Cascade 刪除（不可用 `ExecuteDelete`，它也套用過濾器而刪 0 筆）。刪除與還原都會換新版本號；登入、權限判斷、下拉選單都經全域過濾器而看不到已刪除的資料。稽核：`User.Delete`（軟刪除）、`User.Restore`、`User.Purge`。
- **自動永久刪除**（0.9.97 起）：刪除超過 `SoftDeleteSettings:PurgeAfterDays`（預設 90 天，`0`＝不自動）的使用者，由排程作業「已刪除資料清理」（`SoftDeletePurgeService`）永久刪除；系統層級清除，不看團隊範圍。每次有刪到時寫一筆彙總稽核 `User.AutoPurge`（筆數、天數、觸發方式、`#Id 名稱` 清單）。support 帳號（不分大小寫）永遠不刪。⚠️ 永久刪除後同一個 Google 帳號再登入會被當成第一次登入，建立新的停用帳號；要長期擋人請用停用。見 [排程作業 PRD](排程作業-prd.md)。
- **RBAC 雙寫**（`SyncAssignmentsAsync` → `RbacWriteService`）：`SyncUserRolesAsync` 以 `UserRole` 反映主要＋額外角色（去重）；團隊名稱先解析為 `Team.Id`，`SyncUserTeamsAsync` 以 `UserTeam` 差異化增刪。
- **回填**（`GetUserAssignmentsAsync`）：由 `UserRole` join `RoleView`（0.9.95 起，排除已刪除的角色）扣除主要角色得額外角色、由 `UserTeam` join `Team` 得團隊名稱。
- **存檔驗證角色**（0.9.95 起）：新增與修改時，主要角色與額外角色必須存在且未刪除（null 跳過），否則回「選擇的角色已被刪除或不存在，請關閉視窗、重新開啟後再選擇角色。」—— 編輯視窗開著的期間角色可能被刪掉，存進去的話那個人每次換頁都會被登出。`SyncUserRolesAsync` 只在有效角色之間計算差異，指向已刪除角色的連結保留。
- **啟動回填**（`RbacBackfillService.RunAsync`）：開機時將既有 `MyUser.RoleViewId` 補寫成 `UserRole`、角色預設團隊補寫成 `UserTeam`，冪等執行，確保舊資料進入 RBAC 表。
- **有效團隊**：登入後由 `EffectiveTeamResolver` 決定（使用者直綁團隊優先，否則沿用角色預設團隊）。
- **前置檢查**：`BeforeAddCheckAsync`／`BeforeUpdateCheckAsync` 檢查帳號唯一性（0.9.171 起 `NameNormalizer` 後以 `ToLower()` 比對，還原時同規則）；寫入前 `AutoMapping` 與 `UpdateAsync` 都去頭尾空白。資料庫有部分唯一索引 `IX_MyUser_Account`（`WHERE "IsDeleted" = 0`）兜底，違反時經 `UniqueConstraintHelper` 回「帳號已存在（不分大小寫），無法儲存。」。
- **稽核 actor**：`ResolveActor` 取目前登入者；未登入時 actor 為 null。

> ℹ️ `RbacWriteService` 在 0.4.36 `IDbContextFactory` 遷移後**仍直接接收 DI 注入的 scoped `BackendDBContext`**
> （`PermissionChecker`、`EffectiveTeamResolver` 等亦同），不在 `DataAccessServiceLifetimeTests` 的守門範圍內。
> 0.9.136 起 `MyUserService.AddAsync`／`UpdateAsync` 以工廠建立的 context 開一個交易：存主表 → `SyncAssignmentsAsync` 經
> `RbacWriteService` 帶 context 的多載用**同一個 context** 寫 `UserRole`／`UserTeam` → 提交；對應寫入失敗時整筆回復（`MyUserServiceAssignmentTests`）。
> 之前兩者各自 `SaveChangesAsync`，可能只成功一半。

## 五、權限與安全

- 本頁僅管理員可進入（0.10.4 起 `PagePermissions.IsAdmin`，之前是 `CheckIsAdmin`）；權限判定以 RBAC 表為單一權威，管理員短路一律通過。
- API 端業務資料以 `HasPermissionAttribute` 判權，無權限回 `ApiResult` 403（管理員短路），與本頁指派結果一致。
- 清單／單筆輸出經 `OtherDependencyData` 將密碼欄位清空；不輸出 `Salt`、雜湊、Token。
- 密碼一律 PBKDF2 雜湊儲存；細節見 [登入與帳號流程](登入與帳號流程-prd.md) 與安全文件。

## 六、錯誤與邊界

- 新增未輸入密碼：前端與 `AddAsync` 皆拒絕（「新增使用者時必須輸入密碼。」）。
- 帳號重複（不分大小寫）：新增／修改前置檢查回「帳號已存在，無法新增／修改。」
- 升級到 0.9.171 時既有資料有不分大小寫重複的帳號：migration `AddUserAccountRoleNameUniqueIndex` 保留管理員（其次 Id 最小的；support 不能被停用），其他改名為「原帳號 (重複-Id)」、停用並換 SecurityStamp，每筆稽核 `User.DuplicateAccountRenamed`。
- 修改對象不存在：回「找不到要修改的使用者資料。」；刪除同理。
- **已刪除的使用者**（0.9.95 起）：密碼登入回與密碼錯誤相同的訊息、忘記密碼維持中性訊息、Token 刷新失敗、API 權限判斷回 false（403）。0.10.8 起網頁也是 Token，已開著的頁面在下一個 API 請求就被要求重新登入（每個請求由 `ApiActorMiddleware` 從資料庫載入目前使用者）；0.10.7 以前網頁是既有 Cookie 在下一次換頁被登出，而且在目前這一頁仍有效。
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
- **修改只更新畫面上的欄位**（0.9.93 起）：帳號、姓名、Email、啟用、管理員、角色，密碼有填才更換。 0.9.120 起把 support 或自己改成停用會被擋下（與清單點徽章同一條規則 `GetDisableBlockReason`）。
  0.10.4 起 **support 不可改帳號**（`MyUserService.UpdateAsync`，畫面與 API 都適用，回「「support」是系統預設的開發帳號，不可修改帳號。」）：不可停用、不可刪除、不可當複製範本都是比對帳號，之前先把 support 改名就能繞過（改寫前就有的問題，審查發現）。
  登入失敗次數、鎖定到期、兩步驟驗證、Google 綁定不會被管理員的存檔覆蓋 —— 0.9.92 之前是整筆覆蓋，管理員只改姓名，被鎖定的帳號就解鎖了。
  例外：管理員**設定新密碼**時一併解除鎖定（失敗次數歸零、清除鎖定到期），與「忘記密碼」重設後的行為一致。
  0.9.101 起設定新密碼要通過規則與歷史檢查（不可與這位使用者最近 N 次用過的密碼相同），`MustChangePassword` 一律依畫面儲存。
- **強制登出**（`ForceLogoutAsync`，0.9.103 起）：換掉這個人的工作階段版本（`ExecuteUpdate`、不換 `ConcurrencyStamp`、稽核 `User.ForceLogout`），所有已登入的瀏覽器在下一個 API 請求被要求重新登入、API 無法再 refresh；自己那一列沒有這個按鈕。
  0.10.4 起 API 也擋強制登出自己（`POST api/v1/users/{id}/force-logout` 回 400「不可強制登出自己的帳號，請使用右上角的登出。」；之前只在畫面擋）。
  另外停用、刪除、變更管理員身分或角色、設定新密碼也會換版本（對方下一個請求就被登出：API 401、刷新被拒）；管理員改到自己時這個分頁保持登入（其他裝置維持登出）。
  0.10.8 起（WASM 遷移 M9）：`PUT api/v1/users/{id}` 改到自己的密碼、角色或管理員身分時回傳 `Tokens`（`SelfSessionRenewal`：把這次請求所屬的那一串換成新的一串、舊的撤銷），頁面存起來後整頁重新載入本頁，選單與權限換成新的。0.10.4～0.10.7 是回 `SessionRefreshUrl`（`/Auths/RefreshSession?ticket=…`）整頁導過去換發 Cookie，0.10.8 起移除。
- **重設兩步驟驗證**（`ITwoFactorService.ResetAsync`，0.9.104 起）：清掉密鑰、時間步與備用碼並換工作階段版本（對方所有登入失效），稽核 `User.TwoFactorReset`；對方下次登入只需密碼，若必須使用會被帶去重新設定。自己那一列沒有這個按鈕；0.10.4 起 API 也擋（`POST api/v1/users/{id}/two-factor/reset` 回 400「不可重設自己的兩步驟驗證，請到「兩步驟驗證」頁停用或重新設定。」）。
- **解鎖**（`UnlockAsync`，0.9.101 起）：只以 `ExecuteUpdate` 清除失敗次數與鎖定到期，**不換版本號**（別人開著這位使用者的編輯窗不會因此衝突）；沒有被鎖定時回「這位使用者目前沒有被鎖定。」。

## 七、驗收與測試

- `MyProject.Tests/MyUserServiceAssignmentTests.cs`：`AddAsync_WithMultipleRolesAndTeams_ShouldPersistUserRoleAndUserTeam`（多角色＋團隊落地 `UserRole`／`UserTeam`）；0.9.136 起另有新增與修改在對應寫入失敗時整筆回復的兩支。
- `MyProject.Tests/RbacWriteServiceTests.cs`：`SyncUserRolesAsync`／`SyncUserTeamsAsync` 差異化增刪對帳。
- `MyProject.Tests/RbacBackfillServiceTests.cs`：由 `RoleViewId` 建 `UserRole`、由角色預設團隊建 `UserTeam`、冪等。
- `MyProject.Tests/AuditEventsTests.cs`：`User.Create`／`User.Update`／`User.Delete`（含帳號）與未登入 actor 為 null。
- `MyProject.Tests/PermissionCheckerTests.cs`：多角色聯集有效權限鍵、管理員短路。
- `MyProject.Tests/PasswordPolicyTests.cs`（0.9.101）：新增與修改套用密碼原則、只勾旗標也會存、設定新密碼解鎖、解鎖不換版本號並寫稽核。
- `MyProject.Tests/TwoFactorTests.cs`（0.9.104）：管理員重設清除密鑰與備用碼、換版本、稽核。
- `MyProject.Tests/SoftDeleteUserRoleTests.cs`（0.9.95）：support／自己不可刪、存檔驗證角色、保留指向已刪除角色的連結、還原衝突、永久刪除、已刪除者無法登入。
- `MyProject.Tests/StatusToggleServiceTests.cs`（0.9.120）：使用者停用後⭐ **角色與團隊仍在**、換工作階段版本與版本號並寫稽核；不可停用 support（不分大小寫）與自己，修改窗存檔也擋。`Components/StatusPillTests.cs`、`Components/RequirePermissionTests.cs`：可點標籤與 `Fallback`。
- `MyProject.Tests/MyUserServiceCopyTests.cs`（0.9.127）：複製範本帶入角色／團隊／狀態／管理員且個人欄位留空、略過停用團隊、support 與不存在的來源回 null、`CanCopy`、Email 使用者查詢（大小寫、空白、已刪除）、由範本建立後 `UserRole`／`UserTeam` 一致且稽核含 `copiedFrom`。
- `MyProject.Tests/PasswordPolicyTests.cs` `Generate_ShouldAlwaysPassTheCurrentRules`（0.9.127）：多組規則下產生的密碼都通過 `Check`、長度正確、不含易混淆字元、不重複。
- `AuditHistoryConventionTests.DataPage_ShouldOfferAuditHistoryOnEveryRow`（0.9.173）：兩個分支各一顆「異動歷程」、頁尾一個對話窗、目標類型為 `MyUser`；元件行為見 `Components/AuditHistoryTests`。
- `MyProject.Tests/UserApiTests.cs`（0.10.4 起，8 支）：使用者 Web API 的完整生命週期且每個動作只記一筆稽核、改別人後對方下一個請求被登出、改自己後回新的 `Tokens`、這台維持登入（0.10.8 起；之前經 `SessionRefreshUrl`）且不可強制登出或重設自己的兩步驟驗證（400）、複製從範本開始並記下來源、搜尋標出 support 的可用操作並匯出 xlsx、解鎖、伺服器端規則不靠畫面也成立（必填、support 不可改帳號、產生密碼不快取、新增前檢查）、非管理員 403。
- `MyProject.Tests/ApiAuthorizationConventionTests.cs`（0.10.4 起）：每個 Web API action（含繼承來的）都要標註 `[HasPermission]`／`[AdminOnly]`／`[SignedInUser]`／`[AllowAnonymous]`；除了 `[AllowAnonymous]` 都要有 `[Authorize]`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web.Client/Pages/MyUserPage.razor`（0.10.4 起 WASM；之前在 `MyProject.Web/Components/Pages/Admins/`）
- `src/MyProject/MyProject.Web.Client/Api/UserApi.cs`、`UserDtoMapper.cs`（WASM 頁呼叫 API、DTO ↔ 畫面模型；Controller 共用同一份對應）；`src/MyProject/MyProject.Web/Controllers/UsersController.cs`；DTO 在 `src/MyProject/MyProject.Dtos/Admins/UserDtos.cs`
- `src/MyProject/MyProject.Web.Client/Views/Admins/MyUserView.razor`、`MyUserView.razor.cs`（編輯回填 `OnEditAsync`、多角色／團隊變更 `OnAdditionalRolesChanged`／`OnUserTeamsChanged`、團隊留空提醒 `ConfirmTeamBindingAsync`、點狀態標籤 `OnToggleStatusAsync`）
- `src/MyProject/MyProject.Business/Services/DataAccess/MyUserService.cs`（`AddAsync`、`UpdateAsync`、雙寫 `SyncAssignmentsAsync`、回填 `GetUserAssignmentsAsync`、只改狀態 `SetStatusAsync`／`CanToggleStatus`／`GetDisableBlockReason`）
- `src/MyProject/MyProject.Business/Services/Other/RbacWriteService.cs`（`SyncUserRolesAsync`、`SyncUserTeamsAsync`）
- `src/MyProject/MyProject.Business/Services/Other/RbacBackfillService.cs`（啟動回填 `RunAsync`）
- `src/MyProject/MyProject.Business/Services/Other/EffectiveTeamResolver.cs`（有效團隊）
- RBAC 資料表：`MyUser`、`RoleView`、`UserRole`、`UserTeam`、`RolePermissionMap`、`Permission`（`src/MyProject/MyProject.AccessDatas/Models/`）
- 交叉連結：[登入與帳號流程](登入與帳號流程-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)
