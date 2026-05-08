# 腳手架補強實作路線圖 TODO

## 第一階段：API 與 JWT 基礎
- [x] 目標說明：先補齊日後所有系統都會需要的 API contract、JWT、Swagger、測試與 CI。
- [x] 現況盤點：第一階段已完成，並保留既有 API 路由相容性。
- [x] 實作待辦：已新增 Auth DTO、JWT service、AuthController、ApiExceptionFilter、ApiValidationFilter 結構化錯誤、測試專案與 CI。
- [x] 驗收標準：build 成功、測試通過、弱點掃描未列出風險套件。
- [x] 相關檔案：`src/MyProject/MyProject.Web/Auth`、`src/MyProject/MyProject.Web/Controllers`、`src/MyProject/MyProject.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：目前 refresh token stateless，不支援單顆 token 撤銷；限制已寫入正式部署安全清單。

## 第二階段：品質收斂
- [x] 修正 56 個 build warnings 中的高價值項目，目前剩 6 個低風險 warning，已記錄於文件。
- [x] 拆分 `Program.cs`，已先抽出 localization/application services 與 middleware/static files extension；database/seed 深度拆分保留為後續低風險重構。
- [x] 加入 Web API integration tests，覆蓋 401、login、refresh、me、Project CRUD Bearer 授權與 validation 400；solution 已修正為會實際建置並執行測試專案。
- [x] 補正式部署設定範本，說明 JWT key、資料庫路徑、NLog 路徑、Swagger UI 暴露策略。文件：`docs/正式部署與安全檢查清單.md`。

## 第三階段：腳手架產品化
- [x] 建立新專案改名與替換清單。文件：`docs/腳手架新專案啟動流程.md`。
- [x] 建立 SQL Server 切換與 migration 操作文件。文件：`docs/SQL Server 切換說明.md`。
- [x] 建立預設帳號初始化設定流程：新增 `BootstrapSettings` 支援覆寫 support 帳號與密碼；強制改密碼功能化仍可依正式需求另開任務。
- [x] 建立 API versioning 策略，但不破壞目前 `/api/...`。文件：`docs/API Versioning 策略.md`。