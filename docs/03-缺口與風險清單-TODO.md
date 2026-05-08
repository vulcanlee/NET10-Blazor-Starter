# 缺口與風險清單 TODO

## 已處理風險
- [x] 目標說明：記錄安全、套件、測試、CI、warning、密碼與預設帳號等風險，避免腳手架問題被複製到新系統。
- [x] 現況盤點：AutoMapper 已為 16.1.1；套件弱點掃描目前未列出已知易受攻擊套件。
- [x] 實作待辦：已新增 JWT Bearer、ApiResult 例外封裝、測試專案與 CI。
- [x] 驗收標準：`dotnet list src/MyProject/MyProject.slnx package --vulnerable --include-transitive` 未列出弱點套件。
- [x] 相關檔案：`src/MyProject/MyProject.Web/MyProject.Web.csproj`、`src/MyProject/MyProject.Tests`、`.github/workflows/dotnet-ci.yml`。
- [x] 備註風險：`ApiResult.Exception` 依需求完整回傳例外，正式環境可能揭露堆疊與內部資訊；已納入 `docs/正式部署與安全檢查清單.md`。

## 尚待處理風險
- [x] 將 build warning 從 56 個收斂到 0 個；剩餘僅有 .NET preview SDK 提示訊息，非程式碼 warning。
- [x] 將 `appsettings.json` 內開發用 JWT signing key 改成部署環境 secret 的要求已納入 release checklist；實際正式 secret 需由部署環境提供。
- [x] 強化預設帳號與密碼策略：新增 `BootstrapSettings`，可用設定或環境變數覆寫預設 support 帳號與密碼；正式部署替換流程已寫入 checklist。
- [x] 補 refresh token 不落庫限制說明：目前無法可靠撤銷單一 refresh token，只能靠 signing key 輪替或縮短有效期。
- [x] 建立正式部署前安全檢查清單，包含 HTTPS、Swagger UI 暴露範圍、CORS、secret、日誌敏感資訊。文件：`docs/正式部署與安全檢查清單.md`。