# 登入與帳號流程 PRD

- 文件版本：1.21
- 文件狀態：已實作
- 現行系統版本：0.10.8
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/11/11

## 一、目標與範圍

提供本專案的身分驗證與帳號自助維護能力，涵蓋本地帳密登入、Google OAuth2 第三方登入、登出、待審核導向、變更密碼，以及忘記密碼／以 Email 連結重設密碼（0.9.60 起）。0.10.8 起（WASM 遷移 M9）網頁與 API 都以 Token（JWT Bearer＋刷新 Token）登入，**不再發登入 Cookie**；網頁與 App 是不同的發出管道（`web`／`api`），效期規則不同，共用同一份使用者與 RBAC 權威來源。0.10.7 以前網頁採 Cookie 驗證。

- **範圍**：`/Auths/Login`、`/Auths/TwoFactor`（0.9.104 起，登入第二步）、`/Auths/Logout`、`/Auths/Pending`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`、`/ChangePassword`；Google 導向端點 `/Auths/Google/Login`、`/Auths/Google/Callback`；API 端 `/api/v1/Auth/login`（App）、`login-settings`、`login/web`、`two-factor`、`google/exchange`、`session/fork`、`refresh`、`logout`、`me`、`password/*`（0.10.8 起，端點細節見 [Web API 端點目錄](../architecture/Web%20API%20端點目錄.md)）。帳號安全（PBKDF2 雜湊、登入失敗鎖定、登入時雜湊自動升級）。
- **非範圍**：帳號 CRUD 與角色／團隊指派見 [使用者管理](使用者管理-prd.md)；角色與權限矩陣見 [角色管理](角色管理-prd.md)。兩步驟驗證的設定頁、備用碼、強制與管理員重設見 [兩步驟驗證](兩步驟驗證-prd.md)；本 PRD 只描述登入流程裡的第二步。

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/Auths/Login` | `ClientNoFooterLayout`＋`AuthShell`（0.10.8 起 WASM；之前是靜態 SSR 表單 POST）| 匿名 | 所有人 |
| `/Auths/Logout` | `ClientEmptyLayout`（0.10.8 起 WASM；0.9.95～0.10.7 是 Server 版 `EmptyLayout`）| 匿名（有 Token 就撤銷）| 所有登入者 |
| `/Auths/Pending` | `ClientNoFooterLayout`（0.10.8 起 WASM）| 匿名 | Google 自動建帳待審核者 |
| `/Auths/TwoFactor` | `ClientNoFooterLayout`＋`AuthShell`（0.10.8 起 WASM）| 匿名＋這個分頁有有效的待驗票證 | 已啟用兩步驟驗證、密碼或 Google 已通過的人（0.9.104 起）|
| `/Auths/ForgotPassword` | `ClientNoFooterLayout`＋`AuthShell`（0.10.8 起 WASM）| 匿名 | 忘記密碼者（0.9.60 起；寄信未啟用時只顯示「未啟用」）|
| `/Auths/ResetPassword?token=…` | `ClientNoFooterLayout`＋`AuthShell`（0.10.8 起 WASM）| 匿名＋有效 token | 收到重設信的人（0.9.60 起）|
| `/ChangePassword` | `ClientMainLayout`（0.10.8 起 WASM；強制改密碼期間也顯示完整外框）| 已登入 | 需變更密碼者 |
| `/api/v1/Auth/login`、`refresh` | — | 匿名（帳密換 JWT）| API 用戶端（App）；`refresh` 網頁也用 |
| `/api/v1/Auth/login-settings`、`login/web`、`two-factor`、`google/exchange`、`session/fork`、`password/forgot`、`password/reset*` | — | 匿名 | 網頁登入流程（0.10.8 起）|
| `/api/v1/Auth/me`、`password/change`、`two-factor/setup` | — | JWT Bearer（強制設定期間也可用）| 已登入者 |

## 三、畫面與欄位

- **登入頁**：帳號、密碼、驗證碼（4 碼數字，前端產生存於隱藏欄位 `CaptchaCode`，以 `Ordinal` 比對）、「記住我」核取方塊。`GoogleOAuthSettings.IsConfigured` 為真時顯示「使用 Google 登入」按鈕，連結至 `/Auths/Google/Login`（帶 `returnUrl`；0.10.8 起另帶這個分頁產生的驗證值雜湊 `binding`）。0.10.8 起系統名稱、Google／寄信是否啟用、鎖定規則等讀匿名的 `GET api/v1/Auth/login-settings`；元素 id 與隱藏欄位不變（實機腳本依賴）。前端逐項檢查空值與驗證碼，錯誤顯示「請輸入帳號／密碼／驗證碼」「驗證碼錯誤」。
- **登入頁（0.9.60 起）**：`EmailSettings:Provider` 不是 `None` 時，「記住我」右側的「企業級安全登入」改為「忘記密碼？」連結；從重設頁成功導回（`?reset=1`）時，表單上方顯示綠色「密碼已重設，請以新密碼登入。」。外觀由共用元件 `AuthShell` 提供，三頁一致。出貨與開發環境都是 `None`（0.9.62 起；0.9.61 曾在 `appsettings.Development.json` 預設 `Pickup`），預設看不到；本機要測，用 User Secrets 設 `Provider=Pickup`。
- **忘記密碼頁**：帳號或 Email、驗證碼（與登入頁同一套 4 碼）。送出後**一律**以中性色說明框（`.info-message`）顯示「申請已送出。若帳號存在、為啟用狀態且登記了有效的 Email，您會在幾分鐘內收到重設密碼的信，信中連結 N 分鐘內有效。若一直沒收到…請聯絡系統管理員協助。」—— 不論帳號是否存在、有沒有真的寄出（0.9.85 起；之前是綠色「…信已經寄出…」）。唯一例外：輸入 `support`（不分大小寫）時留在表單，紅字顯示「support 是系統內建帳號，不提供忘記密碼服務…」。寄信未啟用時整頁只顯示「系統目前未啟用寄信功能…請聯絡系統管理員」。
- **重設密碼頁**：副標題顯示「為帳號「X」設定新密碼。密碼規則：…」（0.9.101 起由 `IPasswordPolicy.Describe()` 產生）；新密碼、確認新密碼。token 無效／過期／已用過一律顯示「重設連結無效或已過期，請重新申請。」並提供「重新申請重設信」連結。成功後導回登入頁。
- **待審核頁**：靜態說明，告知 Google 帳號已建立但預設停用，須管理者啟用後再登入，提供返回登入頁連結。
- **變更密碼**：目前密碼、新密碼（下方顯示密碼規則）、確認新密碼（`[Compare]` 驗證一致）。`support` 開發帳號被禁止。0.9.101 起被導到這一頁時上方說明原因（管理員要求／密碼已到期）；右上角使用者選單的「變更密碼」直接開這一頁（原本的對話窗已移除）。
- **兩步驟驗證頁（0.9.104 起）**：「驗證碼或備用碼」（App 上的 6 位數或一組 `XXXXX-XXXXX` 備用碼）、「記住這台裝置 N 天」（`TwoFactorSettings:RememberDeviceDays` 為 0 時不顯示）、「驗證並登入」、「取消，回到登入頁」。錯誤一律「驗證碼不正確，或帳號已被暫時鎖定。」。
- **登入頁的鎖定說明（0.9.101 起）**：表單下方固定顯示「連續輸錯 N 次密碼會暫時鎖定 M 分鐘；被鎖定時可以用「忘記密碼」重設，或請系統管理員解鎖。」（N、M 取自 `LockoutSettings`；寄信停用時改為「請稍後再試」）。

## 四、內部系統運作

- **本地登入**（`MyUserServiceLogin.LoginAsync`，View → Service → `BackendDBContext`）：以 `Account` 查 `MyUser`（0.9.171 起去頭尾空白、不分大小寫；萬一多筆符合優先取大小寫完全相同的）；帳號不存在、密碼錯誤、鎖定中一律回 `InvalidCredentialsMessage`「帳號或密碼不正確，或帳號已被暫時鎖定。」（0.9.101 起；不區分，避免帳號枚舉）。密碼以 `SecurePasswordHasher.VerifyPassword`（PBKDF2）驗證。
- **登入失敗告警**（0.9.166 起，`LoginFailureAlertSettings`，系統參數可調）：全系統最近 10 分鐘（預設）內登入失敗達 20 次（預設，含不存在的帳號、驗證碼錯誤、Google 登入失敗）就通知全體管理員並寄信，60 分鐘內不重發；不分帳號、不分 IP，抓的是帳號鎖定擋不到的橫向猜測。見 [稽核紀錄 PRD](稽核紀錄-prd.md) §七。
- **帳號鎖定**（0.9.101 起讀 `LockoutSettings`，系統參數可調）：失敗時 `AccessFailedCount++`，達 `MaxFailedAttempts`（預設 5）設 `LockoutEndUtc = UtcNow + LockoutMinutes`（預設 15 分）並通知全體管理員（同時寄信）；
  鎖定期間不驗證密碼、不累加次數，稽核記 `Login.Failed`（`reason=Locked`），`Login.LockedOut` 只記造成鎖定的那一次。⚠️ 鎖定**到期時先歸零再驗證**（之前到期後錯一次就再鎖）。成功登入後 `AccessFailedCount` 歸零。
- **舊的 123456**：登入成功時明文是 `123456` 就設 `MustChangePassword`（舊資料只有雜湊，無法用 SQL 轉換）。
- **Google 登入**：處理順序集中在 `ExternalLoginResult.Evaluate`：已刪除 → 待開通 → **鎖定（0.9.101 起，導回 `?sso=locked`）** → 登入。
- **雜湊自動升級**：驗證回傳 `SuccessRehashNeeded`（舊格式）時，即時以 PBKDF2 重新雜湊並存回。
- **網頁登入發 Token**（0.10.8 起，`WebAuthController` `POST api/v1/Auth/login/web`＋`WebSessionTokens`；取代 Server 版登入頁的 Cookie 簽發與 `CookieClaims`）：密碼通過後開一串新的網頁 Token（發出管道 `web`，新的 `LoginSessionId`），存取 Token 帶 `security_stamp`、`fid`（這一串）與 `client_kind`。效期依「記住我」：勾了 → 前端存 localStorage，整串上限 `SessionSettings:RememberMeDays`（30 天，從登入算、不滑動）；沒勾 → 存 sessionStorage，刷新 Token 閒置 `IdleTimeoutMinutes`（480 分鐘，每次刷新重新起算；`IdleTimeoutSliding=false` 時改為從登入起的絕對上限）；存取 Token `AccessTokenMinutes`（15 分鐘）。前端存好 Token 後整頁導到 `returnUrl`（經 `ReturnUrlGuard`）或 `/App`，並廣播「誰登入了」：同一個瀏覽器顯示另一個人的分頁丟掉自己的 Token、重新載入改用這次的登入（**同一個瀏覽器只有一個身分**，與 Cookie 時代相同）。必須改密碼或設定兩步驟驗證的帳號照樣拿到 Token，由閘門擋（見「登入後狀態」）。
- **Google 登入**（`ExternalAuthController` + `ExternalLoginService.FindOrCreateAsync`）：Callback 驗證 `ExternalCookieScheme` 後，依序「有效的 GoogleId → 已刪除的 GoogleId（拒絕）→ 有效的 Email 連結既有帳號 → 已刪除的 Email（拒絕）→ 自動建立停用新帳號」（`Status=false`、`IsAdmin=false`、`Password=""`、`Salt=null`、指派預設角色）。命中已刪除的使用者（0.9.95 起）時不寫入、不連結、不新建，稽核 `Login.Sso.Failed`（`reason=Deleted`）並導向 `/Auths/Login?sso=deleted`，登入頁顯示固定文字「此帳號已被刪除，請洽系統管理員。」（`sso` 只認得固定代碼，其他值一律忽略，不回顯網址內容）。GoogleId 排在 Email 前面（使用者決定）：否則已刪除者的 Google 帳號會被連到同 Email 的另一人，之後他就因 GoogleId 衝突而無法還原。`!Status` 導向 `/Auths/Pending`。0.10.8 起回呼不再簽發 Cookie：產生一次性代碼（`GoogleLoginCodes`：只存雜湊、60 秒、只能兌換一次、綁定發起登入的瀏覽器）導回 `/Auths/Login#code=…`，登入頁以 `POST api/v1/Auth/google/exchange`（出示 sessionStorage 裡的驗證值原值）換 Token 或待驗票證；沒帶綁定值的舊流程一律導回登入頁，不建立也不連結帳號。代碼只在這個行程的記憶體，多台主機要黏著工作階段。
- **登出**（0.10.8 起）：`ClientSession.LogoutAsync` 呼叫 `POST api/v1/Auth/logout`，依 `LoginSessionId` 撤銷**這次網頁登入的每一串**（每個分頁各一串），清掉瀏覽器存的 Token、以 BroadcastChannel 通知其他分頁（它們刷新失敗就回登入頁），再整頁回 `/Auths/Login`。已發出的存取 Token 驗證時檢查那一串是否已撤銷：同一台主機立即失效，多台最晚 30 秒。連不上伺服器時照樣清掉瀏覽器的 Token。0.10.7 以前是 `SignOutAsync(CookieScheme)`。
  - **使用者主動登出**（0.9.29 起）先經二次確認：UI 入口都走
    `MyProject.Web.Client/Commons/LogoutConfirm.cs`，確認後才登出。
  - ⚠️ **系統強制登出不確認**：Token 不能用了（刷新被拒：工作階段版本換掉、停用、刪除、強制登出）時 `ClientSession` 直接清掉 Token、帶 `returnUrl` 回登入頁；
    帳號沒有主要角色（`/me` 回 403 `NO_PRIMARY_ROLE`）時直接登出 —— 對 session 已經失效的人跳「確定要登出嗎？」只會讓他卡住。
  - 直接在網址列輸入 `/Auths/Logout` 維持立即登出，刻意不擋（刻意輸入網址不是誤觸）。
  - ⚠️ **登出頁不可套用 `ClientMainLayout`**：外框會先做登入檢查，帳號已停用時又導回登出，形成重新導向迴圈（0.9.95 在 Server 版修過同一個問題）。0.10.8 起指定 `ClientEmptyLayout`、標 `[AllowAnonymous]`。
- **工作階段失效**（0.9.103 起）：存取 Token 帶著 `MyUser.SecurityStamp`（`JwtTokenService`）；改密碼、停用、刪除、角色或管理員身分變更、管理員強制登出時換版本。
  每個 API 請求由 `ApiActorMiddleware`＋`ICurrentUserLoader` 比對（同一台主機立即，其他主機最晚 `SessionSettings:ValidationIntervalMinutes`，預設 5 分鐘）；refresh 比對（不符 401，並與 Server 版相同寫一筆 `Login.SessionExpired`，個人資料頁的登入紀錄靠它，0.10.8 獨立審查補上）。
  Bearer 只接受 access token。0.10.8 起自己換了版本（`/ChangePassword` 改密碼、啟用／停用兩步驟驗證、管理員改到自己）時，API 直接回這個分頁的新 Token（`SelfSessionRenewal`：把這次請求所屬的那一串換成新的一串、舊的撤銷），前端存起來後整頁重新載入；不再經 `/Auths/RefreshSession` 換發 Cookie。沒勾「記住我」時同一個瀏覽器的其他分頁各自是另一串，也會被要求重新登入。
- **兩步驟驗證的第二步**（0.9.104 起）：帳號已啟用時 `LoginAsync` 回 `RequiresTwoFactor`（⚠️ 不歸零失敗次數、不寫 `Login.Success`）；0.10.8 起 `login/web` 回 5 分鐘的**待驗票證**（`TwoFactorLoginTickets`，Data Protection 加密：使用者 Id、工作階段版本、記住我、返回網址、登入方式），前端存在這個分頁的 sessionStorage 並導向 `/Auths/TwoFactor`（之前是加密 Cookie `.MyProject.TwoFactorPending`）。
  登入時帶上 localStorage 的**記住裝置 Token**（使用者 Id＋工作階段版本，`TwoFactorSettings:RememberDeviceDays` 天）且版本沒換時直接完成（稽核 detail `mfa=device`）。`POST api/v1/Auth/two-factor` 送出時重查版本與帳號狀態（期間改了密碼或被強制登出 → `TWO_FACTOR_TICKET_INVALID`，前端回登入頁），`CompleteSecondFactorAsync` 驗證碼對了才歸零、發 Token、票證作廢、稽核 `Login.Success`（`mfa=totp|backup`），勾了「記住這台裝置」另回記住裝置 Token；錯了稽核 `Login.TwoFactorFailed` 並累加鎖定次數（票證仍可再用）。
  Google 回呼：帳號已啟用時同樣走第二步（稽核 `Login.Sso.Success`，detail 帶 `provider=Google; mfa=…`）。API 登入以 `twoFactorCode` 欄位帶驗證碼。
- **登入後狀態**：伺服器端每個 API 請求由 `ApiActorMiddleware`＋`ICurrentUserLoader`（與 `AuthenticationStateHelper` 同一套規則）確認使用者存在且 `Status` 啟用、具主要角色，載入 `CurrentUser`（`RoleList` 以 `IPermissionChecker.GetEffectivePermissionKeysAsync` 為權威、`TeamList` 由 `EffectiveTeamResolver` 決定）。網頁 Token 遇到沒有主要角色的帳號一律 403 `NO_PRIMARY_ROLE`（0.10.8 起；App 的 API 登入不受影響），前端登出。
  `IPasswordPolicy.RequiresChange`（0.9.101 起：`MustChangePassword` 旗標或密碼已到期；support 與沒有本機密碼的帳號豁免）或必須使用兩步驟驗證卻還沒設定（0.9.104 起）時，業務 API 一律 403 並帶錯誤代碼 `PASSWORD_CHANGE_REQUIRED`／`TWO_FACTOR_SETUP_REQUIRED`（`ApiSessionGateFilter`）；完成設定與顯示外框需要的端點（`me`、`logout`、`password/change`、`two-factor/setup`、shell、通知、公告的讀取與關閉）標 `[AllowDuringForcedSetup]` 放行。前端 `ClientSession` 讀 `/me` 後整頁導到 `/ChangePassword` 或 `/TwoFactorSetup`；⚠️ 已經在那一頁就不再導（0.10.8 起那兩頁也在 WASM，再導會無限重新載入）。
  頁面不在 HTTP 層擋（0.10.8 起頁面一律不標 `[Authorize]`）：未登入的整頁請求只拿到空的 WASM 外殼，`ClientMainLayout` 登入檢查通過前不渲染內容，沒有 Token 就帶 `returnUrl` 回登入頁。
- **API 登入**（`AuthController`）：`login` 以帳密（已啟用兩步驟驗證時加 `twoFactorCode`）換 `TokenResponseDto`（JWT + Refresh，發出管道 `api`，效期看 `JwtSettings`），`refresh` 換新 Token（依那一串的發出管道決定效期），`me` 回目前使用者；一律包 `ApiResult<T>`，失敗回 401。契約 0.10.8 不變。
- **新分頁取得 Token**（0.10.8 起）：沒勾「記住我」的 Token 在 sessionStorage，新分頁讀不到。新分頁以 BroadcastChannel 向已開的分頁要，對方在跨分頁刷新鎖內呼叫 `POST api/v1/Auth/session/fork`，以自己的刷新 Token 開**一串新的**（沿用 `LoginSessionId`、記住我與整串上限，自己那一串不輪替）交過去；每個分頁各一串，不會互相觸發重用偵測。1 秒沒人回應就回登入頁。fork 後再確認來源沒被撤銷（與登出同時發生時新串作廢）。勾了「記住我」的 Token 在 localStorage 共用，另監聽 `storage` 事件：被清掉（別的分頁登出）→ 回登入頁、換成別人 → 整頁重新載入、同一人刷新 → 沿用。
- **刷新與重用偵測**：網頁與 App 都經 `IRefreshTokenStore` 輪替；剛換掉的 Token 在 30 秒寬限內又被用視為同時刷新（不撤銷），超過就是重用：撤銷整串，網頁 Token 另撤銷**同一次登入的每一串**（0.10.8 獨立審查：避免偷到的 Token 先 fork 一串自己的躲過偵測）。多個分頁刷新以 Web Locks 排隊，拿到鎖先重讀 storage。
- **稽核**：登入寫入 `Login.Success` / `Login.Failed` / `Login.Disabled` / `Login.LockedOut` / `Login.TwoFactorFailed`，Google 登入寫 `Login.Sso.Success` / `Login.Sso.Failed`，登出寫 `Logout`，工作階段失效被系統登出寫 `Login.SessionExpired`（`AuditLog`）；忘記密碼寫入 `Password.ResetRequested` / `Password.ResetCompleted` / `Password.ResetFailed`（見下）。
- **登入紀錄的來源 IP**（0.9.117 起寫入，0.9.123 起顯示）：每筆稽核由 `AuditLogService` 經 `IClientIpProvider` 自動帶入 `AuditLog.ClientIp`（0.10.8 起登入、兩步驟驗證、登出都是 API 請求，取 `HttpContext`），呼叫端不傳。首頁「我的帳號」的「上一次登入」（`ProfileService.GetPreviousLoginAsync`：`Login.Success`／`Login.Sso.Success` 的倒數第二筆）在登入方式後面顯示 IP（有值才顯示）；個人資料頁「最近 20 筆登入紀錄」（`GetRecentLoginsAsync`：`Login.*` 與 `Logout`）欄位為「時間、動作、來源 IP、結果」，0.9.117 之前的舊紀錄顯示「—」。見[個人資料](個人資料-prd.md)、[首頁儀表板](首頁儀表板-prd.md)。
- **忘記密碼 → 重設**（`PasswordResetService`，0.9.60 起；Business 層、注入 `IDbContextFactory`）：
  1. `RequestAsync(identifier, 重設頁網址)`：先刪所有過期 token；以 `Account` 比對（與登入相同：0.9.171 起去頭尾空白、不分大小寫），對不到再以 `Email` 不分大小寫比對（可能多筆 → **每個帳號各寄一封**）。
  2. 逐帳號判斷資格：`support`、停用（`Status=false`）、沒有本地密碼（Google-only，`Password=""`）、Email 空白或無效 → 不寄；同帳號 `RequestCooldownSeconds`（預設 60 秒）內已申請過 → 不寄。原因寫進稽核 detail（`reason=NotFound|Support|Disabled|NoLocalPassword|InvalidEmail|Cooldown|QueueFull`），畫面不透露。`RequestAsync` 回傳 `PasswordResetRequestResult`：輸入內建帳號名稱回 `SupportAccountNotAllowed`（大小寫不同也先正規化成 `support`，稽核照記 `reason=Support`），其餘一律 `Accepted`。
  3. 符合資格：刪掉該帳號舊 token → 產生 32 bytes 亂數（Base64Url）→ **資料表 `PasswordResetToken` 只存 SHA-256** 與到期時間（`TokenLifetimeMinutes`，預設 30 分；0.9.98 起可在「系統參數」頁修改，`PasswordResetService` 以 `IOptionsMonitor` 即時讀取，只影響之後寄出的連結）→ 重設信交給 `IEmailQueue` 背景寄出（回應時間不因「有寄信」而變長）。
  4. 重設頁開啟：`ValidateTokenAsync` 只讀不寫，決定顯示表單或「連結無效」。
  5. `ResetAsync`：先驗密碼規則（0.9.101 起依 `PasswordPolicySettings`）與兩次一致，再查歷史（不可與最近 N 次相同，含目前的），**不過時不消耗 token**；再於交易內以 `ExecuteDelete` **搶占** token（刪到 1 列才繼續，並發送出兩次只有一次成功）→ `IPasswordPolicy.ApplyAsync`（雜湊、記設定時間、清旗標、解除鎖定、寫歷史）→ 刪除該帳號其餘 token → commit → 稽核 `Password.ResetCompleted` → 背景寄「密碼已變更」通知信。
  6. 信中連結的網址基準是 `EmailSettings:PublicBaseUrl`；留白時只有非 Production 會退回目前請求的網址（不信任 Host header）。

## 五、權限與安全

- 網頁與 App 都用 Token（0.10.8 起），以發出管道（`client_kind`：`web`／`api`）區分效期與規則；權限判定統一由 RBAC 表（`IPermissionChecker`）為單一權威，管理員短路一律通過。
- 待驗票證、記住裝置 Token、兩步驟驗證設定票證都以 Data Protection 加密（各自不同用途字串），到期時間在加密內容裡；**金鑰環遺失或換掉，這些全部失效**（使用者要重新輸入驗證碼、重新設定），正式環境要保存 `KeyPath`。
- 錯誤訊息不洩漏帳號是否存在（登入與忘記密碼皆然；忘記密碼的信一律走背景佇列，回應時間也一致）；輸出模型不含密碼、`Salt`、Token（`MyUserService.OtherDependencyData` 清空密碼欄位）。
- 帳號停用者的 Token 下一個請求就被拒（刷新 401、API 401），前端回登入頁；Google 自動建帳預設停用並導向待審核，須管理者啟用。
- 重設 token 只存 SHA-256、效期 30 分、單次使用；新申請與重設成功都作廢該帳號其餘 token。重設頁回應 `Referrer-Policy: no-referrer` 與 `Cache-Control: no-store`。
- 日誌不記錄忘記密碼輸入的帳號／Email、token 與連結；稽核的 `Password.ResetRequested`（NotFound）會記下輸入值（截 64 字），與 `Login.Failed` 記錄輸入帳號的作法一致。
- 密碼雜湊與儲存、記住我原理、Google 流程細節見交叉文件，本 PRD 不重述。

## 六、錯誤與邊界

- 驗證碼錯誤／欄位空白：停留登入頁並重新產生驗證碼。
- 連續輸錯 `MaxFailedAttempts` 次鎖定 `LockoutMinutes` 分鐘（預設 5 次、15 分鐘）；鎖定到期後可再次登入，失敗次數從 0 重新算；管理員可在使用者清單立即解鎖。
- 兩步驟驗證：驗證碼錯、備用碼用過、同一組驗證碼第二次使用 → 「驗證碼不正確，或帳號已被暫時鎖定。」並計入鎖定次數；待驗票證超過 5 分鐘或被竄改 → 回登入頁重新輸入密碼（0.10.8 起；之前是待驗證 Cookie）。
- Google 登入要自動建立新帳號，但帳號（Email）已是別的使用者的帳號名稱（0.9.171 起，不分大小寫）：含已刪除的使用者；不建立、不合併，導回 `/Auths/Login?sso=account-taken`，顯示「這個 Email 已經是其他使用者的帳號名稱，無法自動建立帳號。如果那就是你的帳號，請系統管理員在帳號資料補上這個 Email，再用 Google 登入即可連結。」；兩個回呼同時建立同一個帳號時，撞到唯一索引的那一個也照這個結果處理，稽核 `Login.Sso.Failed`（`reason=AccountTaken`）。
- 帳號被鎖定時用 Google 登入：導回 `/Auths/Login?sso=locked`，顯示「此帳號因連續輸錯密碼已暫時鎖定…」（Google 已驗證身分，明確告知無洩漏疑慮）。
- Google Callback 缺 `subject`／`email`：登出外部身分並導回登入頁。
- 已刪除的使用者（0.9.95 起）：密碼登入回與密碼錯誤相同的訊息（不透露帳號曾存在）；Google 登入導回 `/Auths/Login?sso=deleted`（Google 已驗證身分，告知原因無洩漏疑慮）。
- `support` 帳號於 `/ChangePassword` 一律被拒。Google 帳號只用於網頁登入，不支援 API（0.9.63 起移除「設定 API 密碼」；`/Profile` 自 0.9.102 起是與 API 無關的[個人資料](個人資料-prd.md)頁）。
- 使用者無角色、`RoleView` 為 null 或 `TabViewJson` 解析失敗：網頁 Token 回 403 `NO_PRIMARY_ROLE`，前端登出（0.10.8 起；M7 起 `/App` 在 WASM 時這個檢查有缺口，0.10.8 補上並補測試）。
- 忘記密碼：輸入空白 →「請輸入帳號或 Email」；驗證碼錯 → 重新產生驗證碼；查無帳號、不符資格、冷卻中 → 畫面與成功時相同；背景佇列滿 → 不寄（稽核 `QueueFull`）；資料庫錯誤 →「系統暫時無法處理您的申請，請稍後再試。」。
- 重設：連結過期、已用過、偽造、或申請後帳號被停用 → 一律「重設連結無效或已過期」；密碼規則不過 → 顯示原因、連結仍可再用。

### 6.1 已知限制（忘記密碼）

- **其他工作階段失效**（0.9.103 起）：重設密碼會換掉工作階段版本，其他裝置上已開著的頁面在下一個 API 請求被要求重新登入（同一台主機立即、其他主機最晚 5 分鐘），無法再 refresh。
- **限流**（0.10.8 起）：`password/forgot`、`password/reset/check`、`password/reset` 算進較嚴格的登入配額（每 IP 每分鐘 `RateLimit:LoginRequestsPerMinute`，預設 10）；之前 `/Auths/*` 是 Server 頁面、不在限流政策內。另一道防線是同帳號冷卻時間。驗證碼答案在頁面隱藏欄位，只是減速帶，擋不了腳本。
- **網頁登入**（`login/web`）**不算**嚴格登入配額（0.10.8 獨立審查）：照 Server 版靠帳號鎖定擋猜密碼，受一般 API 每 IP 配額；算進每 IP 10 次的話，同一個出口 IP 的公司早上登入尖峰會被擋。`login-settings` 也不算。App 的 `login`、`two-factor`、`google`、`session` 都算。
- token 在網址 query 裡：IIS 的 W3C 日誌、或開啟 `Microsoft.AspNetCore.Hosting.Diagnostics` 的 NLog 規則會記到完整網址。
- 0.9.167 起寄信先存進資料庫（內文加密）再寄，程式重新啟動不會掉信；暫時性失敗會重試。重設密碼信的結果可在「寄信紀錄」頁查詢，重試用完仍失敗時請使用者重新申請。
- Email 不分大小寫比對由 SQLite `lower()` 處理，只摺 ASCII。

## 七、驗收與測試

- `MyProject.Tests/MyUserServiceLoginTests.cs`：新舊雜湊登入、舊雜湊自動升級、密碼錯誤、5 次失敗鎖定並拒絕正確密碼、成功後歸零、鎖定到期放行、成功／失敗稽核。
- `MyProject.Tests/MyUserServicePasswordTests.cs`：`ChangeOwnPasswordAsync` 正確／錯誤舊密碼、空白新密碼、確認不一致、`support` 帳號被拒。
- `MyProject.Tests/SecurePasswordHasherTests.cs`：自述式雜湊、非決定性、新舊格式驗證與要求 rehash。
- `MyProject.Tests/AuthenticationStateHelperTests.cs`：未驗證導向登入頁且不清 Cookie、無效 Sid／查無使用者／停用／已刪除／無角色／壞 RoleJson 導向登出、旗標與到期導向改密碼頁（0.9.101 起；只有 123456 而沒有旗標不導向）、多角色聯集初始化。
- `MyProject.Tests/SessionTests.cs`（0.9.103；0.10.8 起改寫為 Token）：⭐ 換版本的時機（改密碼換、登入時舊雜湊升級不換、只改姓名或 Email 不換、停用／管理員／主要或額外角色變更換、刪除與強制登出換、support 設定密碼變更才換）、空版本登入時補上、快取存活與輪替立即清除、空字串一律不符；
  `SessionIntegrationTests`：換版本後與沒有版本的存取 Token 被拒、停用的帳號被拒、⭐ refresh token 不能當 Bearer、換版本後 refresh 401 並稽核。
- 0.10.8 起（WASM 遷移 M9）：`AccountSelfServiceApiTests`（改密碼、兩步驟驗證設定回新 Token 並撤銷舊串）、網頁登入整合測試（`login/web`、`two-factor`、`google/exchange` 一次性與瀏覽器綁定、`session/fork`、`password/*`、沒有主要角色 403、`login-settings`）、`SessionSettingsTests`（舊鍵 `CookieSettings` 相容、`IdleTimeoutSliding`）、`RefreshTokenStoreTests`（⭐ 重用撤銷整次登入、⭐ 登出後不能 fork）、`PerUserRateLimitTests`（⭐ 限流在驗證之後：已登入依人、網頁登入不算嚴格配額）、`Client/ClientSessionTests`（新分頁取得 Token、兩分頁同時刷新、storage 事件、⭐ 登入廣播讓同一瀏覽器只有一個身分）、登入頁元件測試。實機 `scripts/e2e/m09-auth.mjs`（M9-T1～T11）。
- `MyProject.Tests/PasswordPolicyTests.cs`（0.9.101）：規則邊界、歷史（目前、第 N、第 N+1、0 關閉、裁切、舊格式目前密碼）、必須變更（旗標、到期邊界、support 與 Google 豁免）、新增／修改使用者與變更密碼都套用原則、⭐ 雜湊只准出現在政策服務等三處、⭐ 鎖定門檻與到期先歸零、稽核標籤、同一則訊息、只通知一次、123456 登入補旗標、解鎖不換版本號、Google 被鎖定擋下、到期提醒作業。
- `MyProject.Tests/TotpServiceTests.cs`：TOTP 產碼／驗證（RFC 6238 測試向量）。
- `MyProject.Tests/TwoFactorTests.cs`（0.9.104）：登入兩段（⭐ 密碼對了不歸零、⭐ 重輸密碼也洗不掉驗證碼失敗次數、成功稽核帶方式、停用帳號不能完成第二步）、待驗票證與記住裝置 Token（0.10.8 起；之前是 Cookie）；`TwoFactorIntegrationTests`：API 三種情況、⭐ Google 登入已啟用的帳號要第二步。其餘見[兩步驟驗證 PRD](兩步驟驗證-prd.md)。
- `MyProject.Tests/PasswordResetServiceTests.cs`（0.9.60）：帳號比對、Email 不分大小寫、帳號優先、共用 Email 各寄一封、查無只稽核、五種不符資格、Google 有本地密碼允許、冷卻、新申請作廢舊連結、只存雜湊、過期、重設成功（新密碼可驗＋解鎖＋token 清空＋通知信）、同連結第二次失敗、規則不過不消耗 token、申請後停用、刪除使用者連帶刪 token（cascade）。
- `MyProject.Tests/EmailPagesIntegrationTests.cs`（0.9.60；0.10.8 起改測 API 與標頭）：寄信啟用與否反映在 `login-settings`、忘記密碼與重設 API、重設頁整頁回應帶 `no-referrer`／`no-store`。
- `MyProject.Tests/PageAuthorizationTests.cs`（0.10.8 起改寫）：⭐ 頁面不可有 `[Authorize]`、匿名白名單 9 頁（`/`、`/Error`、`/not-found` 與 6 個登入相關頁）、外框閘門、未登入的整頁請求只拿到空的 WASM 外殼。
- `MyProject.Tests/ProfileTests.cs`、`Components/DashboardTests.cs`（0.9.123）：登入紀錄帶出來源 IP（舊紀錄為 null）、「我的帳號」顯示上一次登入的 IP。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web.Client/Views/Auths/`：`Login.razor(.cs)`、`AuthShell.razor`（登入／兩步驟／忘記／重設共用外框）、`ForgotPassword`、`ResetPassword`、`TwoFactor`、`Pending.razor`、`Logout.razor`（0.10.8 起；Server 版 `Components/Auths/*` 已刪除）
- `src/MyProject/MyProject.Web.Client/Auth/`：`ClientSession.cs`（工作階段、新分頁取得 Token、跨分頁同步、登出）、`TokenStore.cs`、`LoginStorage.cs`（待驗票證、記住裝置 Token、Google 驗證值）、`AuthCaptcha.cs`、`ReturnUrlGuard.cs`、`wwwroot/js/client-auth.js`
- `src/MyProject/MyProject.Web/Controllers/WebAuthController.cs`、`AuthPasswordController.cs`、`TwoFactorSetupController.cs`（0.10.8 起）
- `src/MyProject/MyProject.Web/Auth/WebSessionTokens.cs`（效期規則、`SelfSessionRenewal`）、`SessionSettings.cs`、`TwoFactorLoginTickets.cs`（待驗票證、記住裝置 Token、設定票證）、`GoogleLoginCodes.cs`
- `src/MyProject/MyProject.Web.Client/Views/Profiles/ChangePasswordView.razor(.cs)`
- `src/MyProject/MyProject.Business/Services/Other/MyUserServiceLogin.cs`（鎖定、PBKDF2、升級、稽核）
- `src/MyProject/MyProject.Business/Services/Other/ExternalLoginService.cs`（Google 查找／建立）
- `src/MyProject/MyProject.Business/Services/Other/PasswordResetService.cs`（忘記密碼）、`src/MyProject/MyProject.AccessDatas/Models/PasswordResetToken.cs`、`src/MyProject/MyProject.Models/Systems/PasswordResetSettings.cs`
- `src/MyProject/MyProject.Web/Email/PublicBaseUrlResolver.cs`（信中連結的網址基準）
- `src/MyProject/MyProject.Business/Services/Other/AuthenticationStateHelper.cs`（登入後檢查與 RBAC 載入）
- `src/MyProject/MyProject.Web/Controllers/AuthController.cs`、`ExternalAuthController.cs`
- 交叉連結：[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[寄信服務](寄信服務-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)、[記住我登入原理說明](../security/記住我登入原理說明.md)
