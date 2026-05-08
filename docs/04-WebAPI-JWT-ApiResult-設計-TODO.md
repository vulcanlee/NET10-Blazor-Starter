# Web API、JWT 與 ApiResult 設計 TODO

## 目標說明

- [ ] 將本專案補強為可同時支援 Blazor Web UI 與外部 Web API 的通用腳手架。
- [ ] 所有 Web API 固定使用泛型 `ApiResult<T>` 作為統一回傳格式。
- [ ] Web UI 維持 Cookie 認證，Web API 使用 JWT Bearer 認證。
- [ ] JWT 運作參數由 `appsettings.json` 取得，並支援正式環境用環境變數或 Secret Manager 覆蓋。

## 現況盤點

- [ ] 目前 Cookie 登入已存在，使用 `MagicObjectHelper.CookieScheme`。
- [ ] 目前 JWT 尚未實作，`Program.cs` 只有「Cookie & JWT」註解區塊。
- [ ] 目前 Swagger UI 已存在，但尚未設定 Bearer JWT 授權。
- [ ] 目前 API controller 主要是檔案下載與 Weather 範例。
- [ ] 目前沒有統一 API 回傳型別，Business 層使用 `VerifyRecordResult`，Web API 尚未使用 `ApiResult<T>`。
- [ ] 目前 API 例外尚未統一封裝成標準回應格式。

## 實作待辦

- [ ] 建立 `ApiResult<T>` 類別，支援 `Success`、`Message`、`Data`、`Errors`、`TraceId`、`StatusCode`、`Exception`。
- [ ] 建立 `ApiExceptionInfo` 類別，用於封裝例外型別、訊息、StackTrace、InnerException 與來源資訊。
- [ ] 建立 `ApiResult` factory 或 extension methods，統一產生成功、驗證失敗、未授權、無權限、找不到、例外結果。
- [ ] API 維持正確 HTTP 狀態碼，Body 一律包成 `ApiResult<T>`。
- [ ] API 發生例外時完整封裝到 `ApiResult<T>.Exception`。
- [ ] 檔案下載成功時維持原生 `File` stream；下載失敗、無權限、找不到檔案時才回 `ApiResult<T>`。
- [ ] 建立 `JwtSettings` 強型別設定，欄位包含 `Issuer`、`Audience`、`SigningKey`、`AccessTokenMinutes`、`RefreshTokenDays`、`ClockSkewMinutes`。
- [ ] 在 `appsettings.json` 加入 `JwtSettings` 範例值，正式密鑰不得使用範例值。
- [ ] 在 `Program.cs` 或 authentication extension 中加入 `AddJwtBearer`。
- [ ] 同時保留 Cookie 與 JWT authentication scheme。
- [ ] 建立 API login endpoint，驗證帳密後回傳 access token 與 refresh token。
- [ ] 建立無狀態 signed refresh JWT，v1 不落庫。
- [ ] 建立 refresh endpoint，驗證 refresh JWT 後發新 access token。
- [ ] 文件標示無狀態 refresh JWT 無法可靠撤銷、無法裝置登出、無法偵測重放。
- [ ] 建立 `/api/v1/auth/me`，回傳目前 JWT 使用者資訊。
- [ ] 建立 CRUD API 樣板，示範查詢、單筆、新增、修改、刪除與驗證錯誤。
- [ ] Swagger 設定 Bearer security definition，讓使用者可在 Swagger UI 輸入 JWT。
- [ ] 建立 API version 前綴慣例，例如 `/api/v1/...`。
- [ ] 建立 Controller 例外處理篩選器或 middleware，將未捕捉例外轉成 `ApiResult<T>`。

## 驗收標準

- [ ] 所有 JSON API 成功時回傳 `ApiResult<T>`。
- [ ] 所有 JSON API 失敗時回傳 `ApiResult<T>`，且 HTTP 狀態碼符合語意。
- [ ] 401 回應代表未登入或 token 無效，403 回應代表已登入但無權限。
- [ ] API 例外回應包含完整 `Exception` 資訊。
- [ ] 檔案下載成功仍可由瀏覽器或 HTTP client 正常下載 stream。
- [ ] Swagger UI 可輸入 Bearer token 並呼叫受保護 API。
- [ ] JWT 設定可從 `appsettings.json` 讀取。

## 相關檔案

- [ ] `src/MyProject/MyProject.Models/Systems/ApiResult.cs`
- [ ] `src/MyProject/MyProject.Models/Systems/ApiExceptionInfo.cs`
- [ ] `src/MyProject/MyProject.Models/Systems/JwtSettings.cs`
- [ ] `src/MyProject/MyProject.Web/appsettings.json`
- [ ] `src/MyProject/MyProject.Web/Program.cs`
- [ ] `src/MyProject/MyProject.Web/Controllers/`
- [ ] `src/MyProject/MyProject.Business/Services/Other/MyUserServiceLogin.cs`

## 備註風險

- [ ] 使用者已指定例外完整回傳，因此正式環境可能洩漏敏感資訊；文件與部署檢查需明確標示。
- [ ] 不落庫 refresh JWT 無法可靠撤銷；若需要安全登出、裝置管理、token rotation，未來需新增 refresh token 資料表。
- [ ] 若所有錯誤只看 `ApiResult.Success` 而忽略 HTTP 狀態碼，外部 client、proxy、監控工具會較難正確判斷錯誤類型；本專案規劃維持語意狀態碼。

