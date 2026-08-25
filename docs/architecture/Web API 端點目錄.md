# Web API 端點目錄

- 文件版本：1.2
- 文件狀態：已實作
- 現行系統版本：0.4.31
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/25

本文件彙整 `MyProject.Web/Controllers/` 下所有 Web API 端點的實際路由、HTTP 動詞、授權與回傳型別，作為《[Web API 設計慣例](Web%20API%20設計慣例.md)》（樣板與慣例）之外的**端點清單參照**。慣例細節（`ApiResult<T>`、`PagedResult<T>`、Search DTO、動作級授權）見設計慣例文件。

## 一、通則

- 每個資源控制器同時掛 `api/[controller]` 與 `api/v1/[controller]` 兩條平行路由（見《[API Versioning 策略](API%20Versioning%20策略.md)》）。
- 資源控制器類別層級套 `[ApiController]`、`[ApiValidationFilter]`、`[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`（API 用 JWT Bearer）。**唯一例外是 `ProjectFileController`（見第三節），它走 Cookie。**
- 每個動作以 `[HasPermission(資源鍵, 動作)]` 做動作級授權；無權限回 **403** 並維持 `ApiResult` 外殼；管理員短路。權限鍵定義於 `MyProject.Share` 的 `MagicObjectHelper`，動作為 `PermissionActions.View/Create/Edit/Delete`。
- 回傳一律包在 `ApiResult<T>`；分頁再包 `PagedResult<T>`。

## 二、資源 CRUD 控制器

五個資源控制器共用同一組動作樣板（以 `CategoryController` 為代表，`src/MyProject/MyProject.Web/Controllers/CategoryController.cs:35`）：

| 動作 | 路由（相對 `api/` 與 `api/v1/`）| 權限（`PermissionActions`）| 回傳 |
|------|------|------|------|
| 取得單筆 | `GET {controller}/{id}` | View | `ApiResult<TDto>`（查無回 `NotFound`）|
| 搜尋分頁 | `POST {controller}/search` | View | `ApiResult<PagedResult<TDto>>` |
| 新增 | `POST {controller}` | Create | `ApiResult<TDto>`（同名回 `Conflict`）|
| 更新 | `PUT {controller}/{id}` | Edit | `ApiResult`（路由/資料 ID 不符回 `BadRequest`；查無回 `NotFound`）|
| 刪除 | `DELETE {controller}/{id}` | Delete | `ApiResult`（查無回 `NotFound`）|

各控制器對應的路由前綴與權限鍵：

| 控制器 | 路由前綴 | 權限鍵（`MagicObjectHelper`）| 檔案 |
|--------|----------|------------------------------|------|
| `CategoryController` | `api/Category`、`api/v1/Category` | `角色_分類清單` | `Controllers/CategoryController.cs` |
| `TeamController` | `api/Team`、`api/v1/Team` | `角色_團隊清單` | `Controllers/TeamController.cs` |
| `ProjectController` | `api/Project`、`api/v1/Project` | `角色_專案項目` | `Controllers/ProjectController.cs` |

## 三、專案附件下載 `ProjectFileController`

`src/MyProject/MyProject.Web/Controllers/ProjectFileController.cs`，路由 `api/project-files`、`api/v1/project-files`。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 下載附件 | `GET project-files/{id}/download` | `[Authorize(CookieScheme)]` ＋ `[HasPermission(角色_專案項目, View)]` | 成功回**原生 File stream**；其餘回 `ApiResult` 404 |

與其他控制器不同的三點，都源自「呼叫端是畫面上的一般連結而非程式」：

- **驗證走 Cookie 不走 JWT**。連結由瀏覽器直接導覽（`<a href target="_blank">`），帶的是登入 Cookie。
- **路由是 kebab-case**，不套 `api/[controller]` 慣例，以維持 UI 既有網址。
- **成功時不包 `ApiResult`**，維持原生 File stream（`enableRangeProcessing: true`，支援 1GB 附件續傳）；只有錯誤才回 `ApiResult`。

查無紀錄、團隊越界（`ProjectService.GetFileDownloadAsync` 以 `IsTeamAccessible` 守門）、實體檔案不存在**一律回 404**，不以狀態碼洩漏檔案是否存在；三者差異由 Service 層的 Warning 日誌區分。成功下載寫入稽核 `Project.FileDownload`。

## 四、認證控制器 `AuthController`

`src/MyProject/MyProject.Web/Controllers/AuthController.cs`，路由 `api/Auth`、`api/v1/Auth`；帳密換 JWT。

| 動作 | 路由 | 授權 | 回傳 |
|------|------|------|------|
| 登入 | `POST Auth/login` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（失敗回 `Unauthorized`）|
| 換發 Token | `POST Auth/refresh` | `[AllowAnonymous]` | `ApiResult<TokenResponseDto>`（Refresh Token 無效回 `Unauthorized`）|
| 目前使用者 | `GET Auth/me` | `[Authorize(JwtBearer)]` | `ApiResult<CurrentUserDto>`（讀 JWT Claims）|

## 五、Google 第三方登入 `ExternalAuthController`

`src/MyProject/MyProject.Web/Controllers/ExternalAuthController.cs`，路由前綴 `Auths/Google`。**此為網頁 Cookie 登入導向端點，非 API**（回傳 `Challenge`／`Redirect`，不走 `ApiResult`）。詳見《[Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md)》。

| 動作 | 路由 | 說明 |
|------|------|------|
| 觸發 Google 驗證 | `GET Auths/Google/Login` | 未設定金鑰時導回 `/Auths/Login`|
| 驗證回呼 | `GET Auths/Google/Callback` | 查找/建立帳號；停用帳號導向 `/Auths/Pending`，啟用則完成 Cookie 登入 |

## 六、模板遺留

`WeatherForecastController`（路由 `[controller]`，即 `/WeatherForecast`）為專案模板遺留範例，非正式能力；新專案啟動時可移除（見《[腳手架新專案啟動流程](../guides/腳手架新專案啟動流程.md)》）。

## 七、相關文件

- [Web API 設計慣例](Web%20API%20設計慣例.md)
- [API Versioning 策略](API%20Versioning%20策略.md)
- [認證授權與權限機制](../security/認證授權與權限機制.md)
- [紀錄分類與團隊權控 PRD](../prd/紀錄分類與團隊權控-prd.md)

> 返回 [architecture 索引](README.md)
