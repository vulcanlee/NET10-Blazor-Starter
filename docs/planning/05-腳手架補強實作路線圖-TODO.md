# 腳手架補強實作路線圖 TODO

- 文件版本：1.1
- 文件狀態：進行中
- 現行系統版本：0.4.42
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/26

> 📌 本文為**規劃階段的快照**，保留當時的判斷脈絡。系統現況請以 [`docs/prd/`](../prd/README.md)
> 的能力覆蓋矩陣為準；各次異動的落地細節見 [`docs/changelog/`](../changelog/README.md)。

## 第一階段：API 與 JWT 基礎
- [x] 目標說明：先補齊日後所有系統都會需要的 API contract、JWT、Swagger、測試與 CI。
- [x] 現況盤點：第一階段已完成，並保留既有 API 路由相容性。
- [x] 實作待辦：已新增 Auth DTO、JWT service、AuthController、ApiExceptionFilter、ApiValidationFilter 結構化錯誤、測試專案與 CI。
- [x] 驗收標準：build 成功（0 warning）、`dotnet format --verify-no-changes` 無差異、測試通過、文件編碼檢查通過、弱點掃描未列出風險套件（0.4.32 起共四道 CI 關卡）。
- [x] 相關檔案：`src/MyProject/MyProject.Web/Auth`、`src/MyProject/MyProject.Web/Controllers`、`src/MyProject/MyProject.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：目前 refresh token stateless，不支援單顆 token 撤銷；限制已寫入正式部署安全清單。

## 第二階段：品質收斂
- [x] 修正 50 個 build warnings 中的高價值項目，目前剩 0 個 compiler/analyzer warning，已記錄於文件。
- [x] 拆分 `Program.cs`，已先抽出 localization/application services 與 middleware/static files extension；database/seed 深度拆分保留為後續低風險重構。
- [x] 加入 Web API integration tests，覆蓋 401、login、refresh、me、Project CRUD Bearer 授權與 validation 400；solution 已修正為會實際建置並執行測試專案。
- [x] 補正式部署設定範本，說明 JWT key、資料庫路徑、NLog 路徑、Swagger UI 暴露策略。文件：`docs/operations/正式部署與安全檢查清單.md`。

## 第三階段：腳手架產品化
- [x] 建立新專案改名與替換清單。文件：`docs/guides/腳手架新專案啟動流程.md`。
- [x] ~~建立 SQL Server 切換與 migration 操作文件。~~ **已於 0.4.24 作廢**：SQL Server 支援整體移除，該文件已刪除。
- [x] 建立預設帳號初始化設定流程：新增 `BootstrapSettings` 支援覆寫 support 帳號與密碼；強制改密碼功能化仍可依正式需求另開任務。
- [x] 建立 API versioning 策略，但不破壞目前 `/api/...`。文件：`docs/architecture/API Versioning 策略.md`。
- [x] 建立新專案初始化與 CRUD 模組骨架腳本：`scripts/New-StarterProject.ps1`、`scripts/New-CrudModule.ps1`。
- [x] 補齊 Production 啟動安全檢查、health checks、CORS、rate limiting 與 `/api/v1/...` 平行路由。

---

## 第四階段：安全與工程品質（0.4.32 – 0.4.41）

- [x] 建立工程品質關卡：`.editorconfig`、`TreatWarningsAsErrors`、Central Package Management、
      `global.json`，CI 加入 `dotnet format --verify-no-changes`（0.4.32）。
      文件：[`CI-CD與品質檢查.md`](../operations/CI-CD與品質檢查.md)、
      紀錄：[`工程品質關卡`](../changelog/2026-08-26-工程品質關卡.md)
- [x] 權限一致性：修正 `/projects` 用錯權限鍵、清除「使用者管理／角色管理」死權限、
      權限鍵去空白，並新增四方一致性守門測試（0.4.33）。
      紀錄：[`權限一致性修正`](../changelog/2026-08-26-權限一致性修正.md)
- [x] API 安全缺陷修正：Production 不再外洩例外堆疊、停用帳號不得取得 JWT、
      refresh 回查資料庫、移除模板遺留 Controller 並改為預設拒絕（0.4.34）。
      紀錄：[`API安全缺陷修正`](../changelog/2026-08-26-API安全缺陷修正.md)
- [x] API 安全基礎設施：限流依呼叫端分割並可設定、安全回應標頭、`/UploadFiles` 需登入、
      上傳副檔名白名單與 ContentType 正規化（0.4.35）。
      紀錄：[`API安全基礎設施補強`](../changelog/2026-08-26-API安全基礎設施補強.md)
- [x] Blazor 路徑改用 `IDbContextFactory`，`CleanTrackingHelper` 整條慣例退場（0.4.36）。
      紀錄：[`DbContextFactory遷移`](../changelog/2026-08-26-DbContextFactory遷移.md)
- [x] 限流實跑驗證修正：政策被端點慣例覆蓋、429 被錯誤頁重跑成 400 HTML（0.4.37）。
      紀錄：[`限流實跑驗證修正`](../changelog/2026-08-26-限流實跑驗證修正.md)
- [x] CRUD 樣板收斂與產生器重寫：抽出 `TableSortHelper` / `ViewNotification`，
      `New-CrudModule.ps1` 改為產出可編譯且符合現行慣例的骨架（0.4.38）。
      紀錄：[`CRUD樣板收斂與產生器重寫`](../changelog/2026-08-26-CRUD樣板收斂與產生器重寫.md)
- [x] 分類可指定適用團隊、下拉依使用者所屬團隊過濾（0.4.40）。
      紀錄：[`分類綁定團隊`](../changelog/2026-08-26-分類綁定團隊.md)
- [x] 分類／團隊名稱唯一性補強：寫入前正規化 + 資料庫唯一索引 + API 判定語意對齊（0.4.41）。
      紀錄：[`名稱唯一性補強`](../changelog/2026-08-26-名稱唯一性補強.md)
