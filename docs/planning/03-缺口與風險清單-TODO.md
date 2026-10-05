# 缺口與風險清單 TODO

- 文件版本：1.1
- 文件狀態：已封存（規劃階段快照，不再維護）
- 現行系統版本：0.4.42
- 首次實作版本：0.1.61
- 最後核對日期：2026/08/26

> 📌 本文為**規劃階段的快照**，保留當時的判斷脈絡。系統現況請以 [`docs/prd/`](../prd/README.md)
> 的能力覆蓋矩陣為準；各次異動的落地細節見 [`docs/changelog/`](../changelog/README.md)。
> ⚠️ 本檔頭的「現行系統版本／最後核對日期」**刻意停在快照當時**，不隨系統版本推進 ——
> 它記錄的是「當時看到的樣子」，更新它反而會讓人誤以為內容經過重新查證。

> 📌 現況差異（2026/10/05 核對 0.9.114）：本文為封存快照，內容維持當時原貌。與現行系統不同之處：
> - `.github/workflows/dotnet-ci.yml` 已移除、沒有 CI；`dotnet format` 等檢查改由本機 `scripts/Invoke-QualityGate.ps1` 執行（0.9.89 起）—— 見 [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md)
> - Refresh token 仍不落庫，但 0.9.103 起帶 `SecurityStamp`、refresh 時比對，改密碼、停用、改角色、強制登出會讓該使用者所有 refresh 失效 —— 見[認證授權與權限機制](../security/認證授權與權限機制.md)
> - 密碼策略不只 `BootstrapSettings`：另有 `IPasswordPolicy`（規則、歷史、到期）、可設定的登入鎖定、`MustChangePassword`（0.9.101）與兩步驟驗證（0.9.104）；雜湊為 PBKDF2 —— 見[密碼種類與儲存機制](../security/密碼種類與儲存機制.md)
> - 「.NET preview SDK 提示」已不存在：`global.json` 鎖定 GA SDK `10.0.400`
> - `New-CrudModule.ps1` 0.9.110 起已改為一鍵產生並自動登記 —— 見[建立一個新 CRUD 操作網頁說明](../guides/建立一個新%20CRUD%20操作網頁說明.md)

## 已處理風險
- [x] 目標說明：記錄安全、套件、測試、CI、warning、密碼與預設帳號等風險，避免腳手架問題被複製到新系統。
- [x] 現況盤點：AutoMapper 已為 16.1.1；套件弱點掃描目前未列出已知易受攻擊套件。
- [x] 實作待辦：已新增 JWT Bearer、ApiResult 例外封裝、測試專案與 CI。
- [x] 驗收標準：`dotnet list src/MyProject/MyProject.slnx package --vulnerable --include-transitive` 未列出弱點套件。
- [x] 相關檔案：`src/MyProject/MyProject.Web/MyProject.Web.csproj`、`src/MyProject/MyProject.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：`ApiResult.Exception` 已改為依 `Security:ReturnExceptionDetails` 控制；Production 預設不回傳完整堆疊資訊。

## 已收斂風險（原「尚待處理」，至 0.4.41 均已完成）
- [x] 將 build warning 從 56 個收斂到 0 個；剩餘僅有 .NET preview SDK 提示訊息，非程式碼 warning。
- [x] 將 `appsettings.json` 內開發用 JWT signing key 改成部署環境 secret 的要求已納入 release checklist；實際正式 secret 需由部署環境提供。
- [x] 強化預設帳號與密碼策略：新增 `BootstrapSettings`，可用設定或環境變數覆寫預設 support 帳號與密碼；正式部署替換流程已寫入 checklist。
- [x] 補 refresh token 不落庫限制說明：目前無法可靠撤銷單一 refresh token，只能靠 signing key 輪替或縮短有效期。
- [x] 建立正式部署前安全檢查清單，包含 HTTPS、Swagger UI 暴露範圍、CORS、secret、日誌敏感資訊。文件：`docs/operations/正式部署與安全檢查清單.md`。
- [x] Production 啟動安全檢查已納入 JWT key、support 預設密碼與 Swagger 策略。

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
