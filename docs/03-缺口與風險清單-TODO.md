# 缺口與風險清單 TODO

## 已處理風險
- [x] 目標說明：記錄安全、套件、測試、CI、warning、密碼與預設帳號等風險，避免腳手架問題被複製到新系統。
- [x] 現況盤點：AutoMapper 已為 16.1.1；套件弱點掃描目前未列出已知易受攻擊套件。
- [x] 實作待辦：已新增 JWT Bearer、ApiResult 例外封裝、測試專案與 CI。
- [x] 驗收標準：`dotnet list src/MyProject/MyProject.slnx package --vulnerable --include-transitive` 未列出弱點套件。
- [x] 相關檔案：`src/MyProject/MyProject.Web/MyProject.Web.csproj`、`src/MyProject/MyProject.Tests`、`.github/workflows/dotnet-ci.yml`。
- [ ] 備註風險：`ApiResult.Exception` 依需求完整回傳例外，正式環境可能揭露堆疊與內部資訊，部署前需再次確認策略。

## 尚待處理風險
- [ ] 修正目前 build 摘要中的 56 個 warning，優先順序為 nullable、ASP0000、Repository self-assignment、Blazor analyzer warning。
- [ ] 將 `appsettings.json` 內開發用 JWT signing key 改成部署環境 secret，不可沿用到正式環境。
- [ ] 強化預設帳號與密碼策略，避免腳手架複製後保留可預測帳密。
- [ ] 補 refresh token 不落庫限制說明：目前無法可靠撤銷單一 refresh token，只能靠 signing key 輪替或縮短有效期。
- [ ] 建立正式部署前安全檢查清單，包含 HTTPS、Swagger UI 暴露範圍、CORS、secret、日誌敏感資訊。