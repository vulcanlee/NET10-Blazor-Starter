# 登入與帳號流程 PRD

- 文件版本：1.4
- 文件狀態：已實作
- 現行系統版本：0.9.61
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/09/24

## 一、目標與範圍

提供本專案的身分驗證與帳號自助維護能力，涵蓋本地帳密登入、Google OAuth2 第三方登入、登出、待審核導向、個人 API 密碼設定、變更密碼，以及忘記密碼／以 Email 連結重設密碼（0.9.60 起）。網頁採 Cookie 驗證、API 採 JWT Bearer，兩者共用同一份使用者與 RBAC 權威來源。

- **範圍**：`/Auths/Login`、`/Auths/Logout`、`/Auths/Pending`、`/Auths/ForgotPassword`、`/Auths/ResetPassword`、`/Profile`、`/ChangePassword`；Google 導向端點 `/Auths/Google/Login`、`/Auths/Google/Callback`；API 端 `/api/v1/auth/login`、`/refresh`、`/me`。帳號安全（PBKDF2 雜湊、登入失敗鎖定、登入時雜湊自動升級）。
- **非範圍**：帳號 CRUD 與角色／團隊指派見 [使用者管理](使用者管理-prd.md)；角色與權限矩陣見 [角色管理](角色管理-prd.md)。二階段驗證（TOTP）僅有資料模型與 `TotpService` 骨架，預設關閉、尚未提供強制啟用 UI，不在本次驗收範圍。

## 二、使用者與入口

| 路由 | 版面 | 所需權限 | 主要使用者 |
|------|------|----------|-----------|
| `/Auths/Login` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名 | 所有人 |
| `/Auths/Logout` | 無版面 | 已登入 | 所有登入者 |
| `/Auths/Pending` | `NoFooterLayout` | 匿名 | Google 自動建帳待審核者 |
| `/Auths/ForgotPassword` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名 | 忘記密碼者（0.9.60 起；寄信未啟用時只顯示「未啟用」）|
| `/Auths/ResetPassword?token=…` | `NoFooterLayout`（靜態 SSR 表單 POST）| 匿名＋有效 token | 收到重設信的人（0.9.60 起）|
| `/Profile` | 預設版面 | 已登入（`AuthenticationStateHelper.Check`）| 需設定 API 密碼者 |
| `/ChangePassword` | 預設版面 | 已登入 | 需變更密碼者 |
| `/api/v1/auth/login`、`/refresh` | — | 匿名（帳密換 JWT）| API 用戶端 |
| `/api/v1/auth/me` | — | JWT Bearer | API 用戶端 |

## 三、畫面與欄位

- **登入頁**：帳號、密碼、驗證碼（4 碼數字，前端產生存於隱藏欄位 `CaptchaCode`，以 `Ordinal` 比對）、「記住我」核取方塊。`GoogleOAuthSettings.IsConfigured` 為真時顯示「使用 Google 登入」按鈕，連結至 `/Auths/Google/Login`（帶 `returnUrl`）。前端逐項檢查空值與驗證碼，錯誤顯示「請輸入帳號／密碼／驗證碼」「驗證碼錯誤」。
- **登入頁（0.9.60 起）**：`EmailSettings:Provider` 不是 `None` 時，「記住我」右側的「企業級安全登入」改為「忘記密碼？」連結；從重設頁成功導回（`?reset=1`）時，表單上方顯示綠色「密碼已重設，請以新密碼登入。」。外觀由共用元件 `AuthShell` 提供，三頁一致。0.9.61 起開發環境（`appsettings.Development.json`）預設 `Provider=Pickup`，所以本機看得到連結；Production 出貨為 `None`，看不到。
- **忘記密碼頁**：帳號或 Email、驗證碼（與登入頁同一套 4 碼）。送出後**一律**顯示「如果帳號存在且登記了有效的 Email，重設密碼的信已經寄出，請在 N 分鐘內點信中的連結…」—— 不論帳號是否存在、有沒有真的寄出。寄信未啟用時整頁只顯示「系統目前未啟用寄信功能…請聯絡系統管理員」。
- **重設密碼頁**：副標題顯示「為帳號「X」設定新密碼（至少 6 個字元）」；新密碼、確認新密碼。token 無效／過期／已用過一律顯示「重設連結無效或已過期，請重新申請。」並提供「重新申請重設信」連結。成功後導回登入頁。
- **待審核頁**：靜態說明，告知 Google 帳號已建立但預設停用，須管理者啟用後再登入，提供返回登入頁連結。
- **個人資料（設定 API 密碼）**：新密碼、確認新密碼；已有本地密碼者另需「目前密碼」。設定後可用「帳號（Email）＋此密碼」呼叫 `/api/v1/auth/login` 取得 JWT。`support` 開發帳號被禁止。
- **變更密碼**：目前密碼、新密碼、確認新密碼（`[Compare]` 驗證一致）。`support` 開發帳號被禁止。

## 四、內部系統運作

- **本地登入**（`MyUserServiceLogin.LoginAsync`，View → Service → `BackendDBContext`）：以 `Account` 查 `MyUser`；帳號不存在或密碼錯誤一律回「帳號或者密碼不正確」（不區分，避免帳號枚舉）。密碼以 `SecurePasswordHasher.VerifyPassword`（PBKDF2）驗證。
- **帳號鎖定**：失敗時 `AccessFailedCount++`，達 `MaxFailedAccessAttempts=5` 設 `LockoutEndUtc = UtcNow + 15 分`；鎖定期間回「帳號已鎖定，請稍後再試。」成功登入後將 `AccessFailedCount` 歸零、`LockoutEndUtc` 清空。
- **雜湊自動升級**：驗證回傳 `SuccessRehashNeeded`（舊格式）時，即時以 PBKDF2 重新雜湊並存回。
- **Cookie 簽發**（Login.razor.cs）：建立 `ClaimTypes.Role=User`、`Name`、`NameIdentifier=Account`、`Sid=Id`，以 `CookieAuthenticationScheme` `SignInAsync`；`IsPersistent = RememberMe`（記住我 → 持久性 Cookie），`RedirectUri` 取 `ReturnUrl` 或 `/App`。
- **Google 登入**（`ExternalAuthController` + `ExternalLoginService.FindOrCreateAsync`）：Callback 驗證 `ExternalCookieScheme` 後，依序「GoogleId 比對 → Email 連結既有帳號 → 自動建立停用新帳號」（`Status=false`、`IsAdmin=false`、`Password=""`、`Salt=null`、指派預設角色）。`!Status` 導向 `/Auths/Pending`，否則簽發 Cookie 並導回本地安全的 `returnUrl`。
- **登出**：`SignOutAsync(CookieScheme)` 後 `NavigateTo("/Auths/Login", forceLoad: true)`。
  - **使用者主動登出**（0.9.29 起）先經二次確認：三個 UI 入口都走
    `Components/Commons/LogoutConfirm.cs`，確認後才導向 `/Auths/Logout`。
  - ⚠️ **系統強制登出不確認**：`AuthenticationStateHelper` 的六處
    `NavigateTo("/Auths/Logout", true, true)`（未驗證／無效 Sid／查無使用者／停用／無角色／壞 RoleJson）
    是系統行為，直接登出 —— 對 session 已經失效的人跳「確定要登出嗎？」只會讓他卡住。
    這兩條路徑在分層上就分開：`LogoutConfirm` 在 Web 層，`AuthenticationStateHelper` 在 Business 層，
    後者參照不到前者。
  - 直接在網址列輸入 `/Auths/Logout` 維持立即登出，刻意不擋（刻意輸入網址不是誤觸）。
- **登入後狀態**（`AuthenticationStateHelper.Check`）：驗證已登入、`Sid` 有效、使用者存在且 `Status` 啟用、具角色；`NeedChangePasswordAsync`（密碼等於 `123456`）為真且不在改密碼頁時強制導向 `/ChangePassword`。載入 `CurrentUser`，`RoleList` 以 `IPermissionChecker.GetEffectivePermissionKeysAsync`（RBAC 多角色聯集）為權威、`TeamList` 由 `EffectiveTeamResolver` 決定。
- **API 登入**（`AuthController`）：`login` 以帳密換 `TokenResponseDto`（JWT + Refresh），`refresh` 換新 Token，`me` 回目前使用者；一律包 `ApiResult<T>`，失敗回 401。
- **稽核**：登入寫入 `Login.Success` / `Login.Failed` / `Login.LockedOut`（`AuditLog`）；忘記密碼寫入 `Password.ResetRequested` / `Password.ResetCompleted` / `Password.ResetFailed`（見下）。
- **忘記密碼 → 重設**（`PasswordResetService`，0.9.60 起；Business 層、注入 `IDbContextFactory`）：
  1. `RequestAsync(identifier, 重設頁網址)`：先刪所有過期 token；以 `Account` 精確比對（與登入相同），對不到再以 `Email` 不分大小寫比對（可能多筆 → **每個帳號各寄一封**）。
  2. 逐帳號判斷資格：`support`、停用（`Status=false`）、沒有本地密碼（Google-only，`Password=""`）、Email 空白或無效 → 不寄；同帳號 `RequestCooldownSeconds`（預設 60 秒）內已申請過 → 不寄。原因寫進稽核 detail（`reason=NotFound|Support|Disabled|NoLocalPassword|InvalidEmail|Cooldown|QueueFull`），畫面不透露。
  3. 符合資格：刪掉該帳號舊 token → 產生 32 bytes 亂數（Base64Url）→ **資料表 `PasswordResetToken` 只存 SHA-256** 與到期時間（`TokenLifetimeMinutes`，預設 30 分）→ 重設信交給 `IEmailQueue` 背景寄出（回應時間不因「有寄信」而變長）。
  4. 重設頁開啟：`ValidateTokenAsync` 只讀不寫，決定顯示表單或「連結無效」。
  5. `ResetAsync`：先驗密碼規則（≥ 6 字元、兩次一致、≠ `123456`），**不過時不消耗 token**；再於交易內以 `ExecuteDelete` **搶占** token（刪到 1 列才繼續，並發送出兩次只有一次成功）→ PBKDF2 雜湊新密碼、`AccessFailedCount=0`、`LockoutEndUtc=null`（解除鎖定）→ 刪除該帳號其餘 token → commit → 稽核 `Password.ResetCompleted` → 背景寄「密碼已變更」通知信。
  6. 信中連結的網址基準是 `EmailSettings:PublicBaseUrl`；留白時只有非 Production 會退回目前請求的網址（不信任 Host header）。

## 五、權限與安全

- 網頁 Cookie、API JWT 各自獨立；權限判定統一由 RBAC 表（`IPermissionChecker`）為單一權威，管理員短路一律通過。
- 錯誤訊息不洩漏帳號是否存在（登入與忘記密碼皆然；忘記密碼的信一律走背景佇列，回應時間也一致）；輸出模型不含密碼、`Salt`、Token（`MyUserService.OtherDependencyData` 清空密碼欄位）。
- 帳號停用者於 `Check` 一律導回登出；Google 自動建帳預設停用並導向待審核，須管理者啟用。
- 重設 token 只存 SHA-256、效期 30 分、單次使用；新申請與重設成功都作廢該帳號其餘 token。重設頁回應 `Referrer-Policy: no-referrer` 與 `Cache-Control: no-store`。
- 日誌不記錄忘記密碼輸入的帳號／Email、token 與連結；稽核的 `Password.ResetRequested`（NotFound）會記下輸入值（截 64 字），與 `Login.Failed` 記錄輸入帳號的作法一致。
- 密碼雜湊與儲存、記住我原理、Google 流程細節見交叉文件，本 PRD 不重述。

## 六、錯誤與邊界

- 驗證碼錯誤／欄位空白：停留登入頁並重新產生驗證碼。
- 連續 5 次失敗鎖定 15 分鐘；鎖定到期後（`LockoutEndUtc` 過期）可再次登入。
- Google Callback 缺 `subject`／`email`：登出外部身分並導回登入頁。
- `support` 帳號於 `/Profile`、`/ChangePassword` 一律被拒；Google 帳號首次設 API 密碼免驗舊密碼。
- 使用者無角色、`RoleView` 為 null 或 `TabViewJson` 解析失敗：導向登出。
- 忘記密碼：輸入空白 →「請輸入帳號或 Email」；驗證碼錯 → 重新產生驗證碼；查無帳號、不符資格、冷卻中 → 畫面與成功時相同；背景佇列滿 → 不寄（稽核 `QueueFull`）；資料庫錯誤 →「系統暫時無法處理您的申請，請稍後再試。」。
- 重設：連結過期、已用過、偽造、或申請後帳號被停用 → 一律「重設連結無效或已過期」；密碼規則不過 → 顯示原因、連結仍可再用。

### 6.1 已知限制（忘記密碼）

- **既有工作階段不會失效**：重設後，其他裝置上已登入的 Cookie（最長「記住我」30 天）與 JWT 仍然有效 —— 系統沒有 security stamp（與「Refresh Token 落庫撤銷刻意不做」的決議一致）。
- **沒有依 IP 限流**：`/Auths/*` 不在 `api` 限流政策內；防線是同帳號冷卻時間。驗證碼答案在頁面隱藏欄位，只是減速帶，擋不了腳本。
- token 在網址 query 裡：IIS 的 W3C 日誌、或開啟 `Microsoft.AspNetCore.Hosting.Diagnostics` 的 NLog 規則會記到完整網址。
- 背景佇列只在記憶體：程式重啟時未寄出的信會遺失，使用者重新申請即可。
- Email 不分大小寫比對由 SQLite `lower()` 處理，只摺 ASCII。

## 七、驗收與測試

- `MyProject.Tests/MyUserServiceLoginTests.cs`：新舊雜湊登入、舊雜湊自動升級、密碼錯誤、5 次失敗鎖定並拒絕正確密碼、成功後歸零、鎖定到期放行、成功／失敗稽核。
- `MyProject.Tests/MyUserServicePasswordTests.cs`：`ChangeOwnPasswordAsync` 正確／錯誤舊密碼、空白新密碼、確認不一致、`support` 帳號被拒。
- `MyProject.Tests/SecurePasswordHasherTests.cs`：自述式雜湊、非決定性、新舊格式驗證與要求 rehash。
- `MyProject.Tests/AuthenticationStateHelperTests.cs`：未驗證／無效 Sid／查無使用者／停用／無角色／壞 RoleJson 導向登出、需改密碼導向、多角色聯集初始化。
- `MyProject.Tests/TotpServiceTests.cs`：TOTP 產碼／驗證（骨架，預設關閉）。
- `MyProject.Tests/PasswordResetServiceTests.cs`（0.9.60）：帳號比對、Email 不分大小寫、帳號優先、共用 Email 各寄一封、查無只稽核、五種不符資格、Google 有本地密碼允許、冷卻、新申請作廢舊連結、只存雜湊、過期、重設成功（新密碼可驗＋解鎖＋token 清空＋通知信）、同連結第二次失敗、規則不過不消耗 token、申請後停用、刪除使用者連帶刪 token（cascade）。
- `MyProject.Tests/EmailPagesIntegrationTests.cs`（0.9.60）：Pickup 時登入頁有連結、忘記頁有表單、壞 token 重設頁回 `no-referrer`／`no-store` 且不給表單；None 時沒有連結、兩頁顯示未啟用。
- `MyProject.Tests/PageAuthorizationTests.cs`：匿名白名單含 `ForgotPassword`、`ResetPassword`。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Components/Auths/Login.razor`、`Login.razor.cs`（登入表單與 Cookie 簽發）
- `src/MyProject/MyProject.Web/Components/Auths/AuthShell.razor`（登入／忘記／重設三頁共用外框）、`ForgotPassword.razor(.cs)`、`ResetPassword.razor(.cs)`、`AuthCaptcha.cs`
- `src/MyProject/MyProject.Web/Components/Auths/Logout.razor.cs`、`Pending.razor`
- `src/MyProject/MyProject.Web/Components/Pages/Profile.razor`、`ChangePassword.razor`
- `src/MyProject/MyProject.Business/Services/Other/MyUserServiceLogin.cs`（鎖定、PBKDF2、升級、稽核）
- `src/MyProject/MyProject.Business/Services/Other/ExternalLoginService.cs`（Google 查找／建立）
- `src/MyProject/MyProject.Business/Services/Other/PasswordResetService.cs`（忘記密碼）、`src/MyProject/MyProject.AccessDatas/Models/PasswordResetToken.cs`、`src/MyProject/MyProject.Models/Systems/PasswordResetSettings.cs`
- `src/MyProject/MyProject.Web/Email/PublicBaseUrlResolver.cs`（信中連結的網址基準）
- `src/MyProject/MyProject.Business/Services/Other/AuthenticationStateHelper.cs`（登入後檢查與 RBAC 載入）
- `src/MyProject/MyProject.Web/Controllers/AuthController.cs`、`ExternalAuthController.cs`
- 交叉連結：[使用者管理](使用者管理-prd.md)、[角色管理](角色管理-prd.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[寄信服務](寄信服務-prd.md)
- 安全機制：[認證授權與權限機制](../security/認證授權與權限機制.md)、[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)、[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)、[記住我登入原理說明](../security/記住我登入原理說明.md)、[權限授權現況評估與改善路線](../security/權限授權現況評估與改善路線.md)
