# 腳手架補強實作路線圖 TODO

## 第一階段：API 與 JWT 基礎
- [x] 目標說明：先補齊日後所有系統都會需要的 API contract、JWT、Swagger、測試與 CI。
- [x] 現況盤點：第一階段已完成，並保留既有 API 路由相容性。
- [x] 實作待辦：已新增 Auth DTO、JWT service、AuthController、ApiExceptionFilter、ApiValidationFilter 結構化錯誤、測試專案與 CI。
- [x] 驗收標準：build 成功、測試通過、弱點掃描未列出風險套件。
- [x] 相關檔案：`src/MyProject/MyProject.Web/Auth`、`src/MyProject/MyProject.Web/Controllers`、`src/MyProject/MyProject.Tests`、`.github/workflows/dotnet-ci.yml`。
- [ ] 備註風險：目前 refresh token stateless，不支援單顆 token 撤銷。

## 第二階段：品質收斂
- [ ] 修正 56 個 build warnings，先處理本輪新增以外但會影響腳手架品質的 nullable 與 analyzer warning。
- [ ] 拆分 `Program.cs`，把 authentication、Swagger、localization、database、seed、middleware 註冊拆成 extension。
- [ ] 加入 Web API integration tests，覆蓋未登入、登入、refresh、CRUD 授權與 validation body。
- [ ] 補正式部署設定範本，說明 JWT key、資料庫路徑、NLog 路徑、Swagger UI 暴露策略。

## 第三階段：腳手架產品化
- [ ] 建立新專案改名與替換清單。
- [ ] 建立 SQL Server 切換與 migration 操作文件。
- [ ] 建立預設帳號初始化與強制改密碼流程。
- [ ] 建立 API versioning 策略，但不破壞目前 `/api/...`。