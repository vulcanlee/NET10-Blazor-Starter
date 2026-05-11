# Web API 設計慣例

## 目的
本文件記錄腳手架 Web API 的固定設計規範，未來新增 API 時應遵守同一套 contract，讓前端與外部用戶端能用一致格式處理成功、失敗、驗證錯誤、授權錯誤與例外。

## Controller 規範
- API Controller 放在 `src/MyProject/MyProject.Web/Controllers/`。
- 路由需同時提供 `[Route("api/[controller]")]` 與 `[Route("api/v1/[controller]")]`；新用戶端優先使用 `/api/v1/...`。
- Controller 必須使用 `[ApiController]` 與 `[ApiValidationFilter]`。
- 需要保護的 CRUD API 必須加上 `[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]`。
- API request/response 必須使用 DTO，不可以直接接收或回傳 Entity。
- Entity 與 DTO 轉換優先使用 AutoMapper profile 維護。

## 統一回傳格式
所有一般 Web API 回應固定使用 `MyProject.Dtos.Commons.ApiResult<T>` 或非泛型 `ApiResult`。

標準欄位：
- `Success`：是否成功。
- `StatusCode`：HTTP 狀態碼。
- `Message`：使用者可讀訊息。
- `Data`：成功時的回傳資料。
- `Errors`：欄位或規則錯誤集合。
- `TraceId`：請求追蹤識別碼。
- `Exception`：例外資訊。Development 或 `Security:ReturnExceptionDetails=true` 時回傳；Production 預設不回傳。

相容欄位：
- `ErrorMessage`：舊版錯誤訊息欄位，暫時保留。
- `ErrorDetail`：舊版錯誤詳細欄位，暫時保留。
- `Timestamp`：舊版時間戳欄位，暫時保留。

## HTTP 狀態碼
API 必須維持語意正確的 HTTP 狀態碼，Body 再包成 `ApiResult<T>`。

| 狀態碼 | 使用情境 |
| --- | --- |
| 200 | 查詢、更新、刪除等成功 |
| 400 | ModelState 或商業規則驗證失敗 |
| 401 | 未登入、未提供 Bearer token、token 無效 |
| 403 | 已登入但權限不足 |
| 404 | 找不到指定資源 |
| 409 | 資料衝突，例如名稱重複 |
| 500 | 未預期例外 |

## JWT 與 Swagger
Swagger 已設定 Bearer security definition。開發者可在 Swagger UI 透過 Authorize 輸入 JWT access token 後測試受保護 API。

JWT access token 與 refresh token 由 `AuthController` 提供：
- `POST /api/Auth/login`
- `POST /api/Auth/refresh`
- `GET /api/Auth/me`
- `POST /api/v1/Auth/login`
- `POST /api/v1/Auth/refresh`
- `GET /api/v1/Auth/me`

## 例外與驗證
- `ApiValidationFilter` 會將 ModelState 錯誤轉成 `ApiResult<T>.Errors`。
- `ApiExceptionFilter` 會攔截未處理 API 例外，依 `Security:ReturnExceptionDetails` 決定是否填入 `ApiResult.Exception`。
- 既有 Controller 內手動 catch 的例外也應呼叫 `ApiResult.ServerErrorResult(message, exception)`，保留完整 exception。

## 待辦
- [x] 補完整 integration tests，實際驗證 400、401、403、404、409、500 的 HTTP body。
- [x] 正式環境預設隱藏 `Exception.StackTrace`。
- [x] 已導入 `/api/v1/...` 平行路由，目前保留 `/api/...` 相容路由。
