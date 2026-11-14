# Web API 端點目錄

- 文件版本：1.22
- 文件狀態：已實作
- 現行系統版本：0.10.18
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/14

本文件彙整 `MyProject.Web/Controllers/` 下所有 Web API 端點的實際路由、HTTP 動詞、授權與回傳型別，作為《[Web API 設計慣例](Web%20API%20設計慣例.md)》（樣板與慣例）之外的**端點清單參照**。慣例細節（`ApiResult<T>`、`PagedResult<T>`、Search DTO、動作級授權）見設計慣例文件。

## 一、通則

- 每個資源控制器同時掛 `api/[controller]` 與 `api/v1/[controller]` 兩條平行路由（見《[API Versioning 策略](API%20Versioning%20策略.md)》）；`DownloadsController` 只有 `api/v1/downloads`（0.10.3 起）。WASM 遷移期間新增的控制器（第二之一～二之四節、第四之一節、第五之一節）都只有 `api/v1`。
- 資源控制器類別層級套 `[ApiController]`、`[ApiValidationFilter]`、`[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`（API 用 JWT Bearer）。**例外是 `DownloadsController`：匿名、以一次性票證當憑證（第三節）。** 0.10.6 起已沒有以 Cookie 驗證的 API（專案附件 0.10.3、系統備份 0.10.6 改用下載票證）；0.10.8 起網頁也不再發登入 Cookie，預設驗證機制是 JWT（WASM 遷移 M9）；0.10.9 起 `HasPermissionAttribute` 與 `RequestActorResolver` 只認 JWT 身分（登入 Cookie 的 `Sid` 分支移除，Google 登入過程的 5 分鐘外部 Cookie 不經過它們），主機也不再有 Blazor Server 的 SignalR 端點 `/_blazor`（WASM 遷移 M10）；0.10.10 起（WASM 遷移 M11）主機沒有任何 Razor 元件或主機頁，只提供 API（同站台時另外提供前端的靜態檔，`ApiOnlyHostTests` 守門）。
- **`/api` 底下找不到的路由**（0.10.10 起）：沒有本文的錯誤狀態碼（找不到路由 404、方法不符 405…）由 `UseApiStatusCodePages` 補上 `ApiResult`（找不到時訊息「找不到這個 API。」）；0.10.9 以前是重跑成 `/not-found` 頁。其他有本文的錯誤（JWT 的 401／403、`ApiExceptionFilter`、`ApiValidationFilter`、`HasPermissionAttribute`、限流）不受影響。
- **跨來源（CORS，0.10.10 起）**：同站台（`ClientSettings:BaseUrl` 留空，預設）不開放、不回任何 CORS 標頭；雙站台時只允許 `ClientSettings:BaseUrl` 的來源加上 `Cors:AllowedOrigins`，只收 `Authorization`、`Content-Type` 標頭與 GET／POST／PUT／DELETE，公開 `Content-Disposition`（前端讀下載檔名），不帶憑證；預檢在驗證與限流之前回應（`DualSiteHostTests` 守門）。雙站台時主機不提供前端的檔案，頁面網址（`/`、`/projects`、`index.html`、`theme.css`…）一律 404；Google 登入（第五節）、`/health`、Swagger 照舊在主機上。
- 每個動作以 `[HasPermission(資源鍵, 動作)]` 做動作級授權；無權限回 **403** 並維持 `ApiResult` 外殼；管理員短路。權限鍵定義於 `MyProject.Share` 的 `MagicObjectHelper`，動作為 `PermissionActions.View/Create/Edit/Delete`。
- **每個 action 都要標明誰能呼叫**（0.10.4 起，`ApiAuthorizationConventionTests` 守門）：`[HasPermission]`（頁面或動作權限）、`[AdminOnly]`（只有管理員，見第二之一節）、`[SignedInUser]`（登入即可、刻意不檢查頁面權限，例如個人資料、外框、通知、公告、`Auth/me`）或 `[AllowAnonymous]`；標在類別上等於標在每個 action。四個都沒標的 action，測試直接失敗。兩個新屬性在 `MyProject.Web/Filters/AdminOnlyAttribute.cs`。
- 回傳一律包在 `ApiResult<T>`；分頁再包 `PagedResult<T>`。
- ⚠️ **新增不在 `/api/` 底下的伺服器端點**（0.10.11 起，例如第五節 `Auths/Google` 這類網頁導向端點、`Program.cs` 的 OAuth `CallbackPath` 或 `MapHealthChecks`）：要把路徑加進前端 Service Worker（`MyProject.Web.Client/wwwroot/service-worker.published.js`）的 `networkOnlyPaths`，否則同站台時瀏覽器導覽到這個網址會被 Service Worker 回成快取的前端 `index.html`（`PwaConventionTests` 啟動主機、從實際註冊的端點檢查）。`/api/` 底下的端點已整段排除，不必另外登記。前端也不可寫死這類路徑，一律經 `ApiAddress`（`ServerUrlConventionTests`）。

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
0.10.15 起分類的「適用團隊」（`Teams`）**新選的**團隊已不存在或已停用時，新增、修改與 `POST check` 回 400「所選的團隊「X」已不存在或已停用，請重新選擇（選項已更新）。」（`ErrorMessage`；原本就掛著的不檢查；速查表 §6.33 第 127 點）。CRUD 產生器以 `-WithTeams` 產生的模組相同。

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET {controller}/{id}` | View | `ApiResult<TDto>`（查無或範圍外回 404）；寫一筆「查看」稽核（`*.View`）|
| 搜尋分頁 | `POST {controller}/search` | View | `ApiResult<PagedResult<TDto>>`。0.10.2 起多三個欄位：`Deleted`（查「顯示已刪除」，**要有 Delete 權限**，沒有回 403）、`RecordSearch`（true 時寫一筆搜尋稽核 `*.Search`；換頁、排序不帶）、`SortBy` 不分大小寫（可用 `name`、`isEnabled`、`createdAt`、`updatedAt`，團隊另有 `code`；不認得的退回預設排序）|
| 存檔前檢查 | `POST {controller}/check` | View＋（`Id=0` 要 Create、`Id>0` 要 Edit，否則 403）| `ApiResult`：名稱（團隊另有代號）會不會重複；修改時範圍外的 `Id` 回 404。WASM 頁在存檔確認流程中呼叫 |
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
- 0.10.17 起標題最多 200 字（去頭尾空白後），新增或改標題時超過回 400「專案標題長度不可超過 200 字元。」；以前存下的長標題不改標題時照常可存。
- 0.10.15 起**新選的**分類或團隊已不存在或已停用時回 400「所選的分類「X」已不存在或已停用，請重新選擇（選項已更新）。」（團隊同理；原本就掛著的不檢查，舊專案照樣能存；速查表 §6.33 第 127 點）。
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

## 二之一、只限管理員的管理頁 API（0.10.4 起，WASM 遷移 M5）

使用者、角色、稽核紀錄、寄信紀錄四頁改在 WebAssembly 執行，背後是下面這組 API。只有 `api/v1` 路由，類別層級標 **`[AdminOnly]`**（稽核紀錄是每個 action 各自標）與 `[ApiValidationFilter]`：

- 沒登入回 401；不是管理員回 **403** `ApiResult`（「只有管理員可以使用這個功能。」；異動歷程的兩個端點是「只有管理員可以查看異動歷程。」），並寫一筆 `Permission.Denied`（目標 `ApiEndpoint`／請求路徑，detail「需要管理員」）。
- 「是不是管理員」看 `ApiActorMiddleware` 依存取 Token 從資料庫載入的目前使用者，剛被取消管理員身分的人下一個請求就被擋。
- 這四頁的權限鍵刻意不放進角色矩陣（不變），所以不用 `[HasPermission]`。
- 搜尋的每頁筆數 1～200、頁碼 ≥ 1，不合法回 400（`[ApiValidationFilter]`）；畫面上的必填（使用者的帳號、名稱、角色，角色的名稱）由伺服器再驗一次，缺了回 400。
- **時間**：本地時間一律以不帶時差（`DateTimeKind.Unspecified`，JSON 沒有 `+08:00`）送出，前端照字面顯示；UTC 欄位（`*Utc`）以 `Z` 結尾。送來的日期篩選也不帶時差，伺服器照自己的時區解讀。
- 失敗依服務回傳的 `VerifyRecordResult.Kind` 決定狀態碼（找不到 404、重複或版本衝突 409、規則不符 400、未預期 500；0.10.4 起 `MyUserService`、`RoleViewService`、`TwoFactorService` 的失敗都標上 Kind），錯誤訊息就是畫面那一句。
- 0.10.17 起使用者帳號只能用英文字母、數字與 `- _ . @`：新增（含 `POST users/check`）或修改時改了帳號，格式不符回 400「帳號只能使用英文字母、數字與 - _ . @。」；既有帳號不改帳號時不檢查（例如 Google 登入以 Email 建立、含 `+` 的）。
- 0.10.15 起使用者的團隊（`TeamNames`）、角色的預設團隊（`DefaultTeams`）**新選的**團隊已不存在或已停用時，新增、修改（使用者另含 `POST users/check`）回 400「所選的團隊「X」已不存在或已停用，請重新選擇（選項已更新）。」（原本就掛著的不檢查；速查表 §6.33 第 127 點）。
- 寫入的稽核由服務寫（與畫面相同、每個動作一筆）；查看、搜尋、匯出由 Controller 寫。
- DTO 在 `MyProject.Dtos/Admins`；WASM 端的呼叫在 `MyProject.Web.Client/Api/UserApi.cs`、`RoleApi.cs`、`AdminLogApi.cs`，DTO 與畫面模型的對應在 `UserDtoMapper`、`RoleDtoMapper`（WASM 與 Controller 共用）。

### 2.3 使用者（`UsersController`，`api/v1/users`）

走與畫面相同的 `MyUserService`／`ITwoFactorService`（support 與自己的保護、密碼原則與歷史、角色與團隊同一個交易、並行衝突、軟刪除）。

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋分頁 | `POST users/search` | `ApiResult<PagedResult<UserDto>>`。`Deleted`（顯示已刪除）、`RecordSearch`（true 時寫 `User.Search`）、`SortBy` 不分大小寫（`account`、`name`、`email`、`roleViewName`、`statusText`、`isAdminText`、`createAt`、`updateAt`）。每列帶這一列能做的操作 `CanDelete`／`CanCopy`／`CanToggleStatus`／`CanForceLogout`／`CanResetTwoFactor`，以及鎖定中的文字 `LockedText`（「鎖定至 HH:mm」，不是今天時「鎖定至 MM-dd HH:mm」，**依伺服器時區算好**）；`Password` 一律是空的 |
| 取得單筆 | `GET users/{id}` | `ApiResult<UserDto>`，含額外角色 `AdditionalRoleIds` 與團隊 `TeamNames`（WASM 頁開修改窗時呼叫）；寫 `User.View`；找不到回 404 |
| 表單選項 | `GET users/options` | `ApiResult<UserFormOptionsDto>`：角色（含各角色的預設團隊）、啟用中的團隊、新增時的預設角色 `DefaultRoleId`、密碼規則說明 `PasswordRule` |
| 產生密碼 | `GET users/password/generate` | `ApiResult<string>`：一組符合密碼原則的隨機密碼（「複製」的「產生」用，不存）；回應帶 `Cache-Control: no-store`、`Pragma: no-cache` |
| 新增前檢查 | `POST users/check` | body 同新增的 `UserDto`；`ApiResult`：帳號是否已存在（409）、Email 格式等規則（400）。「複製」建立時的順序：先呼叫這裡檢查帳號，再問「Email 已有人使用」，最後才新增 |
| 複製範本 | `GET users/{id}/copy-draft` | `ApiResult<UserCopyDraftDto>`：範本 `Draft` 與已不存在而略過的團隊 `SkippedTeamNames`；support 或已不存在回 404 |
| 誰在用這個 Email | `POST users/accounts-using-email` | body `{ "email": "…" }`，回帳號清單（複製建立前提醒「重設信會寄到同一個信箱」）|
| 新增 | `POST users` | `ApiResult<UserDto>`。沒帶密碼、帳號／名稱／角色沒填回 400；帳號重複 409。一般新增與「複製」共用，`CopiedFromAccount` 有值時 `User.Create` 稽核記 `copiedFrom` |
| 修改 | `PUT users/{id}` | `ApiResult<UserSaveResultDto>`。路由／資料 ID 不符回 400；版本號不符 409。**改到自己**且工作階段版本換掉（改了自己的密碼、角色或管理員身分）時回 `Tokens`（0.10.8 起：這台的新 Token，舊的一串撤銷、沿用「記住我」與整串上限），WASM 頁存起來後整頁重新載入；其他情況是 null。0.10.4～0.10.7 回的 `SessionRefreshUrl`（`/Auths/RefreshSession` 換發 Cookie）已移除。support 改帳號回 400「「support」是系統預設的開發帳號，不可修改帳號。」|
| 啟用／停用 | `PUT users/{id}/enabled` | body `{ "enabled": true/false }`；support 與自己不可停用（400）|
| 解除鎖定 | `POST users/{id}/unlock` | |
| 強制登出 | `POST users/{id}/force-logout` | 對自己回 400「不可強制登出自己的帳號，請使用右上角的登出。」（0.10.4 起 API 也擋；Server 版只在畫面擋）|
| 重設兩步驟驗證 | `POST users/{id}/two-factor/reset` | 對自己回 400「不可重設自己的兩步驟驗證，請到「兩步驟驗證」頁停用或重新設定。」（同上）|
| 刪除 | `DELETE users/{id}` | 軟刪除；support 與自己不可刪（400）|
| 還原／永久刪除 | `POST users/{id}/restore`、`DELETE users/{id}/purge` | 永久刪除只能對已刪除的使用者，角色與團隊設定一併刪除 |
| 匯出 | `POST users/export` | 成功回 xlsx（不包 `ApiResult`），條件與搜尋相同；超過上限回 400 並帶原因；寫 `User.Export` |

> 停用、強制登出、重設兩步驟驗證、改別人的密碼／角色／管理員身分都會換**對方**的工作階段版本：對方下一個請求就被登出（API 401、刷新 Token 被拒）。改自己時只有這台裝置拿到新 Token（0.10.8 起；以前經 `SessionRefreshUrl` 換發 Cookie），其他裝置維持登出。

### 2.4 角色（`RolesController`，`api/v1/roles`）

走與畫面相同的 `RoleViewService`（權限矩陣轉權限鍵並同步 RBAC 表、預設角色不可刪也不可改名、仍是某人主要角色的不可刪、並行衝突、軟刪除）。角色的變更不換任何人的工作階段版本：有效權限每個請求重新讀取，下一個請求就生效。

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋分頁 | `POST roles/search` | `ApiResult<PagedResult<RoleDto>>`，每列含權限矩陣（「複製」直接用它）；`Deleted`、`RecordSearch`（寫 `Role.Search`）、`SortBy`（`name`、`createAt`、`updateAt`）|
| 取得單筆 | `GET roles/{id}` | `ApiResult<RoleDto>`（開修改窗時取最新的矩陣與版本號）；寫 `Role.View` |
| 表單選項 | `GET roles/options` | `ApiResult<RoleFormOptionsDto>`：啟用中的團隊與空白的權限矩陣 `EmptyPermission`（新增用）|
| 新增／修改 | `POST roles`、`PUT roles/{id}` | 名稱沒填、預設角色改名 400；名稱重複、版本號不符 409。修改不覆寫建立時間 |
| 刪除 | `DELETE roles/{id}` | 軟刪除；預設角色、仍是某人主要角色的回 400（訊息列出前 5 個帳號）|
| 還原／永久刪除 | `POST roles/{id}/restore`、`DELETE roles/{id}/purge` | 永久刪除只能對已刪除的角色；權限設定與「額外角色」關聯一併刪除 |

`RoleDto.Permission` 是**樹狀**的權限矩陣（`RolePermissionDto` → 群組 `Groups[]`（`Name`、`Enable`）→ 頁面 `Permissions[]`（`Name`、`Enable`）→ 動作 `Actions`（動作名稱 → 是否勾選））；伺服器把它轉成權限鍵，規則與畫面相同。⚠️ **只採納權限目錄**：送來的矩陣套到 `RolePermissionService.InitializePermissionSetting()` 的範本上，目錄裡沒有的群組、頁面、動作一律忽略——呼叫端不能多塞「稽核紀錄」這類管理員專屬鍵讓它被授予。

### 2.5 稽核紀錄（`AuditLogsController`，`api/v1/audit-logs`）

0.10.2 起已有異動歷程用的 `history` 與 `{id}/content`（見第五之一節），0.10.4 起補上稽核紀錄頁其他的端點，每個 action 都標 `[AdminOnly]`。走 `AuditLogQueryService`、`AuditIntegrityVerifier`。

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋分頁 | `POST audit-logs/search` | `ApiResult<PagedResult<AuditLogItemDto>>`；條件：起訖時間、動作、成功與否、帳號、IP、關鍵字、目標類型、目標識別（精確）。`RecordSearch` 為 true 時寫 `Audit.Search`，自由輸入的欄位（帳號、IP、關鍵字、目標識別）只記有沒有填。每列另帶 `OccurredAtUtc`（伺服器算好）與簽章 |
| 查詢選項 | `GET audit-logs/options` | `ApiResult<AuditLogOptionsDto>`：資料庫裡出現過的動作與目標類型、清除用的兩個保留天數（`PurgeDays`、`BrowsingPurgeDays`）、記錄範圍提示 `ScopeNotice` |
| 明細 | `GET audit-logs/{id}` | `ApiResult<AuditLogDetailDto>`：一筆稽核與它的詳細內容，本地時間與 UTC 時間都由伺服器算；寫 `Audit.View`；找不到回 404 |
| 匯出 | `POST audit-logs/export` | 目前條件下的資料，CSV（UTF-8 含 BOM，只匯前 10000 筆；欄位定義在 `MyProject.Web/Export/AuditLogExport.cs`），不包 `ApiResult`；寫 `Audit.Export`，detail 記實際匯出筆數（`format=csv; rows=N`），被截掉時另記總筆數（`rows=10000; total=N`）|
| 清除 | `POST audit-logs/purge` | 先清超過保留天數的異動與安全紀錄，成功後才清瀏覽紀錄，兩段各寫一筆 `Audit.Purge`；回 `AuditLogPurgeResultDto` |
| 清空全部 | `POST audit-logs/clear-all` | 清空後寫一筆 `Audit.ClearAll`（清空後查得到是誰清的）|
| 驗證完整性 | `POST audit-logs/verify` | 重算全部簽章（只比對、不更新檢查點），寫 `Audit.Verify`；回 `AuditIntegrityReportDto`。時間與筆數成正比，WASM 端這個請求最長等 30 分鐘（`ApiClient.LongRunningTimeout`），不受一般 100 秒逾時限制 |

### 2.6 寄信紀錄（`EmailLogsController`，`api/v1/email-logs`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋分頁 | `POST email-logs/search` | `ApiResult<PagedResult<EmailLogItemDto>>`（時間欄位不帶時差）：依建立時間新到舊；條件為起訖時間（`FromLocal`、`ToLocal`）、狀態、種類、關鍵字（比對收件者與主旨），每頁 1～200 筆。唯讀、不寫稽核 |

## 二之二、日誌與 AI 管理頁 API（0.10.5 起，WASM 遷移 M6）

日誌檢視、日誌等級設定、系統例外紀錄、AI 對話紀錄、Token 用量、AI 提示詞六頁改在 WebAssembly 執行，背後是下面這組 API。規則與第二之一節相同：只有 `api/v1` 路由、每個 action 都是 **`[AdminOnly]`**（非管理員 403 並寫 `Permission.Denied`，detail「需要管理員」）、套 `[ApiValidationFilter]`、回 `ApiResult`；時間是伺服器本地時間、以不帶時差（`DateTimeKind.Unspecified`）送出。

- **AI 分析回 NDJSON 串流**（`application/x-ndjson`，見《[Web API 設計慣例](Web%20API%20設計慣例.md)》「串流回應」）：每行一個 `AiStreamFrameDto`，`Kind` 依序是 `start`（帶 `AiStreamScopeDto`，例如日誌分析實際用的時間範圍）、零到多個 `delta`（`Text` 是新收到的一段文字）、最後一定是一筆 `result`（`AiResultDto`：成功與否、失敗原因、模型名稱、用量 `AiUsageDto`）。請求在進入串流之前就被拒絕時（403、404、400），回的仍是一般 `ApiResult`。
- **停止產生／關窗＝中斷請求**：伺服器的 `RequestAborted` 取消上游 AI 呼叫，最後一筆是「已取消」的結果，稽核與記帳照寫；畫面收不到那筆結果，用已收到的文字自己收尾。
- **「刪除選取」沒有批次端點**：與 M5 相同，畫面逐筆呼叫 `DELETE {id}`，每筆一列稽核。
- 匯出的 CSV 欄位定義在 `MyProject.Web/Export/`（`ExceptionLogExport`、`AiCallLogExport`、`TokenUsageExport`）；`AiCallLogService`、`TokenUsageLogService`、`PromptTemplateService` 的失敗標上 `Kind`（404／409／400／500）。
- DTO 在 `MyProject.Dtos/Admins`（例外、日誌、Token 用量、提示詞）與 `MyProject.Dtos/Ai`（串流、AI 對話紀錄）；WASM 端的呼叫在 `MyProject.Web.Client/Api/ExceptionLogApi.cs`、`LogApi.cs`、`AiCallLogApi.cs`、`TokenUsageApi.cs`、`PromptTemplateApi.cs`。

### 2.7 系統例外紀錄（`ExceptionLogsController`，`api/v1/exception-logs`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋分頁 | `POST exception-logs/search` | `ApiResult<PagedResult<ExceptionLogItemDto>>`；`RecordSearch` 為 true 時才寫 `ExceptionLog.Search`。排序（最後發生、次數）依點選方向生效 |
| 選項 | `GET exception-logs/options` | `ApiResult<ExceptionLogOptionsDto>`：清除天數 `PurgeDays`、匯出與複製上限 `MaxRows`、AI 是否可用 `AiAvailable` 與原因 `AiUnavailableReason`、追問輪數上限 `MaxFollowUpRounds`（開頁與重新整理時讀）|
| 明細 | `GET exception-logs/{id}` | `ApiResult<ExceptionLogDetailDto>`，含堆疊；寫 `ExceptionLog.View` |
| 堆疊 | `GET exception-logs/{id}/stack-trace` | 點列複製時取完整堆疊，不寫稽核 |
| 複製目前查詢結果 | `POST exception-logs/copy-text` | `ApiResult<ExceptionLogCopyTextDto>`：條件同搜尋，最多 `MaxRows` 筆（含堆疊）|
| 匯出 | `POST exception-logs/export` | CSV（不包 `ApiResult`），寫 `ExceptionLog.Export`（`format=csv; rows=N`）|
| 刪除／清除／清空 | `DELETE exception-logs/{id}`、`POST exception-logs/purge`、`POST exception-logs/clear-all` | 各寫 `ExceptionLog.Delete`／`Purge`／`ClearAll` |
| AI 例外分析與追問 | `POST exception-logs/{id}/ai` | **NDJSON 串流**。body `ExceptionAiRequestDto`：`ConversationId`（開窗時產生，第一次分析與每輪追問共用）與**例外明細之後**的問答 `Messages`（AI 回覆、追問交替，以這次的追問結尾；第一次分析為空）。第一則例外明細（含堆疊檔）與 system 提示由伺服器依資料庫重組，畫面改不到。伺服器檢查角色交替、追問 ≤ 2,000 字、單則回覆 ≤ 512K 字、追問輪數 ≤ 上限，不合格回 400；寫 `ExceptionLog.AiAnalyze`（第一次）或 `ExceptionLog.AiFollowUp` |
| AI 分析 PDF | `POST exception-logs/{id}/ai/pdf` | body 是畫面上的對話（被停止的回覆標 `Interrupted`）、模型與累計用量；伺服器補上例外明細與堆疊；寫 `ExceptionLog.AiExportPdf`。**不受追問輪數上限限制**（對話途中調低上限仍可匯出）|

> 設計：遷移分析報告原本寫「對話存在伺服器快取」，實作改為**畫面送回問答、伺服器重建明細與系統提示**——不怕 IIS 回收、伺服器無狀態，畫面也改不到例外內容與系統提示。

### 2.8 日誌檢視與日誌等級（`LogsController`，`api/v1/logs`；`LogLevelController`，`api/v1/log-level`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋 | `POST logs/search` | `ApiResult<LogSearchResultDto>`。快速區間（`last5m`、`last15m`、`last1h`、`today`）或沒填起訖時的預設「最近 1 小時」，「現在」由伺服器算；回實際套用的起訖（畫面寫回欄位）與警告；`RecordSearch` 時寫 `LogViewer.Search` |
| 匯出 | `POST logs/export` | `.log`（UTF-8 含 BOM），伺服器依同條件重查；寫 `LogViewer.Export` |
| AI 日誌分析 | `POST logs/ai` | **NDJSON 串流**。伺服器依同條件重查、取最新的 `MaxEntries` 筆送給 AI；`start` 帶實際範圍；寫 `LogViewer.AiAnalyze` |
| AI 分析 PDF | `POST logs/ai/pdf` | body `LogAiReportPdfRequestDto`：條件、畫面上的報告 Markdown（≤ 512K 字）、模型名稱（≤ 200 字）、失敗原因（≤ 50 字），超過回 400；伺服器依條件重建 prompt 當附錄；寫 `LogViewer.AiAnalyzeExportPdf` |
| 選項 | `GET logs/options` | AI 是否可用與原因 |
| 讀取等級 | `GET log-level` | `ApiResult<LogLevelStateDto>` |
| 套用／還原 | `POST log-level/apply`、`POST log-level/restore` | 寫 `LogLevel.Apply`（「由 X 調整為 Y（僅執行期有效）」）／`LogLevel.Restore` |

### 2.9 AI 對話紀錄（`AiCallLogsController`，`api/v1/ai-call-logs`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 搜尋 | `POST ai-call-logs/search` | `ApiResult<AiCallLogPageDto>`。內文關鍵字由伺服器逐檔掃描（使用者離開或重新查詢時可取消）；搜尋稽核 `AiCallLog.Search` 寫在掃描**前**，掃描失敗退回重查也不重記。排序（送出時間、耗時、送出字元）依點選方向生效 |
| 選項 | `GET ai-call-logs/options` | 出現過的功能與模型、是否啟用、保留天數、內文掃描上限 |
| 深連結 | `GET ai-call-logs/by-call/{callId}` | 由 `CallId` 找紀錄 Id（其他頁的「看對話紀錄」用）|
| 明細 | `GET ai-call-logs/{id}` | `ApiResult<AiCallLogDetailDto>`，每段只送前 20 萬字元；寫 `AiCallLog.View` |
| 段落全文 | `GET ai-call-logs/{id}/text?part=` | 複製被截斷的段落時取全文 |
| 原文 JSON／PDF | `POST ai-call-logs/{id}/raw`、`POST ai-call-logs/{id}/pdf` | 檔案（不包 `ApiResult`）；寫 `AiCallLog.Export` |
| 合併 PDF | `POST ai-call-logs/merged-pdf` | `Source` 為 `selected`／`conversation`／`query`，最多 50 筆；寫 `AiCallLog.Export` |
| 匯出 | `POST ai-call-logs/export` | CSV（不含內文，上限 10000 筆）|
| 刪除／清除／清空 | `DELETE ai-call-logs/{id}`、`POST ai-call-logs/purge`、`POST ai-call-logs/clear-all` | 各寫 `AiCallLog.Delete`／`Purge`／`ClearAll` |

### 2.10 Token 用量（`TokenUsageController`，`api/v1/token-usage`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 儀表板 | `POST token-usage/dashboard` | `ApiResult<TokenUsageDashboardDto>`：明細一頁、合計、4 種分組、每日（補零，最多 180 天）、近 1／7／30 天卡片、用量上限文字；「今天」以伺服器時間計。排序（時間、費用）依點選方向生效；`RecordSearch` 時寫 `TokenUsage.Search` |
| 選項 | `GET token-usage/options` | 出現過的功能、呼叫種類、模型 |
| 明細 | `GET token-usage/{id}` | 原始 usage 平攤、費率快照、對話紀錄是否存在；寫 `TokenUsage.View` |
| 匯出 | `POST token-usage/export`、`POST token-usage/export-pdf` | CSV；PDF 可選全部或單一頁籤，**伺服器依同條件重查**（查詢與匯出之間有新紀錄時數字可能多幾筆）；寫 `TokenUsage.Export` |
| 刪除／清除／清空 | `DELETE token-usage/{id}`、`POST token-usage/purge`、`POST token-usage/clear-all` | 各寫 `TokenUsage.Delete`／`Purge`／`ClearAll` |

### 2.11 AI 提示詞（`PromptTemplatesController`，`api/v1/prompt-templates`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 全部 | `GET prompt-templates` | `ApiResult<List<PromptTemplateDto>>`：16 個鍵的標題、版本、內建預設、固定附加的安全規則 |
| 存新版本 | `POST prompt-templates/{key}/versions` | 樂觀鎖：別人先存了回 409；建立者由伺服器取目前帳號；寫 `Prompt.Update` |
| 啟用版本 | `POST prompt-templates/{key}/activate` | 寫 `Prompt.Activate` |

## 二之三、維運頁與首頁 API（0.10.6 起，WASM 遷移 M7）

排程作業、系統健康監控、系統備份、資料保留與清理、資料庫用量、系統參數、公告管理七頁與首頁 `/App` 改在 WebAssembly 執行，背後是下面這組 API。規則與第二之二節相同：只有 `api/v1` 路由、回 `ApiResult`、本地時間以不帶時差（`DateTimeKind.Unspecified`）送出；**除了首頁，每個 action 都是 `[AdminOnly]`**（非管理員 403 並寫 `Permission.Denied`，detail「需要管理員」）。

- **「現在」用伺服器時間**：資料保留的「今天」、公告的「現在」、警告日誌的「最近 24 小時」都由伺服器算好送來（或由伺服器驗證），不看瀏覽器時鐘。
- **稽核由伺服器寫**：首頁與資料庫用量不寫稽核。
- **背景作業放進排程佇列**：「立即執行」「立即備份」「立即清理」交給排程框架（與排程共用作業鎖），請求本身馬上回來；已在佇列或執行中回 409。畫面自己輪詢看進度（排程頁有作業排隊或執行中時每 2 秒，備份與資料保留頁每 5 秒）。
- **「刪除選取」沒有批次端點**：與 M5、M6 相同，畫面逐筆呼叫 `DELETE`，每筆一列稽核（公告、備份）。
- DTO 在 `MyProject.Dtos/Admins`（`ScheduledJobDtos`、`SystemHealthDtos`、`MaintenanceDtos`〔備份、資料保留、資料庫用量〕、`SystemParameterDtos`、`AnnouncementDtos`）與 `MyProject.Dtos/Shell/DashboardDtos.cs`；WASM 端的呼叫在 `MyProject.Web.Client/Api/OpsApi.cs`（`ScheduledJobApi`、`SystemHealthApi`、`DashboardApi`）、`MaintenanceApi.cs`（`BackupApi`、`DataRetentionApi`、`DatabaseUsageApi`）、`SystemParameterApi.cs`、`AnnouncementApi.cs`。排程、備份、資料保留共用的作業狀態轉換在 `MyProject.Web/Controllers/MaintenanceJobMapper.cs`。

### 2.12 排程作業（`ScheduledJobsController`，`api/v1/scheduled-jobs`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 總覽 | `GET scheduled-jobs` | `ApiResult<ScheduledJobOverviewDto>`：排程是否啟用、設定錯誤、每個作業的 cron、是否啟用、下次執行、最近一次結果、是否在佇列中 |
| 立即執行 | `POST scheduled-jobs/{name}/trigger` | 放進佇列由排程器在背景執行；已在佇列或執行中回 409（不寫稽核）；成功寫 `Job.Trigger`（`job=名稱`）|
| 啟用／停用 | `PUT scheduled-jobs/{name}/enabled` | body `{ "enabled": true/false }`；寫 `Job.Enable`／`Job.Disable`。停用後不再依排程執行，仍可立即執行 |
| 執行紀錄 | `GET scheduled-jobs/{name}/runs` | 最近的執行紀錄（新到舊，最多 50 筆）|

作業名稱不分大小寫，不認得回 404「沒有名為「X」的排程作業。」。

### 2.13 系統健康監控（`SystemHealthController`，`api/v1/system-health`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 健康報告 | `GET system-health` | `ApiResult<SystemHealthReportDto>`：全部檢查項目與寄信測試區的設定。⚠️ **每次呼叫都會執行全部檢查**；AI 已設定時「LLM API」一項會真的呼叫模型（產生費用、記進 Token 用量） |
| 警告日誌 | `GET system-health/warn-logs` | 最近 24 小時 WARN 以上（新到舊，沿用日誌檢視的上限）；讀取失敗仍回 200，原因放在 `EmptyText`（只影響這一區）|
| 寄信測試 | `POST system-health/test-email` | body `{ "to": "…" }`，同步寄送、當場知道成敗；稽核 `Email.Test` 由 `EmailTestService` 寫 |
| 通知測試 | `POST system-health/test-notification` | 發一則測試通知給全體管理員（不寄信）；稽核 `Notification.Test` 由 `NotificationTestService` 寫 |

### 2.14 系統備份（`BackupsController`，`api/v1/backups`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 清單 | `GET backups` | `ApiResult<BackupListDto>`：備份檔（新到舊，含 manifest 摘要）、備份作業的狀態、保留份數 |
| 立即備份 | `POST backups/run` | 放進排程佇列（與排程共用作業鎖）；執行中回 409「備份已經在執行中，請等它完成。」；寫 `Job.Trigger` |
| 刪除 | `DELETE backups/{檔名}` | 寫 `Backup.Delete`；找不到回 404 |
| 申請下載票證 | `POST backups/{檔名}/ticket` | `ApiResult<DownloadTicketDto>`：一次性下載網址（60 秒、只能用一次），由第三節的 `GET api/v1/downloads/{ticket}` 兌換；檔名不合格、不在備份目錄或檔案不存在回 404。稽核在**兌換時**寫 |

檔名一律經 `BackupStore.TryResolveFullPath`（檔名規則＋備份根目錄），擋路徑穿越。

### 2.15 資料保留與清理（`DataRetentionController`，`api/v1/data-retention`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 總覽 | `GET data-retention` | `ApiResult<DataRetentionOverviewDto>`：每種資料的數量、檔案空間、最舊一筆、保留規則、下次會清掉幾筆、負責的排程，以及伺服器的「今天」 |
| 立即清理 | `POST data-retention/{種類}/purge-now` | 伺服器重新讀一次總覽，確認那一列現在能清理（不能時 409 並說明原因、沒有排程作業 400），放進排程佇列；寫 `Job.Trigger`（`job=…; source=data-retention; expected=N`，**N 由伺服器在按下時重算**，可能與確認窗的數字差幾筆）。已刪除資料的五列共用一個作業 |
| 自訂清除預覽 | `POST data-retention/{種類}/preview` | body `CustomPurgeRequestDto`；回會刪掉幾筆。條件由伺服器以自己的「今天」驗證，不合法回 400 |
| 自訂清除 | `POST data-retention/{種類}/custom-purge` | 刪除早於條件的資料（連同檔案），只影響這一次、不改系統參數；刪到資料才寫稽核。可能跑很久，WASM 端用 `ApiClient.PostLongRunningAsync`（最長 30 分鐘）|

### 2.16 資料庫用量（`DatabaseUsageController`，`api/v1/database-usage`）

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 用量 | `GET database-usage` | `ApiResult<DatabaseUsageDto>`：資料庫與各資料表的大小。**只送資料庫檔名，不送完整路徑**（與系統管理助理的工具相同）；不寫稽核 |

### 2.17 系統參數（`SystemParametersController`，`api/v1/system-parameters`）

走畫面同一個 `SystemParameterManager`：**驗證全在伺服器**（參數目錄的型別、範圍、長度、空白＋所有設定驗證器），通過才寫資料庫，寫完立即重新套用（不需重啟）。設定鍵含冒號，所以放在**本文**而不是網址。

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 全部參數 | `GET system-parameters` | `ApiResult<SystemParameterOverviewDto>`：全部參數（目錄順序，含定義、目前值、設定檔的值、來源、覆寫的版本號與修改者）、資料庫裡不認得的鍵、設定檔本身有誤的區段 |
| 修改 | `POST system-parameters/save` | body `Key`、`Value`、`ExpectedStamp`。不在目錄的鍵（例如 `JwtSettings:SigningKey`）404；輸入不合法或值沒變 400；開窗後別人改過或設定檔本身有誤 409。寫 `SystemParameter.Update`（值與設定檔相同時改記 `SystemParameter.Reset`），detail `key=…; old=…(來源); new=…(來源)` |
| 還原 | `POST system-parameters/reset` | body `Key`、`ExpectedStamp`；還原成設定檔的值，或移除資料庫裡不認得的參數；覆寫已不存在 404、版本不同 409；寫 `SystemParameter.Reset` |

縮短保留天數的確認窗在畫面上。

### 2.18 公告管理（`AnnouncementsController`，`api/v1/announcements`）

0.10.1 起已有給外框橫幅用的 `active`、`dismiss`（見第五之一節，0.10.6 起改為各自標 `[SignedInUser]`），0.10.6 起補上公告管理頁的端點，走畫面同一個 `AnnouncementService`：

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 清單 | `GET announcements` | `ApiResult<AnnouncementListDto>`：全部公告（開始時間新到舊，含對象名稱）、對象選項（未刪除的角色與團隊）、伺服器的「現在」 |
| 新增 | `POST announcements` | 回新的 Id；建立者取目前登入的帳號；輸入不符規則 400；寫 `Announcement.Create` |
| 修改 | `PUT announcements/{id}` | 已不存在 404、開窗後別人改過 409；寫 `Announcement.Update` |
| 刪除 | `DELETE announcements/{id}?concurrencyStamp=` | 帶開窗時的版本號；已不存在 404、別人改過 409；寫 `Announcement.Delete`（標題取自資料庫）|

存檔或刪除後伺服器讓公告快取失效並發出公告變動訊號（0.10.8 以前另讓開著的 Server 版畫面即時更新）；WASM 公告頁另以 `ClientAnnouncementSignal` 請同一個分頁的橫幅立即重讀，其他分頁照舊 60 秒輪詢與切回分頁時重讀（0.10.7 起另經通知推播串流 `POST notifications/stream` 通常幾秒內更新，見第五之一節）。

### 2.19 首頁儀表板（`DashboardController`，`api/v1/dashboard`）

只要登入就能呼叫（0.10.17 起 `[SignedInUser]`：首頁不受權限限制，`PermissionRules.OpenToEveryone`；0.10.16 以前要首頁 view 權限）；「排程作業」小工具的端點例外，是 `[AdminOnly]`。不寫稽核。

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 首頁 | `GET dashboard` | `ApiResult<DashboardDto>`：系統名稱、說明、版本、環境、依權限過濾的快速入口，以及**這個人看得到的小工具**（代號、標題、圖示；由伺服器依 `DashboardWidgetCatalog.VisibleTo` 決定）|
| 我的帳號 | `GET dashboard/account` | 上一次登入（不含這一次）、密碼到期（7 天內標「快到期」）、兩步驟驗證 |
| 我的通知 | `GET dashboard/notifications` | 未讀數與最新 5 則；連結只保留安全的站內網址（`ReturnUrlGuard`），否則為 null |
| 排程作業 | `GET dashboard/scheduled-jobs` | `[AdminOnly]`：上次失敗或中斷、逾期未執行、已停用的作業 |

## 二之四、Agent、系統管理助理與工作流程 API（0.10.7 起，WASM 遷移 M8）

Agent 測試台、系統管理助理（含巡檢報告窗）、Agent 工作流程三頁改在 WebAssembly 執行，背後是下面這組 API。只有 `api/v1` 路由、要 Bearer、回 `ApiResult`；本地時間以不帶時差送出。

- **對話與執行一律回 NDJSON 串流**（見《[Web API 設計慣例](Web%20API%20設計慣例.md)》「串流回應」）：閒置超過 **15 秒**插一筆 `ping`（`NdjsonHeartbeat`），工具或推論模型很久沒輸出時中間的代理伺服器才不會把連線切斷；畫面收到 `ping` 直接忽略。請求在進入串流之前就被拒絕時（400、403、404、409）回一般 `ApiResult`。
- **稽核都在原本的服務裡**：工具的 `Ai.ToolInvoked`／`Ai.ToolDenied`／`Ai.ToolRejected` 由工具外殼寫，刪除對話或紀錄的 `AiAgentConversation.Delete` 由對話服務寫，測試專案由專案服務寫；開啟、改名、看巡檢報告不寫。
- DTO 在 `MyProject.Dtos/Ai`（`AgentDtos`、`AgentStreamDtos`、`AgentWorkflowDtos`、`WorkflowStreamDtos`）與 `MyProject.Dtos/Admins/AdminPatrolReportDtos.cs`；WASM 端的呼叫在 `MyProject.Web.Client/Api/AgentConversationApi.cs`（`AgentPlaygroundApi`、`AdminAssistantApi`）、`AgentWorkflowApi.cs`，對應在 `AgentDtoMapper`、`AgentWorkflowDtoMapper`（伺服器端 `Controllers/AgentWorkflowMapper.cs`）。

### 2.20 Agent 測試台與系統管理助理（`AgentPlaygroundController`，`api/v1/agent-playground`；`AdminAssistantController`，`api/v1/admin-assistant`）

兩個控制器只標授權、選設定，流程共用 `Controllers/AgentConversationEndpoint.cs`。權限：測試台每個 action `[HasPermission(角色_Agent測試台, View)]`，產生／清除測試專案另要專案項目的 `Create`／`Delete`；系統管理助理與巡檢報告每個 action `[AdminOnly]`。

| 動作 | 路由（兩邊相同的以 `{頁}` 表示）| 回傳／說明 |
|------|------|------|
| 頁面資訊 | `GET {頁}/info` | `AgentPageInfoDto`：AI 不可用的原因、對話保存天數；測試台另有推論等級選項與「系統預設」文字、能不能產生／清除測試專案、測試專案的標題前綴（「【測試】」）|
| 我的對話 | `GET {頁}/conversations` | 只列自己的、這個 Agent 的對話（最後對話時間新到舊），含「待核准」旗標 |
| 打開對話 | `GET {頁}/conversations/{id}` | `AgentConversationDto`：完整的畫面紀錄與目前待核准（接續一次，不呼叫模型）。正在執行時最多等 3 秒讓它收尾再讀；不是自己的、內容不見或格式不相容 404「這段對話無法接續…」|
| 改標題 | `PUT {頁}/conversations/{id}/title` | 不寫稽核 |
| 刪除對話 | `DELETE {頁}/conversations/{id}` | 執行中就等它做完再刪（否則執行結束的存檔會把它建回來）；寫 `AiAgentConversation.Delete` |
| 送出訊息 | `POST {頁}/messages` | body `AgentMessageRequestDto`（`ConversationId` 空＝開新對話，`Message`，測試台另有 `ReasoningEffort`）。回 NDJSON（見下）。空白或超過 **2,000 字** 400；推論等級不合法 400；等待核准時送新訊息 409；AI 不可用 409 |
| 回覆核准 | `POST {頁}/conversations/{id}/approvals` | body `ApprovedRequestIds`、`RejectedRequestIds`、`ConfirmText`。回 NDJSON。沒有待核准 409「沒有待核准的操作…」；編號不在目前待核准裡 409「這些操作已經處理過了…」（**所以同一個核准送兩次只執行一次**）；核准不可復原的動作沒輸入「確認」400 |
| 產生測試專案 | `POST agent-playground/samples` | 另要專案項目 `Create`；已有測試專案 409（提醒），建到一半失敗 500 |
| 清除測試專案 | `DELETE agent-playground/samples` | 另要專案項目 `Delete`；永久刪除標題以「【測試】」開頭的專案 |
| 巡檢報告清單 | `GET admin-assistant/patrol-reports` | `AdminPatrolReportListDto`：保存天數與報告清單（最新在前）|
| 一份巡檢報告 | `GET admin-assistant/patrol-reports/{id}` | `AdminPatrolReportDetailDto`（內文 Markdown 由畫面消毒）；已過期 404「找不到這份巡檢報告…」|

- **每個請求接續一次對話**：伺服器不把對話物件留在記憶體，送訊息與回覆核准都是「讀存檔 → `IAiAgentRunner.ResumeAsync` 接續 → 執行 → 存檔」；每個請求用當下系統參數的模型。中斷連線就取消這次執行，已收到的內容照樣存檔。
- **串流每一行**（`AgentStreamFrameDto`，`Kind`）：`start`（對話 ID、模型、推論等級）、`entry`（新增的一則紀錄）、`delta`（文字）、`tool`（工具開始／結束）、`label`（「由誰回答」，**每個請求至少送一次**）、最後是 `approvals`（停下來等核准）或 `result`（`AiResultDto`），閒置時 `ping`。
- **每段對話一把鎖**（`AiAgentConversationLocks`）：鎖得到就先驗證，錯誤以狀態碼回；同一段對話正被另一個請求（例如另一個分頁）使用時**排隊**——先回 200 串流、等待期間送心跳，輪到之後才驗證，這時的錯誤放在 `result` 那一行（`Success=false`）。核准另以資料庫的條件式更新（`HasPendingApproval`）搶占，跨行程也只執行一次。
- 系統管理助理接續停在核准的對話時，沿用提出核准的那一輪（分派結果存在對話檔的 `TurnKey`，由 `IAiAgentTurnPlanner.RestoreTurnAsync` 重建），不會變成「全部工具＋全部領域」。

### 2.21 Agent 工作流程（`AgentWorkflowsController`，`api/v1/agent-workflows`）

類別上 `[HasPermission(角色_Agent工作流程)]`。一次執行要跨好幾個請求，執行物件放在伺服器的 `AiWorkflowRunRegistry`（singleton）。

| 動作 | 路由 | 回傳／說明 |
|------|------|------|
| 頁面資訊 | `GET agent-workflows/info` | 可選的工作流程、AI 不可用的原因、紀錄保留天數 |
| 我的執行紀錄 | `GET agent-workflows/records?workflow=` | 某一條工作流程自己的紀錄（新到舊）；不認得的工作流程 400 |
| 一筆紀錄 | `GET agent-workflows/records/{id}` | 唯讀回顧；內容不見 404 |
| 改名 | `PUT agent-workflows/records/{id}/title` | 不寫稽核 |
| 刪除紀錄 | `DELETE agent-workflows/records/{id}` | 這次執行還沒結束就先放棄（存成「中斷」）再刪；寫 `AiAgentConversation.Delete` |
| 開始執行 | `POST agent-workflows/runs` | body `Workflow`、`Input`。回 NDJSON 到停下來等人（`request`）或結束（`result`）。**一人同時只能有一次執行**（可能在別的分頁），第二個 409；輸入空白、AI 不可用 400 |
| 等我回覆的執行 | `GET agent-workflows/runs/active` | 重新整理後接回：停下來等回覆的那一次（含紀錄與待回覆）；沒有或正在處理中時資料是 null |
| 回覆 | `POST agent-workflows/runs/{id}/responses` | 回 NDJSON 繼續執行。別人的執行或已結束 404；正在處理中、已回覆過或不存在的請求 409（畫面收到 409 重讀狀態）；回答空白、退回沒寫意見、退回次數用完 400 |
| 停止 | `POST agent-workflows/runs/{id}/stop` | 取消正在處理的那一輪（存成「已停止」，串流最後一筆是 Canceled 的結果）|
| 放棄 | `POST agent-workflows/runs/{id}/abandon?onlyIfIdle=` | 存成「中斷」、之後只能回顧。`onlyIfIdle=true`（離開頁面時用）：另一個請求正在使用這次執行就不放棄 |

- **串流每一行**（`AgentWorkflowFrameDto`）：`start`、`agentStarted`、`delta`、`tool`、`agentCompleted`、`output`、`request`（等人：工具核准、提問、審閱）或 `result`（`Completed`／`Ended`／`Failed`／`Canceled`／`Interrupted`），閒置時 `ping`；每一行附上這之前新增的紀錄。
- **執行登錄表** `AiWorkflowRunRegistry`：每次執行有自己的 `AsyncServiceScope`，填入目前使用者與來源 IP（每次回覆時以那個請求載入的最新資料更新，權限被改了下一步就照新的算）；只有擁有者能操作（別人一律 404）；同一次執行一次只有一個請求在讀。停下來等人超過 **30 分鐘**沒有動作就結束成「中斷」並釋放 scope；網站關閉時先中斷、等收尾最多 5 秒再釋放。
- **串流途中斷線＝整個流程中斷**（離開頁面就中斷，不在背景繼續花 AI 費用）；停下來等人時沒有開著的串流，重新整理或關分頁不影響它，回到頁面以 `runs/active` 接回。
- ⚠️ 登錄表只在單一行程有效：IIS 回收或重啟後執行就沒了（紀錄停在最後一次存檔），多台主機也不共用。

## 三、一次性下載票證 `DownloadsController`（0.10.3 起）

`src/MyProject/MyProject.Web/Controllers/DownloadsController.cs`，路由 `api/v1/downloads`（**無** `api/` 平行路由）；票證由 `MyProject.Web/Downloads/DownloadTicketService.cs` 發給與兌換。票證有兩種（`Kind`）：專案附件 `ProjectFile`（0.10.3 起，目標是附件 Id）與系統備份 `Backup`（0.10.6 起，目標是備份檔名）；票證的目標 0.10.6 起改成字串（`DownloadTicket.Target`），由各種類自己解讀。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 以票證下載 | `GET api/v1/downloads/{ticket}` | `[AllowAnonymous]`（票證本身就是憑證）| 成功回**原生檔案串流**（`Content-Disposition: attachment`，不包 `ApiResult`）；任何失敗都回同一個 `ApiResult` 404「下載連結無效或已過期，請回到頁面重新下載。」|

為什麼這樣做：瀏覽器原生下載（`<a download>`）帶不了 `Authorization` 標頭，而大檔不能先讀進 WASM 記憶體。所以先用 Bearer 申請票證（附件 `POST Project/files/{fileId}/ticket`、備份 `POST backups/{檔名}/ticket`），再讓瀏覽器直接下載票證網址。0.10.10 起雙站台（前端與 API 不同來源）時瀏覽器會忽略 `download` 屬性，改由 `browser-files.js` 以 `fetch` 取回、在瀏覽器記憶體組成 blob 網址存檔（不經 WASM 記憶體；檔案先整個下載完才存檔），失敗時畫面顯示伺服器的訊息、留在原頁（CORS 已公開 `Content-Disposition`，檔名照樣取得）。

- **票證**：32 個隨機位元組（URL-safe Base64），60 秒內有效，兌換時就移除（只能用一次，重放回 404）；發新票證時順手清掉過期沒用的。存在單一行程的記憶體：IIS 應用程式集區要維持一個背景工作處理序（預設值）；集區回收只會讓還沒用的票證失效，使用者重按一次即可。
- **兌換時重新檢查**：以發票證時記下的使用者 Id 與安全戳記，經 `ICurrentUserLoader` 重新載入使用者——帳號停用、改密碼、被強制登出、角色變動都回 404，被要求先改密碼或先設定兩步驟驗證時也回 404；附件再確認仍有 `角色_專案項目` 的 View 權限，最後 `ProjectService.GetFileDownloadAsync` 再查一次團隊範圍、專案是否已刪除、實體檔是否存在；備份再從資料庫確認**仍是管理員**，檔名經 `BackupStore.TryResolveFullPath`（只認備份根目錄下、檔名合格的 zip）且檔案存在。
- **失敗一律同一個 404**，不透露原因（原因只寫日誌）。成功下載寫稽核：附件 `Project.FileDownload`（目標類型 `ProjectFile`、detail `file=原始檔名`），備份 `Backup.Download`（目標類型 `Backup`、detail `file=檔名`，回 `application/zip`）。
- **不支援續傳（Range）**：票證只能用一次，下載中斷要回頁面重新點（使用者接受這個取捨；備份下載 0.10.6 起也一樣，Server 版的 Cookie 下載原本可以續傳）。
- **已知限制**：下載管理員或瀏覽器擴充功能若先預先抓取一次網址，會把票證用掉，真正的下載就得到 404；使用者再點一次檔名即可。

> **0.10.3 移除**：舊的 `ProjectFileController`（`GET api/project-files/{id}/download`、`api/v1/project-files/{id}/download`，以 Cookie 驗證、支援續傳）已刪除，舊網址回 404。

> **0.10.6 移除**：舊的系統備份下載 `BackupController`（`GET api/backups/{fileName}/download`，0.9.99～0.10.5，只收 Cookie 驗證、每次查資料庫確認管理員、支援續傳、續傳的後續區段不重複寫稽核）已刪除，舊網址回 404；改用第 2.14 節的票證。

## 四、認證控制器 `AuthController`

`src/MyProject/MyProject.Web/Controllers/AuthController.cs`，路由 `api/Auth`、`api/v1/Auth`；帳密換 token（App、整合程式用；網頁的登入端點見第四之一節）。0.10.0 起刷新 token 存在伺服器端、每次使用就輪替（規則見《[認證授權與權限機制](../security/認證授權與權限機制.md)》「刷新 Token」）。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 登入 | `POST Auth/login` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（失敗回 `Unauthorized`；已啟用兩步驟驗證要在 body 帶 `TwoFactorCode`，沒帶回 401「需要兩步驟驗證碼。」且不計入失敗次數；必須使用兩步驟驗證卻還沒設定的帳號一律 401，0.9.104 起）。0.10.0 起 `refreshToken` 是不透明字串、`isPersistent=true` |
| 換發 Token | `POST Auth/refresh` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`，回傳新的一組 token，舊的刷新 token 隨即失效（0.10.0 起輪替）。不認得、已過期或已撤銷、帳號已停用或刪除、工作階段已失效都回 `Unauthorized`「Refresh Token 無效或已過期。」並寫稽核 `Token.RefreshFailed`；已被換掉的舊 token 超過 30 秒又被使用時整串撤銷並寫 `Token.ReuseDetected`（0.10.8 起網頁管道撤銷**同一次網頁登入的每一串**）；網頁與 App 共用這個端點，效期依發出管道（網頁 `SessionSettings`、App `JwtSettings`）。0.10.8 起因工作階段版本換掉而失敗時另寫 `Login.SessionExpired` |
| 登出 | `POST Auth/logout` | `[AllowAnonymous]`＋`[AllowDuringForcedSetup]` | 0.10.0 起。body 帶 `RefreshToken`；網頁管道（0.10.8 起）撤銷**這次登入的每一串**（每個分頁各一串，以登入識別串起來），其他管道撤銷所屬那一串。存取 Token 帶串的識別，已發出的也立即失效。一律回 `ApiResult` 成功（避免試探），認得時寫稽核 `Logout`（App 另記 `via=api`） |
| 目前使用者 | `GET Auth/me` | `[Authorize(JwtBearer)]`＋`[AllowDuringForcedSetup]` | 0.10.0 起 `ApiResult<SessionInfoDto>`（從資料庫讀：身分、有效權限、有效團隊、`mustChangePassword`、`requiresTwoFactorSetup`、`twoFactorEnabled`；以前是 `CurrentUserDto`、只讀 JWT Claims）。網頁管道的 Token 遇到沒有主要角色的帳號回 403 `NO_PRIMARY_ROLE`（0.10.8 起），前端登出 |

> **0.10.8 移除**：`POST Auth/bridge`（0.10.0～0.10.7，以網頁 Cookie 換 Token 的過渡端點）與 `/Auths/RefreshSession`（改到自己後換發 Cookie 的頁面）。網頁改用第四之一節的端點直接取得 Token。

## 四之一、網頁登入與帳號自助 API（0.10.8 起，WASM 遷移 M9）

只有 `api/v1` 路由、回 `ApiResult`。網頁發出的 Token 屬於發出管道 `web`：勾「記住我」整串上限 `SessionSettings:RememberMeDays`（30 天，從登入算），沒勾刷新 Token 閒置 `IdleTimeoutMinutes`（480 分鐘）；存取 Token `AccessTokenMinutes`（15 分鐘）。DTO 在 `MyProject.Dtos/Auths/WebAuthDtos.cs`。
**登入配額**：路徑在 `LoginQuotaPaths` 的端點算進每 IP 每分鐘 `RateLimit:LoginRequestsPerMinute`（預設 10）——`two-factor`（含 `two-factor/setup`）、`google`、`password`、`session`；**`login/web` 與 `login-settings` 不算**（網頁登入靠帳號鎖定，受一般 API 配額）。

| 控制器 | 動作 | 路由 | 授權 | 回傳／說明 |
|---|---|---|---|---|
| `WebAuthController` | 登入頁設定 | `GET Auth/login-settings` | `[AllowAnonymous]` | `LoginPageSettingsDto`：系統名稱與說明、Google／寄信是否啟用、鎖定次數與分鐘、記住裝置與記住我天數、密碼規則、重設連結有效分鐘 |
| | 網頁登入 | `POST Auth/login/web` | `[AllowAnonymous]` | body `WebLoginRequestDto`（帳號、密碼、記住我、記住裝置 Token、返回網址）；回 `WebLoginResultDto`：完成時 `Tokens`；需要第二步時 `RequiresTwoFactor`＋`TwoFactorTicket`（待驗票證，5 分鐘）；記住過的裝置直接完成。必須設定兩步驟驗證的帳號照樣拿到 Token，由閘門帶到設定頁 |
| | 登入第二步 | `POST Auth/two-factor` | `[AllowAnonymous]` | body `TwoFactorLoginRequestDto`（票證、驗證碼或備用碼、記住這台裝置）；回 `WebLoginResultDto`（勾記住時另回 `RememberDeviceToken`）。驗證碼錯了計入登入失敗、票證仍可用；等待期間改了密碼、被停用或強制登出回 `TWO_FACTOR_TICKET_INVALID` |
| | Google 代碼兌換 | `POST Auth/google/exchange` | `[AllowAnonymous]` | body `GoogleExchangeRequestDto`（代碼、瀏覽器驗證值原值、記住裝置 Token）；代碼 60 秒、只能用一次（出示錯的驗證值也算用掉）。已啟用兩步驟驗證時回待驗票證（記住過的裝置除外）|
| | 新分頁取得 Token | `POST Auth/session/fork` | `[AllowAnonymous]`（憑刷新 Token）| body `RefreshTokenRequestDto`；以目前有效的網頁刷新 Token 開一串新的（沿用登入識別、記住我、整串上限，原本那一串不輪替）；開完再確認來源沒被撤銷，否則作廢 |
| `AuthPasswordController` | 申請重設 | `POST Auth/password/forgot` | `[AllowAnonymous]` | 不論帳號存不存在都回同一個結果；輸入 support 時明說不提供；寄信未啟用回 400 |
| | 檢查重設連結 | `POST Auth/password/reset/check` | `[AllowAnonymous]` | `ResetPasswordCheckDto`（只讀、不消耗連結）|
| | 以連結設定新密碼 | `POST Auth/password/reset` | `[AllowAnonymous]` | `ResetPasswordResultDto`：密碼規則不過時不消耗連結；`LinkInvalid=true` 要重新申請。重設 Token 一律放本文，回應帶 `no-store`、`no-referrer` |
| | 變更密碼頁資料 | `GET Auth/password/change` | `[SignedInUser]`＋`[AllowDuringForcedSetup]` | `ChangePasswordStatusDto`：帳號、是否 support、被要求變更的原因、密碼規則 |
| | 變更自己的密碼 | `POST Auth/password/change` | `[SignedInUser]`＋`[AllowDuringForcedSetup]` | `SessionRenewalDto`：其他裝置登出，這台回新的 `Tokens`（舊的一串撤銷）|
| `TwoFactorSetupController` | 設定狀態 | `GET Auth/two-factor/setup` | `[SignedInUser]`＋`[AllowDuringForcedSetup]`（類別）| `TwoFactorSetupStatusDto` |
| | 開始設定 | `POST Auth/two-factor/setup/start` | 同上 | `TwoFactorSetupStartDto`：QR Code（PNG data URI）、金鑰、設定票證（15 分鐘）；**金鑰確認前不進資料庫**。已啟用或沒有本機密碼回 400 |
| | 啟用 | `POST Auth/two-factor/setup/enable` | 同上 | `TwoFactorBackupCodesDto`：10 組備用碼（只出現這一次）與這台的新 `Tokens` |
| | 重新產生備用碼 | `POST Auth/two-factor/setup/backup-codes` | 同上 | 驗證碼錯了不計入鎖定（靠登入配額擋）|
| | 停用 | `POST Auth/two-factor/setup/disable` | 同上 | `SessionRenewalDto`：這台的新 `Tokens` |

頁面端的對應：登入頁 `/Auths/Login`（含 `?reset=1`、`?sso=…`、`#code=` 兌換）、`/Auths/TwoFactor`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`（這一頁的整頁回應加 `no-referrer`、`no-store`：同站台由主機加，0.10.10 起獨立靜態站由 `MyProject.Web.Client/web.config` 加）、`/ChangePassword`、`/TwoFactorSetup`。

## 五、Google 第三方登入 `ExternalAuthController`

`src/MyProject/MyProject.Web/Controllers/ExternalAuthController.cs`，路由前綴 `Auths/Google`。**此為網頁導向端點，非 API**（回傳 `Challenge`／`Redirect`，不走 `ApiResult`）。0.10.8 起回呼不發登入 Cookie，改產生一次性代碼交給前端兌換（第四之一節 `google/exchange`）；Google 往返過程仍用 5 分鐘的暫存外部 Cookie。0.10.10 起下表導回的前端頁面一律經 `ClientSettings.FrontendUrl`：同站台是本站路徑，雙站台接在 `ClientSettings:BaseUrl` 後面（只用設定值，不採用請求的 Host 或參數）；這兩個端點與 Google 的回呼 `/signin-google` 本身都在 API 主機，前端的 Google 按鈕經 `ApiAddress.GoogleLogin` 整頁導過來。詳見《[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)》。

| 動作 | 路由 | 說明 |
|------|------|------|
| 觸發 Google 驗證 | `GET Auths/Google/Login?binding=` | 未設定金鑰時導回 `/Auths/Login`。`binding` 是前端產生的瀏覽器驗證值的雜湊（SHA-256 → Base64Url，原值存在那個分頁的 sessionStorage），放進 OAuth 的 state 帶到回呼 |
| 驗證回呼 | `GET Auths/Google/Callback` | 查找/建立帳號；依 `ExternalLoginResult.Evaluate` 的順序：已刪除導回 `/Auths/Login?sso=deleted`、停用（待開通）導向 `/Auths/Pending`、鎖定導回 `/Auths/Login?sso=locked`；其餘產生一次性代碼（只存雜湊、60 秒、一次、綁定 `binding`）導回 `/Auths/Login#code=…`（代碼在網址片段，不送到伺服器、不進日誌），兩步驟驗證在兌換時處理。**沒帶綁定值的舊流程一律回登入頁，不建立或連結帳號**。代碼只在這個行程的記憶體，回呼與兌換要落在同一台主機 |

## 五之一、WASM 外框、個人資料與異動歷程（0.10.1 起，WASM 遷移 M2；異動歷程 0.10.2 起）

只有 `api/v1` 路由；一律 `[Authorize(JwtBearer)]`（`shell/culture` 例外），`ShellController`、`NotificationsController`、`ProfileController` 0.10.4 起在類別上明確標 `[SignedInUser]`（登入即可；`AnnouncementsController` 0.10.6 起多了只限管理員的管理端點，改成 `active`、`dismiss` 各自標 `[SignedInUser]`），只能讀改**自己**的資料（使用者一律取自 `CurrentUserService`，由 `ApiActorMiddleware` 依存取 Token 載入），回傳 `ApiResult<T>`。
時間欄位（`*Local`）已換成伺服器的本地時間、以不帶時差送出，前端照字面顯示（0.10.6 修正三處：`NotificationsController`、`ProfileController` 與 `ShellController` 的 `StartedAtLocal`〔「關於」的啟動時間〕原本帶 `+08:00` 送出，伺服器與瀏覽器時區不同時鈴鐺、個人資料頁與「關於」的時間會錯）。DTO 在 `MyProject.Dtos/Shell`。

| 控制器 | 動作 | 路由 | 回傳／說明 |
|---|---|---|---|
| `ShellController` | 系統資訊 | `GET shell/info` | `ShellInfoDto`：系統名稱、描述、版本、環境、啟動時間、各頁說明主題（路由、標題）；0.10.7 起多 `NotificationPushEnabled`（`NotificationSettings:PushEnabled`，外框依它決定要不要連推播）|
| | 選單 | `GET shell/menu` | `ShellMenuDto`：依目前使用者權限過濾後的選單（`SidebarMenuService` 以 `PermissionRules.CanAccessPage` 過濾），以及全部選單網址 |
| | 操作說明 | `GET shell/help?route=` | `HelpDocumentDto`：Markdown 已在伺服器端以 `HelpMarkdownRenderer`（停用 HTML）轉成 HTML，相關頁面已依權限過濾；沒有說明回 404 |
| | 開頁稽核 | `POST shell/page-view` | 寫 `Page.View`（路徑去掉 query string；只有 `AuditLogSettings:RecordBrowsing` 開啟才會留下）。只接受 WASM 的頁面路徑（`ClientRouteTable`；0.10.8 起即全部頁面），每位使用者每分鐘最多 60 筆，超過的不記。0.10.2 起使用者沒有該頁權限時（依選單的權限鍵，`SidebarMenuService.FindPagePermissionAsync`）改寫「沒有權限開頁」稽核 `Permission.Denied`（detail `via=WASM`），WASM 頁面不自己寫 |
| | 日期時間格式 | `GET shell/culture` | 0.10.2 起，**不需登入**（`[AllowAnonymous]`）。`DateTimeFormatDto`：伺服器（zh-TW）的日期時間格式；WASM 在啟動前讀一次（`ClientCulture`），讓清單的時間顯示與伺服器的格式一致。內容只有格式字串 |
| | 瀏覽器錯誤回報 | `POST shell/client-errors` | 交給 `BrowserErrorReporter`（同一套截斷、開關與系統例外紀錄）。每位使用者每分鐘上限沿用 `ClientErrorReporting:MaxPerCircuitPerMinute`（`PerUserRateLimiter` 跨請求計數；鍵名沿用 Blazor 電路時代，0.10.9 起意思就是每位使用者每分鐘；0.10.10 起 `BrowserErrorReporter` 拿掉 `[JSInvokable]` 與每個實例的計數，只剩這裡計數），超過回 `accepted=false` |
| `NotificationsController` | 摘要 | `GET notifications/summary` | 未讀數、最新 10 則、有沒有已讀的；連結只保留安全的站內網址（`ReturnUrlGuard`），否則為 null |
| | 標為已讀／全部已讀 | `POST notifications/{id}/read`、`POST notifications/read-all` | |
| | 刪除一則／清除已讀 | `DELETE notifications/{id}`、`DELETE notifications/read` | 不是自己的或找不到回 404 |
| | 通知推播串流 | `POST notifications/stream` | 0.10.7 起。NDJSON 長連線（`PushFrameDto`）：`ready` → 自己的通知或公告有變動時送 `notifications`／`announcements`（不帶內容，外框自己重查；短時間多次合併成一筆）→ 閒置每 25 秒 `ping`。伺服器主動結束時最後一筆是 `renew`（存取 Token 到期前 1 分鐘，前端刷新後重連）或 `reconnect`（滿 30 分鐘、網站關閉）。每位使用者最多 3 條（第 4 條起 429，那個分頁只靠輪詢）；`NotificationSettings:PushEnabled=false` 時 404。產生端（`Streaming/NotificationPushStream.cs`）自己看 `RequestAborted` 結束並取消訂閱；不記慢請求 |
| `AnnouncementsController` | 目前公告 | `GET announcements/active`（公告管理頁的端點見 2.18）| 挑選規則在 `AnnouncementCache.Select`（時間、對象、沒被關閉；0.10.9 起從已刪除的 Server 版公告橫幅 `AnnouncementBanner` 搬來）|
| | 關閉 | `POST announcements/{id}/dismiss` | |
| `ProfileController` | 個人資料 | `GET profile` | `ProfileDto`：帳號、姓名、Email、角色、團隊、密碼與兩步驟驗證狀態、最近 20 筆登入紀錄（動作已轉成中文）|
| | 改姓名 | `PUT profile/name` | body 帶 `ConcurrencyStamp`；別人同時改過或姓名不合規則回 400 |
| `AuditLogsController` | 某筆資料的異動歷程 | `GET audit-logs/history?targetType=&targetId=&page=&pageSize=&opened=` | 0.10.2 起，**只限管理員**（`[AdminOnly]`，與「稽核紀錄」頁相同，非管理員回 403；稽核紀錄頁的其他端點見 2.5）。`PagedResult<AuditHistoryItemDto>`，新的在前、不含搜尋與查看；`opened=true`（打開對話窗）時寫一筆 `Audit.Search`，換頁不帶。每頁最多 50 筆 |
| | 一筆稽核的完整內容 | `GET audit-logs/{id}/content` | 0.10.2 起，只限管理員。`AuditContentDto`（修改前後差異等長內容）；寫一筆 `Audit.View`。伺服器上的檔案路徑不回傳（清單只給 `HasContent`）|

## 六、Controller 端預設拒絕（0.4.34 起）

`Program.cs` 的 `app.MapControllers().RequireAuthorization()` 讓**未明確標註授權的 Controller 一律拒絕**。
需要匿名存取的端點必須自己標 `[AllowAnonymous]`（目前為 `AuthController` 的 login/refresh/logout、`WebAuthController` 全部（0.10.8 起）、`AuthPasswordController` 的 forgot、reset/check、reset（0.10.8 起）、`ShellController` 的 culture（0.10.2 起）、整個 `DownloadsController`（0.10.3 起，以一次性票證當憑證）與整個 `ExternalAuthController`）。
反過來，「登入就能呼叫」也要明說：0.10.4 起每個 `[ApiController]` 的 action（含繼承來的）都必須標 `[HasPermission]`、`[AdminOnly]`、`[SignedInUser]` 或 `[AllowAnonymous]` 其中之一，而且除了 `[AllowAnonymous]`，controller 或 action 上一定要有 `[Authorize]`（`ApiAuthorizationConventionTests`），避免新增端點時忘了加權限、變成任何登入的人都能呼叫。

0.10.0 起，以 access token 呼叫且使用者被要求先改密碼或先設定兩步驟驗證時，全域的 `ApiSessionGateFilter` 讓業務 API 一律回 403 `ApiResult`（`errorCode`：`PASSWORD_CHANGE_REQUIRED`／`TWO_FACTOR_SETUP_REQUIRED`），只有標 `[AllowDuringForcedSetup]` 的端點放行（0.10.8 起：`Auth/me`、`Auth/logout`、`Auth/password/change`、`Auth/two-factor/setup` 全部，以及強制頁外框要用的 `shell`、`notifications`、`announcements/active` 與 `dismiss`）。

> 刻意**只**套在 Controller 上，不使用 `AuthorizationOptions.FallbackPolicy`。頁面 0.10.8 起全部在 WASM、**不標 `[Authorize]`**：
> 整頁請求只回空的 WASM 外殼（0.10.10 起是前端的靜態檔 `index.html`），由 `ClientMainLayout` 在登入檢查通過前不渲染任何內容；匿名頁面各自標 `[AllowAnonymous]`
> 並由 `PageAuthorizationTests` 白名單（9 頁）守門（0.9.41～0.10.7 是 `Components/Pages/_Imports.razor` 以登入 Cookie 擋整頁請求）。
> 詳見 [開發慣例與限制速查 §5.1](開發慣例與限制速查.md)。

> 0.4.34 移除了模板遺留的 `WeatherForecastController`（無授權標註、路由不在 `/api` 之下、回傳裸物件）。

## 七、相關文件

- [Web API 設計慣例](Web%20API%20設計慣例.md)
- [API Versioning 策略](API%20Versioning%20策略.md)
- [認證授權與權限機制](../security/認證授權與權限機制.md)
- [紀錄分類與團隊權控 PRD](../prd/紀錄分類與團隊權控-prd.md)

> 返回 [architecture 索引](README.md)
