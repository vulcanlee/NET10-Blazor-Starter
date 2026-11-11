# WASM 遷移 M9：純 Token 登入（0.10.8）

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.10.8
- 首次實作版本：0.10.8
- 最後核對日期：2026/11/11

## 目的

WASM 遷移的第九個里程碑（計畫見 [WASM 遷移計畫](../migration/WASM遷移計畫.md)）：**網頁改成只用 Token 登入、不再發登入 Cookie**，登入相關頁面與剩下的 Server 頁面全部搬到 WebAssembly。
做完之後 36 個頁面全部在使用者的瀏覽器執行，網頁與未來的 App 用同一套 API 與同一種登入憑證；Server 渲染的基礎留到 M10 移除。
分兩段在同一個分支完成、一次合併：9a 先補 API、搬 5 個中性頁面（登入方式不變，仍發 Cookie、保留 bridge）；9b 才真正切換、停發 Cookie。
測試報告見 [M09-純Token登入](../migration/測試報告/M09-純Token登入.md)。

## 變更內容

### 1. 全部頁面改用 WebAssembly

- **9a**：`/ChangePassword`、`/TwoFactorSetup`、`/`（啟動頁）、`/Error`、`/not-found` 搬到 `MyProject.Web.Client`。錯誤頁的追蹤碼改經網址 `/Error?trace=` 帶過去（只接受英數大寫）；正式環境頁面出錯改成 302 導到它，API 請求回 500 `ApiResult`（`Diagnostics/ErrorPageRedirect.cs`）。`/not-found` 用新的 `ClientEmptyLayout`。
- **9b**：`/Auths/Login`（含 `?reset=1`、`?sso=…` 的固定提示文字、Google 按鈕與 `#code=` 兌換）、`/Auths/TwoFactor`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`（主機對這一頁的整頁回應加 `no-referrer`、`no-store`）、`/Auths/Pending`、`/Auths/Logout`（呼叫登出 API、清 Token、通知其他分頁）搬到 `Views/Auths/`。匿名頁用 `ClientNoFooterLayout`＋`AuthShell`（`b-auth-shell` 共用 scope 一起搬），CSS 照抄。登入頁保留 `#account`、`#password`、`#captcha`、隱藏欄位 `Input.CaptchaCode`、`#rememberMe`、`button.login-button`。`ReturnUrlGuard`、`AuthCaptcha` 搬到 Client。
- Server 版頁面與 `Components/Pages` 全部刪除。**目前 36 個頁面全部以 WebAssembly 執行**（M2～M8 的 25 個加上這次的 11 個）。

### 2. 新的 Web API

全部在 `api/v1/Auth` 底下、回 `ApiResult`，Business 層的稽核代碼不變。端點細節見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md) 第四之一節，DTO 在 `MyProject.Dtos/Auths/WebAuthDtos.cs`。

- **`WebAuthController`**（匿名）：`GET login-settings`（Google 與寄信是否啟用、鎖定次數與分鐘、記住裝置與記住我天數、密碼規則、系統名稱與說明、重設連結有效分鐘）、`POST login/web`（記住我、記住裝置 Token；需要兩步驟驗證時回待驗票證；必須設定兩步驟驗證的帳號也拿得到 Token，交給閘門）、`POST two-factor`（票證＋驗證碼或備用碼＋記住這台裝置）、`POST google/exchange`（Google 一次性代碼換 Token）、`POST session/fork`（新分頁取得 Token）。
- **`AuthPasswordController`**：`POST password/forgot`、`password/reset/check`、`password/reset`（匿名）；`GET`／`POST password/change`（登入即可、強制改密碼期間可用；成功回新的一組 Token、撤銷舊的一串）。
- **`TwoFactorSetupController`**（`two-factor/setup`，登入即可、強制設定期間可用）：`GET` 狀態、`POST start`（伺服器產生 QR PNG data URI＋設定票證，**金鑰確認前不進資料庫**）、`enable`（回備用碼與新 Token）、`backup-codes`、`disable`（回新 Token）。
- `PUT api/v1/users/{id}` 改到自己時回 `Tokens`（`UserSaveResultDto` 改繼承 `SessionRenewalDto`），**移除 `SessionRefreshUrl`**。
- `ApiErrorCodes` 新增 `NO_PRIMARY_ROLE`、`TWO_FACTOR_TICKET_INVALID`。
- App 用的 `POST api/v1/Auth/login`（同一個請求帶 `TwoFactorCode`）契約不變。

### 3. 停發登入 Cookie

- 預設驗證機制改成 JWT；**登入 Cookie 的 scheme 移除**（原計畫放 M10，這次提前：沒有任何流程發它，留著只會讓舊的 `.MyProject.Auth` Cookie 還有地方被讀）。Google 登入過程用的 5 分鐘暫存外部 Cookie 保留。
- 移除：`POST api/v1/Auth/bridge`、`/Auths/RefreshSession`、`SessionRefreshTicketService`／`SessionRefreshNavigator`、`SecurityStampCookieEvents`、`CookieClaims`、`TwoFactorLoginCookies`（待驗與記住裝置兩個 Cookie）、`auth-reset.js`、`ExpectedUserId`、`/UploadFiles` 靜態下載（只認登入 Cookie、沒有任何地方連到它）。
- 系統健康監控的「身分驗證」改檢查「JWT 是預設驗證機制且設定完整」。
- Google 回呼不再發 Cookie：產生一次性代碼（只存雜湊、60 秒、只能用一次、綁定發起的瀏覽器的驗證值），導回 `/Auths/Login#code=`，前端以 `google/exchange` 兌換。沒帶綁定值的舊流程一律回登入頁，不建立或連結帳號。

### 4. Token 效期與撤銷

- 網頁的 Token 屬於發出管道 `web`：勾「記住我」→ localStorage、整串上限 `RememberMeDays`（30 天，從登入算）；沒勾 → sessionStorage（關掉分頁就登出）、刷新 Token 閒置 `IdleTimeoutMinutes`（480 分鐘，滑動；`IdleTimeoutSliding=false` 時為從登入起的絕對上限）；存取 Token `AccessTokenMinutes`（15 分鐘）。規則集中在 `WebSessionTokens`；App 的 API 登入照舊看 `JwtSettings`。
- 設定區段 **`SessionSettings` 取代 `CookieSettings`**；舊鍵（`ExpireMinutes`、`RememberMeDays`、`ValidationIntervalMinutes`、`SlidingExpiration`）仍相容，會蓋過新區段的值並在啟動時記警告。
- 存取 Token 帶這一串的識別 `fid` 與發出管道 `client_kind`，每個請求檢查這一串是否已撤銷：同一台主機登出立即生效，多台最多 30 秒。
- 待驗票證（5 分鐘）、記住裝置 Token（`RememberDeviceDays` 天）、兩步驟驗證設定票證（15 分鐘）以 Data Protection（不同用途）加密，綁使用者與工作階段版本，到期時間在加密內容裡（`TwoFactorLoginTickets`、`TwoFactorSetupTickets`）。
- 改到自己會換工作階段版本的操作（改密碼、啟用／停用兩步驟驗證、管理員改自己）由 `SelfSessionRenewal` 把這次請求所屬的那一串換成新的一串，回這台的新 Token；其他裝置與這台的舊 Token 都失效。
- 沒有主要角色的帳號：網頁 Token 一律 403 `NO_PRIMARY_ROLE`，前端登出。M7 起 `/App` 在 WASM 時這裡其實有缺口（Server 版會登出），9a 先寫測試證明後補上。

### 5. WASM 工作階段（`ClientSession`）

- 每個頁面**拿掉 `[Authorize]`**，由外框 `ClientMainLayout` 把關：登入檢查（Token、`/me`）通過前不渲染任何東西，未登入帶 `returnUrl`（經 `ReturnUrlGuard`）回登入頁；未登入的整頁請求只拿到空的 WASM 外殼。
- **新分頁取得 Token**：沒有 Token 時以 BroadcastChannel 向已開分頁要，對方在刷新鎖內呼叫 `session/fork` 開一串新的交過來（沿用登入識別、記住我與整串上限；每個分頁各一串，不會互相觸發重用偵測），1 秒沒人回應就回登入頁。
- **監聽 `storage` 事件**：記住我的 Token 被別的分頁清掉就回登入頁、換人就整頁重新載入、同一人刷新就沿用。
- **同一個瀏覽器只有一個身分**（與 Server 版的 Cookie 相同）：登入成功時廣播「誰登入了」，顯示另一個人的分頁丟掉自己這個分頁的 Token、重新載入改用新的登入。
- **登出**依登入識別撤銷這次登入的每一串，清掉瀏覽器存的 Token 並通知其他分頁。
- **強制改密碼／設定兩步驟驗證**：外框需要的 API（`shell`、通知、公告的讀取與關閉）加 `[AllowDuringForcedSetup]`，強制頁照 Server 版顯示完整外框；已在目標頁就不再整頁導頁（那兩頁也在 WASM，否則會無限重新載入）；業務 API 一律 403＋錯誤代碼。
- 改密碼、啟用／停用兩步驟驗證、管理員改自己之後，存下 API 回的新 Token 再整頁重新載入（不再經過換發 Cookie 的頁面）。
- 匿名頁的 API 呼叫不帶 Token、401 不導回登入頁；瀏覽器錯誤在沒登入時只留在主控台。

### 6. 限流

- **限流移到驗證之後**（`UseAuthentication` → `UseRateLimiter` → `UseAuthorization`）：已登入的請求依每人分區、未登入依 IP。以前排在驗證之前，全部依 IP 分區，同一個出口 IP 的所有人共用一份配額。
- 嚴格登入配額（每 IP 每分鐘 `RateLimit:LoginRequestsPerMinute`，預設 10）涵蓋 App 的 `login`、`two-factor`（含設定）、`google`、`password`、`session`；**網頁登入 `login/web` 與 `login-settings` 不算**（照 Server 版畫面登入靠帳號鎖定，受一般 API 每 IP 配額）。

### 7. 獨立審查（7 項，全部修正）

1. 中高：偷到的刷新 Token 先 `session/fork` 一串自己的，可躲過重用偵測 → 重用時撤銷同一次網頁登入的每一串（補測試）。
2. 中：限流在驗證之前，「每人」配額實際是每 IP；網頁登入被算進每 IP 10 次 → 限流移到驗證之後、網頁登入不算嚴格配額（補 `PerUserRateLimitTests`）。
3. 中：舊設定 `CookieSettings:SlidingExpiration=false` 被靜默忽略、變成永不到期 → 新增 `IdleTimeoutSliding`，舊鍵相容（補測試）。
4. 低：同一瀏覽器兩個分頁登入不同人時，新分頁可能拿到另一人的 Token → 登入廣播，同一瀏覽器只有一個身分（補測試）。
5. 低：fork 與登出同時發生時新串可能沒被撤銷 → fork 後再確認來源沒被撤銷，否則作廢（補測試）。
6. 低：不再寫 `Login.SessionExpired`，個人資料的登入紀錄少一類 → 刷新因版本換掉而失敗時補寫（同 Server 版的內容）。
7. 低：匿名頁的瀏覽器錯誤回報會觸發 401 而被導回登入頁 → 沒登入時只留在主控台。

### 8. 與 Server 版不同、使用者看得到的地方

1. 沒勾「記住我」時，**關掉分頁就登出**（Server 版要關掉整個瀏覽器）；同一個瀏覽器開新分頁會自動接續已開分頁的登入。
2. 同一個瀏覽器換人登入，其他分頁也會換成那個人（重新載入）。
3. 管理員改自己的密碼、角色，或自己改密碼、啟用／停用兩步驟驗證之後，這一頁直接重新載入繼續使用，不再經過換發登入的過場頁。
4. 一個分頁登出，其他分頁也回登入頁。
5. 錯誤頁的追蹤碼經網址帶過去；正式環境頁面出錯會導到 `/Error?trace=…`。

### 9. 守門測試調整

- `PageAuthorizationTests` 改寫：頁面不可有 `[Authorize]`、匿名白名單 9 頁（`SplashPage`、`ErrorPage`、`NotFoundPage`、`Login`、`Logout`、`Pending`、`ForgotPassword`、`ResetPassword`、`TwoFactor`）、不是匿名頁不可指定 `ClientMainLayout` 以外的版面、未登入的整頁請求只拿到空的 WASM 外殼；呼叫 `Check()` 的元件掃描已沒有對象（規則留著，M10 一併刪）。
- `ApiIntegrationTests.AuthCookieName_ShouldBeProjectSpecific` 改檢查 Google 的暫存外部 Cookie 名稱 `.MyProject.External`，並確認不再設定 `.MyProject.Auth`。
- `TokenApiIntegrationTests` 刪除 bridge 相關測試。

### 10. 權限不變

- 頁面與 API 的權限規則不變；登入相關 API 依用途標 `[AllowAnonymous]` 或 `[SignedInUser]`。

### 11. 測試

- 測試 2,970 → 3,050。新增或改寫：`AccountSelfServiceApiTests`、網頁登入的整合測試（`login/web`、`two-factor`、`google/exchange`、`session/fork`、密碼、兩步驟驗證設定、沒有主要角色、登入頁設定）、`SessionSettingsTests`、`PageAuthorizationTests`、`TokenApiIntegrationTests`、`SessionTests`、`TwoFactorTests`、`Client/ClientSessionTests`（新分頁取得 Token、兩個分頁同時刷新、`storage` 事件、登入廣播）、`EmailPagesIntegrationTests`（改走 API 與回應標頭）、`ErrorPageIntegrationTests`、登入頁元件測試、`RefreshTokenStoreTests`（重用撤銷整次登入、登出後不能 fork）、`PerUserRateLimitTests`。
- 實機驗證腳本：`common.mjs`（`login` 等 WASM 表單；`apiAsPageUser` 改從 storage 讀 Token，另有 `pageTokens`、`apiJsonAsPageUser`）、`scripts/e2e/m09-views.mjs`（10 個畫面比對）、`m09-auth.mjs`（M9-T1～T11 登入情境＋36 路由煙霧測試）；m01、m02、m03、m04、m05、m07、m08 跟著改；`smoke-routes.mjs` 移除 `/Auths/RefreshSession`（36 個路由）。`Start-E2EHost.ps1` 的信件一律存成 .eml 到隔離資料夾（開發機 User Secrets 可能設了真的 SMTP）。用法見 [`scripts/e2e/README.md`](../../scripts/e2e/README.md)。
- 實機注意：WASM 頁面一直開著通知推播連線，腳本不可用 `waitForLoadState('networkidle')`，一律等畫面元素出現。

## 升版注意

- 版本號 0.10.7 → 0.10.8。
- 沒有資料庫異動（登入識別 `LoginSessionId` 在 M1 已有）。
- **網頁使用者升級後通常要重新登入一次**：舊的 `.MyProject.Auth` Cookie 不再被讀取。
- **設定改名**：`CookieSettings` → `SessionSettings`（`ExpireMinutes` → `IdleTimeoutMinutes`、`SlidingExpiration` → `IdleTimeoutSliding`，`RememberMeDays`、`ValidationIntervalMinutes` 同名，另有 `AccessTokenMinutes`）。舊鍵仍相容、啟動時記警告；`appsettings.Production.json` 或環境變數裡的舊鍵請改成新鍵。見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)。
- **Data Protection 金鑰環**（`DataProtectionKeyPath`）現在保護待驗票證、記住裝置 Token、設定票證與 Google 暫存 Cookie：要持久化、多台主機共用；換掉的話記住的裝置要重新驗證、進行中的登入要重來。
- Google 一次性代碼與票證「已用過」的紀錄只在單一行程的記憶體：多台主機要黏著工作階段（IIS 單一站台不受影響）。Google Console 的回呼網址不變。
- 反向代理或負載平衡器後面的部署：限流現在依每人分區，未登入依 IP——`UseForwardedHeaders` 的 `KnownProxies`／`KnownNetworks` 仍要設定，否則 IP 會被偽造。
- 衍生專案若改過登入相關的 Server 頁面（`Components/Auths/*`、`Components/Pages/ChangePassword.razor`、`TwoFactorSetupPage.razor`、`Error.razor`、`NotFound.razor`）：改動要搬到 `MyProject.Web.Client` 的同名頁面與檢視，資料改經 `api/v1/Auth`。
- 衍生專案若新增了「改到自己會換工作階段版本」的 API：改回新 Token（`SelfSessionRenewal`），不要再回 `SessionRefreshUrl`。
- 衍生專案若有呼叫 `POST api/v1/Auth/bridge` 或讀 `UserSaveResultDto.SessionRefreshUrl` 的用戶端：兩者都已移除。
- 衍生專案若新增了頁面：不要標 `[Authorize]`（標了整頁請求會拿到 401），由 `ClientMainLayout` 把關；匿名頁要列進 `PageAuthorizationTests.AnonymousPages`。
