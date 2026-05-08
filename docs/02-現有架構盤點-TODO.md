# 現有架構盤點 TODO

## 分層現況
- [x] 目標說明：盤點腳手架分層，讓後續系統能從清楚的責任邊界開始擴充。
- [x] 現況盤點：目前包含 `AccessDatas`、`Business`、`Dtos`、`Models`、`Share`、`Web`、`Tests` 七個專案。
- [x] 實作待辦：已新增測試專案並加入 solution；Web API 已使用 DTO 作為 request/response，不直接暴露 Entity。
- [x] 驗收標準：`ProjectController`、`MyTaskController`、`MeetingController` 使用 Create/Update/Search/Dto 類別作為 API 邊界。
- [x] 相關檔案：`src/MyProject/MyProject.Dtos`、`src/MyProject/MyProject.Web/Controllers`、`src/MyProject/MyProject.Tests`。
- [ ] 備註風險：Controller 仍有部分手動 try/catch 與商業訊息，下一階段可抽出共用 API response helper，降低重複碼。

## 架構整理待辦
- [x] 拆分 `Program.cs`，建立 service registration extension 與 middleware extension。已新增 `Extensions/ServiceCollectionExtensions.cs` 與 `Extensions/ApplicationBuilderExtensions.cs`。
- [x] 修正 `Program.cs` 內 ASP0000 `BuildServiceProvider` warning，改由 `app.Services.GetRequiredService<ILogger<Program>>()` 取得 logger。
- [x] 清理 `ProjectRepository` 的 self-assignment warning，保留目前 API shape 無 related include 的註解。
- [ ] 評估 `Models` 與 `Dtos` 專案責任邊界，避免 AdapterModel 與 API DTO 混用。