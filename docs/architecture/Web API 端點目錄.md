# Web API 端點目錄

- 文件版本：1.14
- 文件狀態：已實作
- 現行系統版本：0.10.3
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/11

本文件彙整 `MyProject.Web/Controllers/` 下所有 Web API 端點的實際路由、HTTP 動詞、授權與回傳型別，作為《[Web API 設計慣例](Web%20API%20設計慣例.md)》（樣板與慣例）之外的**端點清單參照**。慣例細節（`ApiResult<T>`、`PagedResult<T>`、Search DTO、動作級授權）見設計慣例文件。

## 一、通則

- 每個資源控制器同時掛 `api/[controller]` 與 `api/v1/[controller]` 兩條平行路由（見《[API Versioning 策略](API%20Versioning%20策略.md)》）；`BackupController` 只有 `api/backups`，`DownloadsController` 只有 `api/v1/downloads`（0.10.3 起）。
- 資源控制器類別層級套 `[ApiController]`、`[ApiValidationFilter]`、`[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`（API 用 JWT Bearer）。**例外是 `BackupController`（見第三節末），它走 Cookie（M7 改用下載票證）；`DownloadsController` 匿名、以一次性票證當憑證（第三節）。** 0.10.3 起專案附件已沒有以 Cookie 驗證的 API。
- 每個動作以 `[HasPermission(資源鍵, 動作)]` 做動作級授權；無權限回 **403** 並維持 `ApiResult` 外殼；管理員短路。權限鍵定義於 `MyProject.Share` 的 `MagicObjectHelper`，動作為 `PermissionActions.View/Create/Edit/Delete`。
- 回傳一律包在 `ApiResult<T>`；分頁再包 `PagedResult<T>`。

## 二、資源 CRUD 控制器

三個資源控制器的路由前綴與權限鍵：

| 控制器 | 路由前綴 | 權限鍵（`MagicObjectHelper`）| 檔案 |
|--------|----------|------------------------------|------|
| `CategoryController` | `api/Category`、`api/v1/Category` | `角色_分類清單` | `Controllers/CategoryController.cs` |
| `TeamController` | `api/Team`、`api/v1/Team` | `角色_團隊清單` | `Controllers/TeamController.cs` |
| `ProjectController` | `api/Project`、`api/v1/Project` | `角色_專案項目` | `Controllers/ProjectController.cs` |

### 2.1 分類、團隊（0.10.2 起，WASM 遷移 M3）

`CategoryController`／`TeamController` 改呼叫畫面同一個服務（`CategoryService`／`TeamService`），團隊範圍、名稱規則、並行衝突、軟刪除與稽核只有一份（稽核的操作者是存取 Token 的使用者）；`CategoryRepository`／`TeamRepository` 已刪除。WASM 版的分類、團隊頁只用這組端點。
失敗依服務回傳的 `VerifyRecordResult.Kind` 決定狀態碼（找不到或範圍外 404、名稱或代號重複／版本衝突／目前狀態不允許 409、輸入不符規則 400、未預期錯誤 500，見《[Web API 設計慣例](Web%20API%20設計慣例.md)》），**錯誤訊息與畫面上看到的相同**（0.10.2 起；使用者確認沒有外部程式依賴舊訊息）。

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET {controller}/{id}` | View | `ApiResult<TDto>`（查無或範圍外回 404）；寫一筆「查看」稽核（`*.View`）|
| 搜尋分頁 | `POST {controller}/search` | View | `ApiResult<PagedResult<TDto>>`。0.10.2 起多三個欄位：`Deleted`（查「顯示已刪除」，**要有 Delete 權限**，沒有回 403）、`RecordSearch`（true 時寫一筆搜尋稽核 `*.Search`；換頁、排序不帶）、`SortBy` 不分大小寫（可用 `name`、`isEnabled`、`createdAt`、`updatedAt`，團隊另有 `code`；不認得的退回預設排序）|
| 存檔前檢查 | `POST {controller}/check` | View＋（`Id=0` 要 Create、`Id>0` 要 Edit，否則 403）| `ApiResult`：名稱（團隊另有代號）會不會重複；修改時範圍外的 `Id` 回 404。WASM 頁在存檔確認流程中呼叫，順序與 Server 版相同 |
| 新增 | `POST {controller}` | Create | `ApiResult<TDto>`（回傳含資料庫配的 `Id` 與 `ConcurrencyStamp`；同名回 409，團隊另檢查代號；指派範圍外的團隊、上層團隊不合法回 400；客戶端傳來的 `Id` 一律忽略，0.9.94 起）|
| 更新 | `PUT {controller}/{id}` | Edit | `ApiResult`（路由/資料 ID 不符回 400；**沒帶 `ConcurrencyStamp` 回 400**；先查範圍：查無或範圍外回 404，再比對名稱；版本號不符或名稱重複回 409；團隊指派或團隊樹規則不過回 400；團隊改成已刪除團隊的名稱回 409）|
| 啟用／停用 | `PUT {controller}/{id}/enabled` | Edit | body `{ "enabled": true/false }`，只改狀態並換版本號；已經是目標值回成功、不寫稽核 |
| 刪除 | `DELETE {controller}/{id}` | Delete | `ApiResult`（**軟刪除**，0.9.94 起；查無、範圍外或已刪除回 404；團隊還有未刪除的下屬回 400，與改寫前相同）|
| 還原 | `POST {controller}/{id}/restore` | Delete | `ApiResult`（只對已刪除的資料；已有同名（團隊另有同代號）回 409）|
| 永久刪除 | `DELETE {controller}/{id}/purge` | Delete | `ApiResult`（只能對已刪除的資料，否則 404；團隊還有其他團隊以它為上層時回 409）|
| 匯出 | `POST {controller}/export` | View（`Deleted=true` 要 Delete，否則 403）| 成功回 xlsx 檔（不包 `ApiResult`），條件與搜尋相同；超過 10000 筆回 400 並帶原因；寫匯出稽核 `*.Export`。團隊在沒有關鍵字、不是已刪除、沒有啟用狀態條件時匯出全部團隊（部門樹模式）|

只有分類或團隊才有的端點：

| 控制器 | 動作 | 路由 | 權限 | 回傳 |
|---|---|---|---|---|
| `CategoryController` | 表單選項 | `GET Category/options` | View | `ApiResult<CategoryFormOptionsDto>`：啟用中的團隊名稱、呼叫者是否管理員（`IsAdmin`）與自己的團隊範圍（`MyTeams`）；不要求團隊頁的權限 |
| `TeamController` | 全部團隊 | `GET Team/all` | View | `ApiResult<List<TeamDto>>`：所有未刪除的團隊（畫面組成部門樹）|
| | 可選的上層團隊 | `GET Team/{id}/parent-candidates` | View | `ApiResult<List<TeamNodeDto>>`：排除自己與所有下屬（新增時 `id` 傳 0）；存檔時伺服器會再檢查一次 |

`CategoryDto`／`TeamDto` 0.10.2 起多了 `IsDeleted`、`DeletedAt`、`DeletedBy`（「顯示已刪除」清單用）。網址、狀態碼的分類方式與欄位名稱都沒變；以前與畫面不同的錯誤訊息改成畫面那一句。

> 稽核：新增、修改、啟用／停用、刪除、還原、永久刪除由服務寫（與畫面相同）；查看、搜尋、匯出由 Controller 寫。

### 2.2 專案（`ProjectController`，0.10.3 起，WASM 遷移 M4）

`ProjectController` 改呼叫畫面同一個服務 `ProjectService`，團隊範圍、名稱規則、附件、並行衝突、軟刪除與稽核只有一份；`ProjectRepository`（與它用的 `UpdateOutcome`、`CombinedSearchHelper`、`ReplaceExpressionVisitor`）已刪除，專案中已沒有 Repository。WASM 版的專案項目頁只用這組端點。
失敗依 `VerifyRecordResult.Kind` 決定狀態碼（與 2.1 相同），錯誤訊息就是畫面那一句。網址、HTTP 狀態碼與欄位名稱沒變；以下幾點經使用者確認改成與畫面一致（0.10.3 起）：

- 關鍵字比對標題、描述、狀態、優先級、負責人（以前只比對標題與描述）。
- 狀態、優先級不在畫面的選項裡回 400；結束日期早於開始日期回 400。
- 開始、結束日期可以不填（`ProjectCreateUpdateDto.StartDate`／`EndDate` 改成可為 null，拿掉 `[Required]`）。
- **專案標題不能重複**：新增時、或修改且標題真的有變時，與另一筆未刪除專案的標題相同（去頭尾空白、英文不分大小寫）回 409「專案名稱已存在，無法新增。」／「專案名稱已存在，無法修改。」。0.10.3 之前畫面允許重複（API 原本就回 409）；以前留下的重複標題，只要不改標題仍可存檔；還原時已有同名的未刪除專案也回 409「已有同名的專案「X」，無法還原。請先將現有的同名專案改名後再還原。」（與分類相同）。存檔時標題以 `NameNormalizer.Normalize` 去掉前後空白（含 Tab、全形空白）。這個檢查在 `ProjectService.BeforeAddCheckAsync`／`BeforeUpdateCheckAsync`，AI Agent 工具與「產生測試專案」直接呼叫 `AddAsync`，不受影響。
- 修改、刪除、還原等先確認資料在自己的團隊範圍內（範圍外一律 404），再比對標題。⚠️ 但標題是**全系統**唯一（不分團隊），所以新增或改名時的 409 仍會透露「某個看不到的團隊有這個標題的專案」——全系統唯一的已知取捨。另一個已知限制：唯一性只在程式檢查，資料庫沒有唯一索引，兩個同時送出的新增可能都通過。

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET Project/{id}` | View | `ApiResult<ProjectDto>`（查無或範圍外回 404）。0.10.3 起含附件清單 `Files`（`Id`、`OriginalFileName`、`ContentType`、`FileSize`、`CreatedAt`；伺服器上的存放路徑不回傳），寫一筆「查看」稽核 `Project.View`。`includeRelatedData` 參數保留相容、已不作用 |
| 搜尋分頁 | `POST Project/search` | View | `ApiResult<PagedResult<ProjectDto>>`（清單不帶附件）。原有的 `Owner`、`Status`、`Priority`、`StartDateFrom`、`StartDateTo`、`CompletionPercentageMin`／`Max` 條件保留（`ProjectListFilter`）；0.10.3 起多 `Categories`、`Teams`（符合任一即可，與畫面的篩選相同）、`Deleted`（查「顯示已刪除」，**要有 Delete 權限**，沒有回 403；已刪除清單同樣套用 `Categories`／`Teams`，WASM 頁面在顯示已刪除時不送這兩個條件）、`RecordSearch`（true 時寫一筆搜尋稽核 `Project.Search`）；`SortBy` 不分大小寫，不認得的退回預設排序（更新時間新到舊）|
| 表單選項 | `GET Project/options` | View | `ApiResult<ProjectFormOptionsDto>`：啟用中的分類與團隊名稱、`IsAdmin`、自己的團隊範圍（`MyTeams`）、新增時預設帶入的團隊（`DefaultTeams`：範圍裡最上層的團隊）、允許的附件副檔名（`AllowedExtensions`）、單一附件上限（`MaxUploadFileSize`，1 GB）|
| 新增 | `POST Project` | Create | `ApiResult<ProjectDto>`（回傳含 `Id` 與 `ConcurrencyStamp`；標題重複回 409；指派範圍外的團隊回 400；客戶端傳來的 `Id` 一律忽略，0.9.94 起）。接受兩種內容，見下方「附件」|
| 更新 | `PUT Project/{id}` | Edit | `ApiResult`（路由/資料 ID 不符回 400；**沒帶 `ConcurrencyStamp` 回 400**；查無或範圍外回 404；版本號不符或標題重複回 409；團隊指派不過回 400）。接受兩種內容，見下方「附件」|
| 刪除 | `DELETE Project/{id}` | Delete | `ApiResult`（**軟刪除**；查無、範圍外或已刪除回 404；附件實體檔保留到永久刪除）|
| 還原 | `POST Project/{id}/restore` | Delete | `ApiResult`（0.10.3 起；只對已刪除的專案；已有同名的未刪除專案回 409）|
| 永久刪除 | `DELETE Project/{id}/purge` | Delete | `ApiResult`（0.10.3 起；只能對已刪除的專案，否則 404；附件的實體檔一併刪除）|
| 匯出 | `POST Project/export` | View（`Deleted=true` 要 Delete，否則 403）| 0.10.3 起。成功回 xlsx 檔（不包 `ApiResult`），條件與搜尋相同；超過 10000 筆回 400 並帶原因；寫匯出稽核 `Project.Export` |
| 申請下載票證 | `POST Project/files/{fileId}/ticket` | View | 0.10.3 起。`ApiResult<DownloadTicketDto>`：`{ url: "<PathBase>/api/v1/downloads/<票證>", expiresInSeconds: 60 }`；附件看不到（專案已刪除、團隊範圍外、實體檔不存在）回 404。用法見第三節 |

**附件（0.10.3 起）**：`POST Project` 與 `PUT Project/{id}` 在同一個網址依內容類型（`[Consumes]`）分兩種：

- JSON（不帶附件）：接受 `application/json`、`text/json`、`application/*+json`（與 0.10.3 之前相同）。
- `multipart/form-data`（帶附件），欄位：
  | 欄位 | 內容 |
  |---|---|
  | `data` | 必填。與 JSON 版相同的 `ProjectCreateUpdateDto`，序列化成 JSON 字串放在表單欄位；解析不了回 400 |
  | `files` | 選填，可重複多次，每個是一個附件檔（檔名取原始檔名、ContentType 由伺服器依副檔名決定）|
  | `removedFileIds` | 選填，只有 `PUT`，可重複多次，要移除的附件 Id |

  multipart 的 action 標 `[ApiExplorerSettings(IgnoreApi = true)]`，**Swagger 不顯示**（Swagger 不能在同一個方法＋路徑放兩個 action），格式以本表為準。路由另設 `Order = 1`：請求沒帶 Content-Type 時由 JSON 版處理、回 415（與 0.10.3 之前相同），不會因為兩個 action 都符合而回 500。走與畫面相同的流程：資料與附件在同一個資料庫交易寫入，任何一步失敗就整筆不存（版本號不變，可直接重送）；稽核依序是 `Project.Create`／`Project.Update`，有新附件再一筆 `Project.FileUpload count=N`，有移除再一筆 `Project.FileDelete count=N`。
  單一請求上限 4 GB（`ProjectController.MaxRequestBytes` = `uint.MaxValue`，IIS 允許的最大值，`web.config` 的 `requestLimits` 與它一致）；單一附件上限 1 GB 由服務檢查，超過回 400「檔案 X 超過 1GB 限制」；副檔名不在允許清單回 400「檔案 X 的類型不在允許清單中。」，整筆都不存。其他端點仍是 ASP.NET Core 預設的約 30 MB 上限。

> **樂觀並行（0.9.93 起）**：`GET` 回傳的 DTO 帶有 `ConcurrencyStamp`。`PUT` 必須把它原樣帶回；伺服器以它比對資料庫目前的值，不同代表別人已先修改或刪除，回 409（訊息「這筆資料在你編輯期間已被其他人修改或刪除…」），用戶端應重新 `GET` 後再決定如何合併。`PUT` 成功後版本號會換新，要再次修改請重新 `GET`。`POST` 會忽略這個欄位。
>
> **軟刪除（0.9.94 起）**：`DELETE` 只把資料標記為已刪除，之後對同一筆的 `GET`／`PUT`／`DELETE` 都回 404。分類、團隊 0.10.2 起、專案 0.10.3 起另有還原與永久刪除的端點；`ProjectDto` 0.10.3 起也多了 `IsDeleted`、`DeletedAt`、`DeletedBy`。
>
> 0.9.93 之前三個 `POST` 會直接採用客戶端傳來的 `Id`，與既有資料撞號時回 500；0.9.94 起一律由資料庫配號。

## 三、一次性下載票證 `DownloadsController`（0.10.3 起）

`src/MyProject/MyProject.Web/Controllers/DownloadsController.cs`，路由 `api/v1/downloads`（**無** `api/` 平行路由）；票證由 `MyProject.Web/Downloads/DownloadTicketService.cs` 發給與兌換。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 以票證下載 | `GET api/v1/downloads/{ticket}` | `[AllowAnonymous]`（票證本身就是憑證）| 成功回**原生檔案串流**（`Content-Disposition: attachment`，不包 `ApiResult`）；任何失敗都回同一個 `ApiResult` 404「下載連結無效或已過期，請回到頁面重新下載。」|

為什麼這樣做：瀏覽器原生下載（`<a download>`）帶不了 `Authorization` 標頭，而大檔不能先讀進 WASM 記憶體。所以先用 Bearer 申請票證（`POST Project/files/{fileId}/ticket`），再讓瀏覽器直接下載票證網址。

- **票證**：32 個隨機位元組（URL-safe Base64），60 秒內有效，兌換時就移除（只能用一次，重放回 404）；發新票證時順手清掉過期沒用的。存在單一行程的記憶體：IIS 應用程式集區要維持一個背景工作處理序（預設值）；集區回收只會讓還沒用的票證失效，使用者重按一次即可。
- **兌換時重新檢查**：以發票證時記下的使用者 Id 與安全戳記，經 `ICurrentUserLoader` 重新載入使用者——帳號停用、改密碼、被強制登出、角色變動都回 404，被要求先改密碼或先設定兩步驟驗證時也回 404；再確認仍有 `角色_專案項目` 的 View 權限，最後 `ProjectService.GetFileDownloadAsync` 再查一次團隊範圍、專案是否已刪除、實體檔是否存在。
- **失敗一律同一個 404**，不透露原因（原因只寫日誌）。成功下載寫稽核 `Project.FileDownload`（目標類型 `ProjectFile`、detail `file=原始檔名`）。
- **不支援續傳（Range）**：票證只能用一次，下載中斷要回頁面重新點檔名（使用者接受這個取捨）。M7 的備份下載會沿用同一套機制。
- **已知限制**：下載管理員或瀏覽器擴充功能若先預先抓取一次網址，會把票證用掉，真正的下載就得到 404；使用者再點一次檔名即可。

> **0.10.3 移除**：舊的 `ProjectFileController`（`GET api/project-files/{id}/download`、`api/v1/project-files/{id}/download`，以 Cookie 驗證、支援續傳）已刪除，舊網址回 404。

> **系統備份下載 `BackupController`（0.9.99 起，M7 改用下載票證）**：`src/MyProject/MyProject.Web/Controllers/BackupController.cs`，路由 `api/backups`（**無** `api/v1` 平行路由）。`GET backups/{fileName}/download`，同樣走 `[Authorize(CookieScheme)]`、成功回原生檔案（`application/zip`，支援續傳）；不用 `[HasPermission]`，而是每次從資料庫確認是**管理員**，否則回 `ApiResult` 403；找不到備份回 `ApiResult` 404。第一段下載寫入稽核 `Backup.Download`（續傳的後續區段不重複寫）。

## 四、認證控制器 `AuthController`

`src/MyProject/MyProject.Web/Controllers/AuthController.cs`，路由 `api/Auth`、`api/v1/Auth`；帳密換 token。0.10.0 起刷新 token 存在伺服器端、每次使用就輪替（規則見《[認證授權與權限機制](../security/認證授權與權限機制.md)》「刷新 Token」）。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 登入 | `POST Auth/login` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（失敗回 `Unauthorized`；已啟用兩步驟驗證要在 body 帶 `TwoFactorCode`，沒帶回 401「需要兩步驟驗證碼。」且不計入失敗次數；必須使用兩步驟驗證卻還沒設定的帳號一律 401，0.9.104 起）。0.10.0 起 `refreshToken` 是不透明字串、`isPersistent=true` |
| 換發 Token | `POST Auth/refresh` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`，回傳新的一組 token，舊的刷新 token 隨即失效（0.10.0 起輪替）。不認得、已過期或已撤銷、帳號已停用或刪除、工作階段已失效都回 `Unauthorized`「Refresh Token 無效或已過期。」並寫稽核 `Token.RefreshFailed`；已被換掉的舊 token 又被使用時整串撤銷並寫 `Token.ReuseDetected` |
| 登出 | `POST Auth/logout` | `[AllowAnonymous]`＋`[AllowDuringForcedSetup]` | 0.10.0 起。body 帶 `RefreshToken`，撤銷所屬整串；一律回 `ApiResult` 成功（避免試探），認得時寫稽核 `Logout`（`via=api`） |
| 網頁 Cookie 換 Token | `POST Auth/bridge` | `[AllowAnonymous]`（自己驗證 Cookie）＋`[AllowDuringForcedSetup]` | 0.10.0 起，WASM 遷移過渡期。必須帶標頭 `X-MyProject-Client`，沒帶回 400；Cookie 無效回 401。回 `ApiResult<TokenResponseDto>`，`isPersistent` 沿用 Cookie 的「記住我」|
| 目前使用者 | `GET Auth/me` | `[Authorize(JwtBearer)]`＋`[AllowDuringForcedSetup]` | 0.10.0 起 `ApiResult<SessionInfoDto>`（從資料庫讀：身分、有效權限、有效團隊、`mustChangePassword`、`requiresTwoFactorSetup`、`twoFactorEnabled`；以前是 `CurrentUserDto`、只讀 JWT Claims）|

## 五、Google 第三方登入 `ExternalAuthController`

`src/MyProject/MyProject.Web/Controllers/ExternalAuthController.cs`，路由前綴 `Auths/Google`。**此為網頁 Cookie 登入導向端點，非 API**（回傳 `Challenge`／`Redirect`，不走 `ApiResult`）。詳見《[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)》。

| 動作 | 路由 | 說明 |
|------|------|------|
| 觸發 Google 驗證 | `GET Auths/Google/Login` | 未設定金鑰時導回 `/Auths/Login`|
| 驗證回呼 | `GET Auths/Google/Callback` | 查找/建立帳號；依 `ExternalLoginResult.Evaluate` 的順序：已刪除導回 `/Auths/Login?sso=deleted`、停用（待開通）導向 `/Auths/Pending`、鎖定導回 `/Auths/Login?sso=locked`；已啟用兩步驟驗證且這台裝置沒被記住時導向 `/Auths/TwoFactor`，其餘完成 Cookie 登入 |

## 五之一、WASM 外框、個人資料與異動歷程（0.10.1 起，WASM 遷移 M2；異動歷程 0.10.2 起）

只有 `api/v1` 路由；一律 `[Authorize(JwtBearer)]`（`shell/culture` 例外），只能讀改**自己**的資料（使用者一律取自 `CurrentUserService`，由 `ApiActorMiddleware` 依存取 Token 載入），回傳 `ApiResult<T>`。
時間欄位（`*Local`）已換成伺服器的本地時間，前端照字面顯示。DTO 在 `MyProject.Dtos/Shell`。

| 控制器 | 動作 | 路由 | 回傳／說明 |
|---|---|---|---|
| `ShellController` | 系統資訊 | `GET shell/info` | `ShellInfoDto`：系統名稱、描述、版本、環境、啟動時間、各頁說明主題（路由、標題）|
| | 選單 | `GET shell/menu` | `ShellMenuDto`：依目前使用者權限過濾後的選單（規則同 Server 版側邊欄的 `CheckAccessPage`），以及全部選單網址 |
| | 操作說明 | `GET shell/help?route=` | `HelpDocumentDto`：Markdown 已在伺服器端以 `HelpMarkdownRenderer`（停用 HTML）轉成 HTML，相關頁面已依權限過濾；沒有說明回 404 |
| | 開頁稽核 | `POST shell/page-view` | 寫 `Page.View`（路徑去掉 query string；只有 `AuditLogSettings:RecordBrowsing` 開啟才會留下，規則同 Server 版電路）。只接受已搬到 WASM 的頁面路徑（`ClientRouteTable`），每位使用者每分鐘最多 60 筆，超過的不記。0.10.2 起使用者沒有該頁權限時（依選單的權限鍵，`SidebarMenuService.FindPagePermissionAsync`）改寫「沒有權限開頁」稽核 `Permission.Denied`（detail `via=WASM`），WASM 頁面不自己寫 |
| | 日期時間格式 | `GET shell/culture` | 0.10.2 起，**不需登入**（`[AllowAnonymous]`）。`DateTimeFormatDto`：伺服器（zh-TW）的日期時間格式；WASM 在啟動前讀一次（`ClientCulture`），讓清單的時間顯示與 Server 版相同。內容只有格式字串 |
| | 瀏覽器錯誤回報 | `POST shell/client-errors` | 交給 `BrowserErrorReporter`（同一套截斷、開關與系統例外紀錄）。每位使用者每分鐘上限沿用 `ClientErrorReporting:MaxPerCircuitPerMinute`（`PerUserRateLimiter` 跨請求計數），超過回 `accepted=false` |
| `NotificationsController` | 摘要 | `GET notifications/summary` | 未讀數、最新 10 則、有沒有已讀的；連結只保留安全的站內網址（`ReturnUrlGuard`），否則為 null |
| | 標為已讀／全部已讀 | `POST notifications/{id}/read`、`POST notifications/read-all` | |
| | 刪除一則／清除已讀 | `DELETE notifications/{id}`、`DELETE notifications/read` | 不是自己的或找不到回 404 |
| `AnnouncementsController` | 目前公告 | `GET announcements/active` | 挑選規則與 Server 版公告橫幅共用 `AnnouncementBanner.Select`（時間、對象、沒被關閉）|
| | 關閉 | `POST announcements/{id}/dismiss` | |
| `ProfileController` | 個人資料 | `GET profile` | `ProfileDto`：帳號、姓名、Email、角色、團隊、密碼與兩步驟驗證狀態、最近 20 筆登入紀錄（動作已轉成中文）|
| | 改姓名 | `PUT profile/name` | body 帶 `ConcurrencyStamp`；別人同時改過或姓名不合規則回 400 |
| `AuditLogsController` | 某筆資料的異動歷程 | `GET audit-logs/history?targetType=&targetId=&page=&pageSize=&opened=` | 0.10.2 起，**只限管理員**（與「稽核紀錄」頁相同，非管理員回 403）。`PagedResult<AuditHistoryItemDto>`，新的在前、不含搜尋與查看；`opened=true`（打開對話窗）時寫一筆 `Audit.Search`，換頁不帶。每頁最多 50 筆 |
| | 一筆稽核的完整內容 | `GET audit-logs/{id}/content` | 0.10.2 起，只限管理員。`AuditContentDto`（修改前後差異等長內容）；寫一筆 `Audit.View`。伺服器上的檔案路徑不回傳（清單只給 `HasContent`）|

## 六、Controller 端預設拒絕（0.4.34 起）

`Program.cs` 的 `app.MapControllers().RequireAuthorization()` 讓**未明確標註授權的 Controller 一律拒絕**。
需要匿名存取的端點必須自己標 `[AllowAnonymous]`（目前為 `AuthController` 的 login/refresh/logout/bridge、`ShellController` 的 culture（0.10.2 起）、整個 `DownloadsController`（0.10.3 起，以一次性票證當憑證）與整個 `ExternalAuthController`）。

0.10.0 起，以 access token 呼叫且使用者被要求先改密碼或先設定兩步驟驗證時，全域的 `ApiSessionGateFilter` 讓業務 API 一律回 403 `ApiResult`（`errorCode`：`PASSWORD_CHANGE_REQUIRED`／`TWO_FACTOR_SETUP_REQUIRED`），只有標 `[AllowDuringForcedSetup]` 的端點放行。

> 刻意**只**套在 Controller 上，不使用 `AuthorizationOptions.FallbackPolicy`。Blazor 頁面另有自己的
> 「預設需登入」機制（0.9.41 起）：`Components/Pages/_Imports.razor` 對整個 `Pages/` 標 `[Authorize]`，
> 登入頁等匿名頁面各自標 `[AllowAnonymous]` 並由 `PageAuthorizationTests` 白名單守門。
> 詳見 [開發慣例與限制速查 §5.1](開發慣例與限制速查.md)。

> 0.4.34 移除了模板遺留的 `WeatherForecastController`（無授權標註、路由不在 `/api` 之下、回傳裸物件）。

## 七、相關文件

- [Web API 設計慣例](Web%20API%20設計慣例.md)
- [API Versioning 策略](API%20Versioning%20策略.md)
- [認證授權與權限機制](../security/認證授權與權限機制.md)
- [紀錄分類與團隊權控 PRD](../prd/紀錄分類與團隊權控-prd.md)

> 返回 [architecture 索引](README.md)
