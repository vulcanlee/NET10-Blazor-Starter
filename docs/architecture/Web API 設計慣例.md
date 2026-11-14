# Web API 設計慣例

- 文件版本：1.16
- 文件狀態：已實作
- 現行系統版本：0.10.18
- 首次實作版本：0.1.61
- 最後核對日期：2026/11/14

## 目的
本文件記錄腳手架 Web API 的固定設計規範，未來新增 API 時應遵守同一套 contract，讓前端與外部用戶端能用一致格式處理成功、失敗、驗證錯誤、授權錯誤與例外。

## Controller 規範
- API Controller 放在 `src/MyProject/MyProject.Web/Controllers/`。
- 新的 Controller 只用 `api/v1` 路由（例如 `[Route("api/v1/users")]`）；只有既有的 `Auth`、`Category`、`Project`、`Team` 四個 Controller 同時提供 `[Route("api/[controller]")]` 與 `[Route("api/v1/[controller]")]`。新用戶端優先使用 `/api/v1/...`。
- Controller 必須使用 `[ApiController]` 與 `[ApiValidationFilter]`。
- 需要保護的 CRUD API 必須加上 `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`。
- 除驗證身分外，受保護動作方法**必須**以 `[HasPermission("resource", "action")]` 標註做**功能級／動作級授權**（見「功能級／動作級授權」節）。
- **每個 action 都要標明授權意圖**（0.10.4 起，`ApiAuthorizationConventionTests` 守門）：`[HasPermission]`、`[AdminOnly]`、`[SignedInUser]`、`[AllowAnonymous]` 四選一（標在類別上等於每個 action，繼承來的 action 也算），漏標測試就失敗；除了 `[AllowAnonymous]`，controller 或 action 上還要有 `[Authorize]`。只有 `[Authorize]` 的端點任何登入的人都能呼叫，新增端點時忘了加權限是最常見的越權漏洞。見「功能級／動作級授權」節的選法。
- API request/response 必須使用 DTO，不可以直接接收或回傳 Entity。
- Entity 與 DTO 轉換優先使用 AutoMapper profile 維護。
- **資料一律經畫面同一個 Service**（0.10.2 起，WASM 遷移 M3）：Controller 不另寫查詢或寫入邏輯，團隊範圍、名稱規則、並行衝突、軟刪除與稽核只有一份。分類、團隊 0.10.2 起、專案 0.10.3 起已改，專案中已沒有任何 Repository（`CategoryRepository`／`TeamRepository`／`ProjectRepository` 都已刪除）；新模組不要再寫 Repository（CRUD 產生器也不再產生）。DTO 與畫面模型的對應放在 `MyProject.Web.Client/Api/*DtoMapper.cs`，WASM 頁面與 Controller 共用。
- 寫入動作（新增、修改、刪除）成功後要寫稽核（0.9.78 起，LOG-14）。走服務的 Controller 由服務寫（與畫面相同，操作者是 `ApiActorMiddleware` 依存取 Token 載入的目前使用者），Controller 只補服務不寫的查看、搜尋、匯出稽核；不經服務的少數動作（例如 `AuthController` 的 Token 事件）呼叫 `this.WriteAuditAsync(AuditActions.Xxx..., ...)`
  （`Controllers/ControllerAuditExtensions.cs`，操作者由 `RequestActorResolver` 從 JWT 身分解析；0.10.8 起不再有登入 Cookie）。
  動作代碼一律引用 `MyProject.Business/Helpers/AuditActions.cs` 的常數，不可寫字串字面值（`AuditConventionTests` 守門）。
- **同一個網址依內容類型分流**（0.10.3 起）：要帶檔案的寫入動作不另開上傳端點，在同一個網址各寫一個 `application/json` 與一個 `multipart/form-data` 的 action，以 `[Consumes]` 區分（例：`POST/PUT api/v1/Project`，multipart 的 `data` 是與 JSON 版相同的 DTO 字串、`files` 是附件）。
  JSON 版的 `[Consumes]` 要列 `application/json`、`text/json`、`application/*+json`（維持原本接受的類型）；multipart 版設路由 `Order = 1`（沒帶 Content-Type 時由 JSON 版回 415，而不是兩個 action 模稜兩可回 500），並標 `[ApiExplorerSettings(IgnoreApi = true)]`（Swagger 不能在同一個方法＋路徑放兩個 action，multipart 格式改寫在《[Web API 端點目錄](Web%20API%20端點目錄.md)》）。
  multipart 的 action 另標 `[RequestSizeLimit]`／`[RequestFormLimits]` 放寬請求上限（專案為 4 GB，與 `web.config` 的 IIS `requestLimits` 一致，`WebConfigTests` 守門）；其他端點維持 ASP.NET Core 預設的約 30 MB。
- **檔案下載用一次性票證**（0.10.3 起）：瀏覽器原生下載帶不了 Bearer，大檔也不能讀進 WASM 記憶體，所以先以 Bearer 呼叫 `POST .../ticket` 取得 60 秒、只能用一次的網址，再由瀏覽器直接下載匿名的 `GET api/v1/downloads/{ticket}`（`DownloadsController`、`DownloadTicketService`）。
  兌換時依發票證時的使用者與安全戳記重新載入使用者、重查權限與資料範圍，任何失敗都回同一個 404；不支援續傳。細節見《[Web API 端點目錄](Web%20API%20端點目錄.md)》第三節。
  0.10.3 起專案附件、0.10.6 起系統備份（`POST api/v1/backups/{檔名}/ticket`，`[AdminOnly]`；兌換時再確認仍是管理員）都走這套，已沒有以 Cookie 驗證的 API（`ProjectFileController`、`BackupController` 都已刪除）。新增「下載某種檔案」時，在 `DownloadTicketService` 加一個票證種類（`Kind`，目標是字串），並在 `DownloadsController` 兌換時重新檢查權限與檔案位置。
- **背景作業交給排程佇列**（0.10.6 起）：「立即執行」「立即備份」「立即清理」這類要跑很久的動作，API 只把作業放進排程框架的佇列（`ScheduledJobOverviewService.TryTrigger`，與排程共用作業鎖）就回來，已在佇列或執行中回 409；畫面自己輪詢看進度。不要在請求裡同步執行。
- **設定鍵這類含冒號的識別放本文**（0.10.6 起）：例：`POST api/v1/system-parameters/save` 的 `Key`（`BackupSettings:KeepCount` 這種含冒號的鍵放進網址要另外編碼，也容易被誤解成路徑）。

- **AI 逐字顯示用 NDJSON 串流**（0.10.5 起，WASM 遷移 M6；不用 SignalR）：action 回 `new NdjsonResult<AiStreamFrameDto>(…)`（`MyProject.Web/Streaming/NdjsonResult.cs`），規則見下方「串流回應」節。0.10.7 起 Agent 對話、工作流程與通知推播也用同一套（不用 SignalR、不用 SSE）。
- **跨請求的狀態放伺服器、每個請求只做一段**（0.10.7 起，WASM 遷移 M8）：Agent 對話每個請求「讀存檔 → 接續 → 執行 → 存檔」，伺服器不把對話物件留在記憶體；工作流程一次執行要跨好幾個請求，執行物件放在 singleton 登錄表（`AiWorkflowRunRegistry`），每次執行有自己的 `AsyncServiceScope`。⚠️ 自己建的 scope 不經 `ApiActorMiddleware`，**一定要填入目前使用者**（`CurrentUserService`、來源 IP），每次回覆再以那個請求載入的最新資料更新，否則記帳、權限檢查與稽核拿不到人。
- **同一份資料的併發寫入要排隊、核准要搶占**（0.10.7 起）：Agent 對話每段一把鎖（`AiAgentConversationLocks`），後到的請求排隊而不是拒絕（兩個分頁同時操作同一段對話才不會互相覆蓋）；「待核准」以資料庫的條件式更新（`TryClaimPendingApprovalAsync`）搶占，**同一個核准送兩次只執行一次**（後到的 409），跨行程也成立。鎖只在同一個行程內有效，不要拿它當跨行程的保證。

## 串流回應（NDJSON，0.10.5 起）

AI 分析（日誌分析、例外分析與追問）要邊產生邊顯示，回應改用 NDJSON：

- **格式**：`Content-Type: application/x-ndjson; charset=utf-8`，每行一個 JSON（`AiStreamFrameDto`）。`Kind` 依序是 `start`（帶 `AiStreamScopeDto`）、零到多個 `delta`（`Text`）、最後**一定**是一筆 `result`（`AiResultDto`：成功與否、原因、模型、用量 `AiUsageDto`）。伺服器端的轉換在 `MyProject.Web/Ai/AiStreamFrames.cs`（`Start`／`Delta`／`Result`／`ToDto`）。
- **不緩衝**：`NdjsonResult` 關閉回應緩衝、每行寫完就 flush，並送 `Cache-Control: no-cache, no-transform`、`X-Accel-Buffering: no`；標頭在第一個字之前就送出。IIS 與反向代理的注意事項見《[正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)》。
- **進入串流之前的拒絕仍回一般 `ApiResult`**：權限 403、找不到 404、驗證 400 都在回 `NdjsonResult` 之前判斷；前端看 Content-Type 決定怎麼讀。
- **使用者中斷**：關窗或「停止產生」會中斷請求，`RequestAborted` 把取消傳給上游 AI 呼叫。⚠️ `NdjsonResult` **不**拿 `RequestAborted` 中斷列舉：寫不出去之後照樣把序列讀完、只是不再寫出，序列最後的收尾（稽核、用量記帳）才一定會執行。
- **一定要有結果**：把服務的串流包進 `AiStreamGuard.RunAsync`（`MyProject.Web/Ai/AiStreamGuard.cs`）。服務在取消時丟取消例外 → 轉成「已取消」結果（稽核照寫）；其他例外 → 記錯誤日誌、回「未預期的錯誤」並設 `Unexpected`（這種情況不寫稽核，與 Server 版外層防護相同）。不包的話，標頭已送出、串流直接斷掉，畫面停在「分析中」。
- **前端**：WASM 用 `ApiClient.StreamAsync<T>(url, body, onItem, ct)`（帶 Token、401 刷新後重送一次、強制改密碼／兩步驟的 403 整頁導走、被拒時回一般 `ApiResult`；只有標頭受 100 秒逾時，之後可以一直收；取消時回失敗、不丟例外）。
- **心跳**（0.10.7 起）：可能很久沒有資料的串流一律包 `NdjsonHeartbeat.WithHeartbeat(source, interval, ping)`（`MyProject.Web/Streaming/NdjsonHeartbeat.cs`），資料源閒置超過間隔就插一筆 `Kind=ping`，前端忽略。Agent 與工作流程 15 秒、通知推播 25 秒——中間的代理伺服器或負載平衡器常把 60 秒以上沒資料的連線當成閒置切斷。要排隊等鎖的請求也在串流裡等（先送一筆 ping 讓回應開頭立刻送出），不要在回應開始前等：等待可能超過一般請求的 100 秒逾時。
- ⚠️ **無限長的串流要自己停**（0.10.7 起，通知推播）：`NdjsonResult` 寫不出去之後照樣把序列讀完——對 AI 分析是好事（收尾一定執行），對「沒有盡頭」的序列就是永遠不結束。這種序列的**產生端要自己看 `RequestAborted`** 結束列舉並取消訂閱（例：`NotificationPushStream.ProduceAsync` 把 `RequestAborted`、網站關閉與連線最長時間連成一個取消來源）；訂閱的是 singleton 訊號時，不取消就會一直留著委派。
- **長連線要有上限與重連約定**（0.10.7 起，通知推播）：請求開始時才檢查一次身分，所以伺服器在存取 Token 到期前 1 分鐘主動結束（最後一筆 `renew`，前端刷新後重連）、滿 30 分鐘或網站關閉時結束（`reconnect`），帳號停用與強制登出靠重連時生效；每位使用者最多 3 條（超過 429）。長連線不記慢請求（`ApplicationBuilderExtensions.IsLongLivedConnection`）。
- **串流途中的錯誤放在最後一行**：已經回 200 開始串流之後（例如排隊等到鎖才驗證），錯誤寫在 `result` 那一行（`Success=false`、`Reason`、`ErrorMessage`），不能再改狀態碼。

## 統一回傳格式
所有一般 Web API 回應固定使用 `MyProject.Dtos.Commons.ApiResult<T>` 或非泛型 `ApiResult`。

標準欄位：
- `Success`：是否成功。
- `StatusCode`：HTTP 狀態碼。
- `Message`：使用者可讀訊息。
- `Data`：成功時的回傳資料。
- `Errors`：欄位或規則錯誤集合。
- `TraceId`：**錯誤追蹤碼**（0.9.78 起，LOG-10）。值是 8 碼短碼（例：`7K3QX9MD`），由 `UseHttpRequestLogging`
  以 `TraceCode.New()` 產生並**取代 `HttpContext.TraceIdentifier`**，因此與日誌檔的 TraceId 欄位、`/Error` 頁、
  系統例外紀錄的 `LastTraceId` 是同一個碼，管理員可在 `/logs` 以「錯誤追蹤碼」篩選找回同一次請求。
  目前會填入的路徑：`ApiExceptionFilter`（未處理例外）、`this.ApiServerError(...)`、JWT 的 401／403 事件、`this.FromVerify(...)` 的失敗與 `this.ForbidUnless(...)` 的 403（0.10.2 起）；
  一般驗證錯誤（400）、`HasPermission` 的 403、限流的 429 不帶（為 `null`）。WASM 頁面的錯誤通知會附上最近一次失敗回應的這個碼（見[開發慣例與限制速查 §6.33](開發慣例與限制速查.md)）。
- `Exception`：例外資訊。Development 或 `Security:ReturnExceptionDetails=true` 時回傳；Production 預設不回傳。

相容欄位：
- `ErrorMessage`：舊版錯誤訊息欄位，暫時保留。
- `ErrorDetail`：舊版錯誤詳細欄位，暫時保留。
- `ErrorCode`（0.10.0 起）：機器可判讀的錯誤代碼，讓 WASM 前端與 App 不必比對中文訊息就能決定下一步；可能的值集中在 `MyProject.Dtos.Commons.ApiErrorCodes`（目前是 `PASSWORD_CHANGE_REQUIRED`、`TWO_FACTOR_SETUP_REQUIRED`、`NO_PRIMARY_ROLE`、`TWO_FACTOR_TICKET_INVALID`），沒有特別代碼時為 `null`。新增代碼時加在 `ApiErrorCodes`，不要在各處寫字串。
- `Timestamp`：舊版時間戳欄位，暫時保留。

## HTTP 狀態碼
API 必須維持語意正確的 HTTP 狀態碼，Body 再包成 `ApiResult<T>`。

| 狀態碼 | 使用情境 |
| --- | --- |
| 200 | 查詢、更新、刪除等成功 |
| 400 | ModelState 或商業規則驗證失敗（例：指派了自己範圍外的團隊、團隊樹的上層不合法；匯出超過 10000 筆）|
| 401 | 未登入、未提供 Bearer token、token 無效 |
| 403 | 已登入但權限不足（由 `HasPermissionAttribute` 判定產生；只限管理員的端點由 `AdminOnlyAttribute` 產生，0.10.4 起；某個參數要更高權限時由 `this.ForbidUnless(...)` 產生，0.10.2 起） |
| 404 | 找不到指定資源。`/api` 底下根本沒有這個路由時（0.10.10 起）由 `UseApiStatusCodePages` 補上 `ApiResult`（「找不到這個 API。」；方法不符的 405 同樣補信封），0.10.9 以前是重跑成 `/not-found` 頁 |
| 409 | 資料衝突：名稱重複，或 `PUT` 帶來的 `ConcurrencyStamp` 與資料庫不符（別人已先修改，0.9.93 起）；還原時已有同名的資料、永久刪除時還有其他資料以它為上層 |
| 429 | 超過限流配額（`api` 分區：已登入每人、未登入每個 IP；登入相關端點另有較嚴的每 IP 配額，見下方「限流」），Body 同樣是 `ApiResult`。WASM 前端把它當成暫時狀況：不清 Token、不回登入頁，外框顯示「請求過於頻繁，請稍後再試。」（0.10.5 起，見[認證授權與權限機制](../security/認證授權與權限機制.md)）|
| 500 | 未預期例外 |

### 服務結果轉成狀態碼（0.10.2 起）

服務的寫入方法回傳 `VerifyRecordResult`，0.10.2 起多了 `Kind`（`VerifyRecordErrorKind`，`MyProject.Models/Systems/VerifyRecordResult.cs`）標示失敗的種類；畫面只看 `Message`，不受影響。服務以 `VerifyRecordResultFactory.NotFound`／`Conflict`／`Invalid`／`Error` 建立失敗結果，Controller 以 `this.FromVerify(result, 成功訊息)`（`Controllers/ControllerVerifyResultExtensions.cs`）轉成回應：

| `Kind` | 狀態碼 | 例 |
| --- | --- | --- |
| `NotFound` | 404 | 找不到，或不在自己的團隊範圍內（一律當作不存在）|
| `Conflict` | 409 | 名稱或代號重複、版本衝突、還原撞名、永久刪除時仍被引用 |
| `Invalid`、`None` | 400 | 輸入不符規則（例如指定範圍外的團隊、上層選到自己的下屬）|
| `Error` | 500 | 未預期的錯誤（已記錯誤日誌）；例外細節同樣交給 `ApiServerError` 依 `ExceptionDetailPolicy` 決定是否回傳 |

- 訊息就是畫面上看到的那一句（0.10.2 起分類、團隊，0.10.3 起專案，0.10.4 起使用者、角色的 API 錯誤訊息與畫面相同；網址、狀態碼的分類方式與欄位不變）。0.10.4 起 `MyUserService`、`RoleViewService`、`TwoFactorService` 的失敗也都標上 `Kind`。
- 端點本身只要求較低的權限、但某個參數需要更高權限時（例如搜尋與匯出的 `Deleted=true` 要刪除權限），用 `this.ForbidUnless(允許, 訊息)`：不允許時回 403 `ApiResult`（帶 `TraceId`），允許時回 null。判斷一律用 `PermissionRules.CanAccessAction`（與畫面同一份規則）。
- 修改時先確認資料在自己的範圍內（範圍外 404），再比對名稱：不要讓範圍外的資料因為名稱重複回 409 而透露它存在。存檔前檢查端點（`POST check`）同理，而且要有新增（`Id=0`）或修改（`Id>0`）權限，只能看的人不能拿它探名稱。
- 新增成功後服務會把資料庫配的 `Id` 與 `ConcurrencyStamp` 寫回傳入的畫面模型，Controller 直接用它組回應。

## JWT 與 Swagger
Swagger 已設定 Bearer security definition。開發者可在 Swagger UI 透過 Authorize 輸入 JWT access token 後測試受保護 API。

JWT access token 與 refresh token 由 `AuthController` 提供：
- `POST /api/Auth/login`
- `POST /api/Auth/refresh`
- `GET /api/Auth/me`
- `POST /api/v1/Auth/login`
- `POST /api/v1/Auth/refresh`
- `GET /api/v1/Auth/me`

0.10.8 起網頁也只用 Token 登入（不發登入 Cookie）：網頁的登入、兩步驟驗證、Google 代碼兌換、新分頁取得 Token、密碼與兩步驟驗證設定在 `api/v1/Auth` 底下的 `WebAuthController`、`AuthPasswordController`、`TwoFactorSetupController`，清單見《[Web API 端點目錄](Web%20API%20端點目錄.md)》第四之一節。
⚠️ 「改到自己會換工作階段版本」的端點（改自己的密碼、角色、啟用／停用兩步驟驗證）**直接回這台的新 Token**（`SessionRenewalDto.Tokens`／`UserSaveResultDto.Tokens`，`SelfSessionRenewal`），不要回換發網址讓前端再登入一次。

## 限流（0.10.8 起排在驗證之後）

- 全站 API 套 `api` 分區（`MapControllers().RequireRateLimiting("api")`），分區鍵：**已登入依每人、未登入依 IP**（`ResolvePartitionKey`）。
- ⚠️ **`UseRateLimiter` 必須排在 `UseAuthentication` 之後**（0.10.8 獨立審查）：分區鍵要讀已驗證的使用者，排在前面全部都是匿名、全部依 IP 分區——同一個出口 IP（NAT、反向代理）的所有人共用一份配額。`PerUserRateLimitTests` 守門。
- **較嚴格的登入配額**（每 IP 每分鐘 `RateLimit:LoginRequestsPerMinute`，預設 10）寫在同一個 policy 裡、依路徑判斷（`LoginQuotaPaths`）：App 的 `Auth/login`、`two-factor`（含 `two-factor/setup`：重新產生備用碼與停用時驗證碼錯了不計入鎖定，只靠這裡擋）、`google`、`password`、`session`。
  **不算**的例外（`LoginQuotaExcludedPaths` 與路徑本身）：網頁登入 `login/web`（靠帳號鎖定，受一般 API 每 IP 配額；算進每 IP 10 次的話同一個出口 IP 的公司早上登入尖峰會被擋）、`login-settings`（每次開登入頁、啟動頁都呼叫，沒有可猜的東西）。
- ⚠️ 不要用 `[EnableRateLimiting("login")]` 屬性另設 policy：端點慣例 `RequireRateLimiting("api")` 套用時機晚於屬性，會把它蓋掉而靜默失效。

## 功能級／動作級授權
身分驗證（`[Authorize]`）之外，受保護 CRUD **每個動作方法**再以 `[HasPermission(頁面鍵, 動作)]`（`src/MyProject/MyProject.Web/Filters/HasPermissionAttribute.cs`）強制授權：

- 由 `IPermissionChecker`（`MyProject.Business/Services/Other/PermissionChecker.cs`，UI 與 API 共用的單一 RBAC 權威來源）判權；**管理員短路**一律通過。
- 動作對應：GET/`search`→`view`、POST→`create`、PUT→`edit`、DELETE→`delete`（動作常數見 `MyProject.Share/Helpers/PermissionKeys.cs`）。
- **權限鍵兩型**：裸頁面鍵（如 `專案項目`）＝該頁全動作（向後相容）；動作鍵（如 `專案項目:edit`）＝特定動作。「唯讀角色」只給 `專案項目:view`。
- 無權限一律回 **403**，Body 維持 `ApiResult` 格式並標示缺少的權限鍵。多角色時權限取聯集。
- 詳見 [認證授權與權限機制](../security/認證授權與權限機制.md)。

**每個 action 都要標註授權意圖**（0.10.4 起，WASM 遷移 M5）。新增 API 時依下表選一個，標在類別上等於每個 action 都標（`ApiAuthorizationConventionTests` 會掃 `MyProject.Web` 裡所有 `[ApiController]` 的 public action，含繼承自基底 controller 的，四個都沒標就失敗；除了 `[AllowAnonymous]`，也檢查 controller 或 action 上有 `[Authorize]`）：

| 標註 | 什麼時候用 | 例 |
| --- | --- | --- |
| `[HasPermission(頁面鍵, 動作)]` | 屬於某個頁面、權限在角色矩陣裡 | 分類、團隊、專案；0.10.7 起 Agent 測試台（另要專案項目的新增／刪除才能產生／清除測試專案）、Agent 工作流程 |
| `[AdminOnly]` | 只有管理員能用、權限鍵刻意不放進角色矩陣的頁面 | 使用者、角色、稽核紀錄、寄信紀錄（`api/v1/users`、`roles`、`audit-logs`、`email-logs`）；0.10.5 起日誌與 AI 管理頁；0.10.6 起維運頁（`scheduled-jobs`、`system-health`、`backups`、`data-retention`、`database-usage`、`system-parameters`、公告管理）與首頁的「排程作業」小工具；0.10.7 起系統管理助理與巡檢報告（`admin-assistant`）|
| `[SignedInUser]` | 登入就能呼叫、刻意不檢查頁面權限（每個人只處理自己的東西）| 個人資料、WASM 外框、通知（0.10.7 起含推播串流 `notifications/stream`）、公告、`Auth/me`；0.10.8 起變更自己的密碼、兩步驟驗證設定 |
| `[AllowAnonymous]` | 不需登入（自己另有憑證或只回公開資訊）| 登入、刷新、`shell/culture`、下載票證兌換；0.10.8 起登入頁設定、網頁登入、兩步驟驗證第二步、Google 代碼兌換、新分頁取得 Token（憑刷新 Token）、忘記／重設密碼 |

**強制改密碼／設定兩步驟驗證期間**（0.10.0 起，全域 `ApiSessionGateFilter`）：業務 API 一律 403＋`errorCode`（`PASSWORD_CHANGE_REQUIRED`／`TWO_FACTOR_SETUP_REQUIRED`）。只有完成設定所需的端點才另外標 `[AllowDuringForcedSetup]`——0.10.8 起是 `Auth/me`、`Auth/logout`、`Auth/password/change`、`Auth/two-factor/setup`，以及強制頁外框要用的 `shell`、`notifications`、`announcements/active` 與 `dismiss`（強制頁顯示完整外框）。⚠️ 新增的業務端點**不要**標。

**沒有主要角色的帳號**（0.10.8 起）：網頁管道的 Token 一律 403 `NO_PRIMARY_ROLE`，前端登出；App 的 API 登入不受影響。

- `[AdminOnly]`（`MyProject.Web/Filters/AdminOnlyAttribute.cs`）是授權篩選器：沒登入回 401；不是管理員回 403 `ApiResult`（預設訊息「只有管理員可以使用這個功能。」，可用 `[AdminOnly("訊息")]` 換），並與 `HasPermission` 一樣寫一筆 `Permission.Denied`（目標 `ApiEndpoint`／請求路徑，detail「需要管理員」）。管理員身分看 `ApiActorMiddleware` 從資料庫載入的目前使用者，所以剛被取消管理員的人下一個請求就被擋。
  ⚠️ 只對 JWT Bearer 的請求有效（0.10.6 起已沒有以 Cookie 驗證的 API；0.10.8 起網頁也不發登入 Cookie）。
- `[SignedInUser]` 本身沒有行為（登入由 `[Authorize]` 擋），只是明說「這裡刻意不檢查頁面權限」，讓守門測試分得出「忘了標」和「本來就不需要」。用了它的端點一律只能讀改目前使用者自己的資料。
- **伺服器要自己驗證**（0.10.4 起的管理頁 API）：controller 套 `[ApiValidationFilter]`（搜尋 DTO 的每頁筆數 1～200、頁碼 ≥ 1，不合法回 400），畫面表單上的必填也由伺服器再驗一次——API 呼叫端不經過畫面。從呼叫端收到的清單或矩陣只採納伺服器認得的項目（例：角色 API 把矩陣套到權限目錄的範本上，目錄外的節點忽略）。
- **本地時間以不帶時差送出**（0.10.4 起）：回給 WASM 的本地時間用 `DateTimeKind.Unspecified`（JSON 不帶 `+08:00`），否則瀏覽器會換成自己的時區；UTC 欄位以 `Z` 結尾。⚠️ `TimeZoneInfo.ConvertTimeFromUtc` 回的是 `Kind=Local`，要再 `DateTime.SpecifyKind(…, Unspecified)`（0.10.6 修正了 M2 留下的三處：`NotificationsController`、`ProfileController`、`ShellController` 的 `StartedAtLocal`；`DateTimeOffset.LocalDateTime` 同樣是 `Kind=Local`）。「現在」「今天」也用伺服器時間（0.10.6 起資料保留、公告的清單直接帶伺服器的現在）。WASM 送來的日期篩選同樣不帶時差（`ClientCulture.Unzoned`），伺服器照自己的時區解讀。
- 回應含明碼密碼等機密時加 `Cache-Control: no-store`（例：`GET api/v1/users/password/generate`）。

## 例外與驗證
- `ApiValidationFilter` 會將 ModelState 錯誤轉成 `ApiResult<T>.Errors`。
- `ApiExceptionFilter` 會攔截未處理 API 例外，依 `Security:ReturnExceptionDetails` 決定是否填入 `ApiResult.Exception`。
- Controller 自行 `catch` 後回 500 時，請用 `this.ApiServerError(...)`
  （`Controllers/ControllerApiResponseExtensions.cs`），它**同樣**遵守該設定。
  兩條路徑共用 `Configuration/ExceptionDetailPolicy` 這個單一判斷來源。
  ⚠️ **不要自己組 `ApiResult.ServerErrorResult(message, exception)` 回傳** ——
  那個多載會無條件塞入 `exception.ToString()` 與堆疊追蹤，Production 會外洩。

> 沿革：0.4.34 之前 `ApiServerError` 正是這樣繞過設定的（Controller 中共 16 處），
> 導致 Production 設了 `ReturnExceptionDetails: false` 仍回傳完整堆疊。
> 已由 `ApiExceptionDetailSuppressionTests` 兩條路徑一起守門。

- 例外本身請由呼叫端自行 `logger.LogError(ex, ...)` 留在**伺服器日誌**；
  要不要回傳給呼叫端，一律交由 `ExceptionDetailPolicy` 決定，不要在 Controller 內自行判斷。

## 待辦
- [x] 補完整 integration tests，實際驗證 400、401、403、404、409、500 的 HTTP body。
- [x] 正式環境預設隱藏 `Exception.StackTrace`。
- [x] 已導入 `/api/v1/...` 平行路由，目前保留 `/api/...` 相容路由。
