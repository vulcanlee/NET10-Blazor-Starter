# Google OAuth2 第三方登入

- 文件版本：1.9
- 文件狀態：已實作
- 現行系統版本：0.10.10
- 首次實作版本：0.2.6
- 最後核對日期：2026/11/13

## 目的

本文件說明如何在本系統啟用「使用 Google 帳號登入」，並串接既有的權限控管（權控）與 Web API（JWT）機制。

設計核心：**Google OAuth2 只負責「網頁登入」這條路徑**。0.10.8（WASM 遷移 M9）起網頁改成純 Token 登入，Google 回呼不再發登入 Cookie，而是產生一次性代碼交回登入頁，由登入頁換成與帳密登入相同的網頁 Token；App 的 API 仍維持「帳號＋密碼換 JWT」（`POST api/v1/Auth/login`）。Google 帳號只用於網頁登入，**不支援 App 的 API 登入**（0.9.63 起移除「設定 API 密碼」）；要從 App 呼叫 API 請使用本地帳號。詳見「認證授權與權限機制」一文。

---

## 行為總覽（定案規則）

| 項目 | 行為 |
|------|------|
| 帳號開通 | 首次以 Google 登入時，**自動建立**本地使用者，以 Google 的 Email 作為帳號名稱（`Account`） |
| 網域限制 | **不限制**，任何 Google 帳號皆可進行驗證 |
| 預設角色 | 自動建立的帳號套用設定中的 `DefaultRoleName`（預設「預設角色」） |
| 資安閘門 | 新帳號預設 `Status = false`（**停用**），需由管理者於「使用者管理」**啟用並確認角色**後才能登入 |
| Email 連結 | 若已存在相同 Email 的本地帳號，**自動連結**（寫入 GoogleId，不重建、不覆寫狀態與權限） |
| 網頁登入 | 純 Google 單一登入（SSO），**不需設定密碼**；0.10.8 起回呼以一次性代碼交回登入頁換 Token，沒有「記住我」（關掉分頁就登出） |
| App 的 API 登入 | **不支援**（0.9.63 起）：Google 帳號沒有本地密碼，無法呼叫 `/api/v1/auth/login`；要從 App 呼叫 API 請使用本地帳號 |

---

## 一、在 Google Cloud Console 建立 OAuth 用戶端

1. 進入 [Google Cloud Console](https://console.cloud.google.com/) 並建立（或選擇）一個專案。
2. 設定「OAuth 同意畫面」（OAuth consent screen）：
   - 使用者類型：內部（限 Google Workspace 組織）或外部皆可。
   - 填寫應用程式名稱、支援電子郵件等必填欄位。
3. 前往「憑證」（Credentials）→「建立憑證」→「OAuth 用戶端 ID」：
   - 應用程式類型：**Web application（網頁應用程式）**。
   - **授權重新導向 URI（Authorized redirect URIs）** 必須填入本系統的回呼路徑（網址結尾固定為 `/signin-google`）：
     - 開發（https）：`https://localhost:7144/signin-google`
     - 開發（http）：`http://localhost:5109/signin-google`
     - 正式：`https://<your-domain>/signin-google`
     - 正式且前端另外部署成獨立站台（雙站台，0.10.10 起）：填 **API 主機**的網址，例如 `https://api.公司.com/signin-google`（回呼在 API 主機，不是前端的 `app.公司.com`）。
   - `/signin-google` 是 Google 中介軟體預設的回呼路徑（對應 `CallbackPath`），請務必與系統設定**完全一致**（差一個字 Google 就會拒絕）。本機開發埠以 `Properties/launchSettings.json` 為準；用 `scripts/New-StarterProject.ps1` 產生的衍生專案會拿到另一組隨機埠，腳本結尾會印出要註冊的 redirect URI。
4. 建立完成後，畫面會顯示 **用戶端 ID（ClientId）** 與 **用戶端密鑰（ClientSecret）**，複製下來填入下一節的設定。

---

## 二、系統設定

設定區段位於 `appsettings.json` 的 `GoogleOAuthSettings`，並綁定到強型別 `MyProject.Web.Auth.GoogleOAuthSettings`：

```json
"GoogleOAuthSettings": {
  "Enabled": false,
  "ClientId": "",
  "ClientSecret": "",
  "DefaultRoleName": "預設角色"
}
```

| 欄位 | 該填什麼 | 範例 |
|------|----------|------|
| `Enabled` | 是否啟用 Google 登入。要用就設 `true`；`false` 時不註冊 Google 驗證，登入頁也不顯示按鈕 | `true` |
| `ClientId` | Google Cloud Console 產生的「用戶端 ID」 | `123456789-abc123.apps.googleusercontent.com` |
| `ClientSecret` | Google Cloud Console 產生的「用戶端密鑰」 | `GOCSPX-xxxxxxxxxxxxxxxxxxxx` |
| `DefaultRoleName` | 新帳號自動套用的角色名稱，**保持 `預設角色` 即可**（對應系統 seeding 出來的角色） | `預設角色` |

> ⚠️ **`ClientId` 與 `ClientSecret` 不是自己輸入任意字串，必須先到 Google Cloud Console 申請**（見上方「一、在 Google Cloud Console 建立 OAuth 用戶端」）。未申請或填錯，Google 會在登入時拒絕。

啟用步驟：將 `Enabled` 設為 `true`，並填入向 Google 申請到的 `ClientId` 與 `ClientSecret`。`DefaultRoleName` 一般維持 `預設角色`。

### 填入方式（擇一）

**方式 A：user-secrets（開發環境建議）**

`ClientSecret` 屬機密，**不應寫入版本控管的 `appsettings.json`**。專案已設定 `UserSecretsId`，可直接於 `MyProject.Web` 目錄執行：

```powershell
cd src\MyProject\MyProject.Web
dotnet user-secrets set "GoogleOAuthSettings:Enabled" "true"
dotnet user-secrets set "GoogleOAuthSettings:ClientId" "你的用戶端ID"
dotnet user-secrets set "GoogleOAuthSettings:ClientSecret" "你的用戶端密鑰"
```

`appsettings.json` 內的該區段則維持留空當預設值即可。

**方式 B：環境變數（正式環境建議）**

鍵名以雙底線分隔：

```
GoogleOAuthSettings__Enabled=true
GoogleOAuthSettings__ClientId=你的用戶端ID
GoogleOAuthSettings__ClientSecret=你的用戶端密鑰
```

**方式 C：直接寫入 `appsettings.json`（僅限本機快速測試）**

```json
"GoogleOAuthSettings": {
  "Enabled": true,
  "ClientId": "123456789-abc123.apps.googleusercontent.com",
  "ClientSecret": "GOCSPX-xxxxxxxxxxxxxxxxxxxx",
  "DefaultRoleName": "預設角色"
}
```

> ⚠️ 方式 C 會讓密鑰進版控，**正式上線前務必改用方式 A / B，並把密鑰從 `appsettings.json` 移除**。

只有在 `Enabled = true` 且 `ClientId`、`ClientSecret` 皆非空（`GoogleOAuthSettings.IsConfigured`）時，系統才會在 `Program.cs` 註冊 Google 驗證與外部暫存 Cookie；填好後重啟，登入頁就會出現「使用 Google 登入」按鈕（登入頁經 `GET api/v1/Auth/login-settings` 的 `GoogleEnabled` 判斷）。

---

## 三、驗證流程

### 元件與 scheme

- **外部暫存 Cookie scheme**：`MagicObjectHelper.ExternalCookieScheme`（`.MyProject.External`，OAuth 流程中暫存 Google 身分，效期 5 分鐘，回呼處理完就清掉）。0.10.8 起這是整個系統唯一的 Cookie，不是登入憑證。
- **Google scheme**：`GoogleDefaults.AuthenticationScheme`，`SignInScheme` 指向外部暫存 Cookie，`CallbackPath = /signin-google`。
- **一次性代碼**：`GoogleLoginCodes`（`MyProject.Web/Auth/GoogleLoginCodes.cs`，0.10.8 起）：回呼確認身分後產生代碼，前端以代碼換 Token。
- 0.10.7 以前回呼直接以主 Cookie scheme（`MagicObjectHelper.CookieScheme`）完成登入；0.10.8 起登入 Cookie 已移除，改由下方流程第 8～9 步發 Token。

### 端點

| 路由 | 說明 |
|------|------|
| `GET /Auths/Google/Login?returnUrl=&binding=` | `ExternalAuthController`（一般 MVC 控制器）：觸發 Google OAuth Challenge，回呼導向 Callback。0.10.8 起 `binding`（瀏覽器驗證值的雜湊）必填，沒帶回登入頁 |
| `GET /signin-google` | Google 中介軟體內建的回呼，登入到外部暫存 Cookie（不需自行撰寫） |
| `GET /Auths/Google/Callback?returnUrl=` | Google 驗證完成後的處理端點：查找／連結／建立帳號，依狀態分流 |
| `POST /api/v1/Auth/google/exchange` | `WebAuthController`（0.10.8 起）：一次性代碼＋瀏覽器驗證值換 Token（或待驗票證）。算進較嚴格的登入配額 |

### 流程步驟

1. 使用者開啟 `/Auths/Login`（WASM）：登入頁在這個分頁的 sessionStorage 產生一個隨機**驗證值**，把它的 SHA-256（Base64Url）放進「使用 Google 登入」按鈕的連結：`/Auths/Google/Login?returnUrl=…&binding=<雜湊>`。0.10.10 起這個連結由 `ApiAddress.GoogleLogin` 組成，是 **API 主機**的完整網址（同站台時與網頁同一個網址；雙站台時是前端 `wwwroot/appsettings.json` 的 `ApiBaseUrl`），按下後整頁導到 API 主機。
2. 系統發出 `Challenge`（綁定雜湊放進 OAuth 的 state，加密、防竄改），瀏覽器導向 Google 完成驗證，Google 再導回 `/signin-google`。
3. Google 中介軟體把身分寫入外部暫存 Cookie，並導向 `/Auths/Google/Callback`。
4. Callback 讀取外部 Cookie 的 Email、Name、`sub` 與 state 裡的綁定雜湊（沒有綁定值 → 回登入頁重新開始，不建立也不連結帳號），呼叫 `ExternalLoginService.FindOrCreateAsync(...)`：
   - 先以 **GoogleId** 比對既有連結；
   - GoogleId 屬於**已刪除**的使用者 → 拒絕（0.9.95 起）；
   - 否則以 **Email** 連結既有本地帳號（寫入 GoogleId，不改狀態與權限）；
   - Email 屬於**已刪除**的使用者 → 拒絕（0.9.95 起）；
   - 都沒有則 **自動建立**新帳號（`Status = false`、套用預設角色、無密碼）。
     0.9.171 起建立前先檢查帳號（Email）是否已被別的使用者當成帳號名稱（不分大小寫，含已刪除的）：被占用就拒絕、不合併，
     導向 `/Auths/Login?sso=account-taken`，顯示「這個 Email 已經是其他使用者的帳號名稱，無法自動建立帳號。如果那就是你的帳號，請系統管理員在帳號資料補上這個 Email，再用 Google 登入即可連結。」；
     兩個回呼同時建立同一個帳號時，撞到唯一索引的那一個也照這個結果處理。
   - 被拒絕時不寫入、不連結、不新建，導向 `/Auths/Login?sso=deleted`，登入頁顯示「此帳號已被刪除，請洽系統管理員。」；管理員在使用者管理「顯示已刪除」還原後即可再登入。
5. 清除外部暫存 Cookie。
6. 若 `Status = false` → 導向 `/Auths/Pending`（帳號待審核頁，**不**登入）。
7. 若帳號因密碼輸錯被鎖定（0.9.101 起）→ 導向 `/Auths/Login?sso=locked`，不登入。判斷順序集中在 `ExternalLoginResult.Evaluate`：帳號已被使用（0.9.171 起）→ 已刪除 → 待開通 → 鎖定 → 登入。
8. 否則產生**一次性代碼**（記下使用者 Id、工作階段版本、`returnUrl`、是否要第二步、綁定雜湊），導回 `/Auths/Login#code=<代碼>`。代碼放在網址片段（`#` 後面），不會送到伺服器、不進日誌。
   0.10.10 起第 4、6、7、8 步所有導回前端的網址都經 `ClientSettings.FrontendUrl` 組成：雙站台時接在 API 主機的 `ClientSettings:BaseUrl`（前端網址）後面，例如 `https://app.公司.com/Auths/Login#code=…`；同站台時是本站路徑。只用設定值，不採用請求的 Host 或任何參數（不可成為開放式重新導向）。
9. 登入頁讀出代碼、立刻從網址拿掉，連同第 1 步的驗證值原值與記住裝置 Token 送 `POST api/v1/Auth/google/exchange`：
   - 代碼不存在、過期（60 秒）、已用過、驗證值不符，或回呼之後帳號被停用、改了工作階段版本 → 401「Google 登入已逾時，請重新登入。」，留在登入頁；
   - 帳號已啟用兩步驟驗證（0.9.104 起）且這台裝置沒有被記住 → 回待驗票證，前端帶到 `/Auths/TwoFactor` 完成第二步；
   - 否則發一組網頁 Token（Google 登入**沒有「記住我」**，Token 存在 sessionStorage，關掉分頁就登出；開新分頁會向已開分頁要），稽核 `Login.SsoSuccess`，導向 `returnUrl`（預設 `/App`）。

```
使用者 ──點 Google 登入（帶驗證值雜湊）──▶ /Auths/Google/Login ──Challenge──▶ Google
                                                                              │
                                                                              ▼
                                                           /signin-google（外部暫存 Cookie）
                                                                              │
   /Auths/Pending ◀── 停用待審 ───┐                                             │
                                 └──── /Auths/Google/Callback ◀──────────────┘
                                       （查找/連結/建立帳號 → 依 Status 分流）
                                                  │ 可登入：產生一次性代碼
                                                  ▼
   /App ◀── Token ── POST api/v1/Auth/google/exchange ◀── /Auths/Login#code=…（代碼＋驗證值原值）
```

---

## 四、管理者審核（啟用帳號）

1. 以管理者帳號（預設 `support`）登入。
2. 進入「使用者管理」（`/myusers`）。
3. 找到自動建立的 Google 使用者（帳號為其 Email），編輯：
   - 勾選 **啟用**（`Status`）。
   - 指派 **角色**（`RoleView`），確認權限符合需求。
   - 密碼欄位可留白（Google 帳號不需密碼即可網頁登入）。
4. 儲存後，該使用者即可再次以 Google 登入並進入系統，選單依角色權限顯示。

---

## 五、Google 使用者呼叫 API（0.9.63 起不支援）

Google 使用者預設沒有本地密碼，因此無法用 App 的 `/api/v1/auth/login` 取得 JWT（網頁登入後網頁本身呼叫 API 用的 Token 不受影響）。
0.9.63 之前可從使用者選單的「設定 API 密碼」（`/Profile`）自行設定一組密碼，此功能已移除（0.9.102 起 `/Profile` 是與 API 無關的個人資料頁）；
需要呼叫 API 請改用本地帳號。

---

## 六、資料模型異動

`MyUser` 新增兩個可空欄位（對應 EF 遷移 `AddGoogleOAuth`）：

| 欄位 | 型別 | 說明 |
|------|------|------|
| `OAuthProvider` | `string?` | 外部驗證提供者；本地帳號為 `null`，Google 帳號為 `"Google"` |
| `GoogleId` | `string?` | Google 的使用者唯一識別碼（`sub`） |

- **SQLite**：已產生遷移 `AddGoogleOAuth`，啟動時自動套用。
> 0.4.24 起本專案只支援 SQLite，原「SQL Server 需另行處理 schema」的說明已不再適用。

> 「必須變更密碼」（`MustChangePassword` 旗標或密碼到期，0.9.101 起）對沒有本地密碼的 Google 帳號一律不觸發（`IPasswordPolicy` 豁免），否則會永遠卡在變更密碼頁。
> 0.9.101 起帳號因密碼輸錯被鎖定時，Google 登入也會被擋下，導回登入頁顯示「此帳號因連續輸錯密碼已暫時鎖定…」。

---

## 七、安全考量與限制

- **開放註冊風險**：目前為「任何 Google 帳號皆可驗證並自動建帳」。資安閘門是 `Status = false` 預設停用 + 管理者人工審核，請務必落實審核流程。若要更嚴格，可改為限制特定網域或關閉自動建帳。
- 自動建立的帳號雖套用「預設角色」，但在管理者啟用前無法登入。
- `ClientSecret` 請勿進版控；正式環境一律用 user-secrets / 環境變數。
- 回呼 URI（`/signin-google`）必須與 Google Console 設定完全一致，否則 Google 會拒絕。0.10.8 改成純 Token 登入時回呼網址**沒有變**，不必改 Google Console。0.10.10 起改成雙站台部署時，回呼網址要改成 API 主機的網域（同站台不變）。
- **一次性代碼**（0.10.8 起）：只存代碼的雜湊、60 秒、只能兌換一次（兌換時先移除再檢查，出示錯的驗證值也算用掉）；並綁定發起的瀏覽器 —— 代碼經瀏覽器歷史或側錄外流，沒有那個分頁裡的驗證值原值也換不到 Token。
- ⚠️ 代碼只存在**單一行程的記憶體**：回呼與兌換要落在同一台主機（IIS 單站台即是；雙站台時回呼與兌換都在 API 主機，同樣成立）；多台主機要設定黏著工作階段，否則兌換會失敗、要重新按 Google 登入。IIS 回收時還沒兌換的代碼失效。
- 外部暫存 Cookie 與 OAuth state 以 Data Protection 金鑰加密：金鑰環要固定（`SystemSettings:ExternalFileSystem:DataProtectionKeyPath`），否則 Google 驗證到一半回收就會失敗。
