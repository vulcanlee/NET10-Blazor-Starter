# 腳手架補強實作路線圖 TODO

## 目標說明

- [ ] 將腳手架補強工作依優先順序整理成可逐項執行的待辦清單。
- [ ] 讓日後實作時可以依階段開 branch、寫測試、驗收、更新文件。
- [ ] 先處理會被所有未來系統繼承的基礎問題，再處理可選增強項。

## 現況盤點

- [ ] 專案目前可建置成功，但仍有套件弱點警告。
- [ ] 文件已有 EF Core 與 CRUD 複刻說明，但尚未形成完整腳手架 backlog。
- [ ] Web API 與 JWT 是目前最重要的未完成核心機制。
- [ ] 測試與 CI 尚未建立，導致後續重構風險較高。
- [ ] Program.cs、檔案上傳、權限定義等區塊需要逐步整理。

## 實作待辦

- [ ] 第一階段：文件與安全基準。
- [ ] 建立本 TODO 文件組。
- [ ] 升級或替換有弱點的 AutoMapper 套件。
- [ ] 補上套件弱點掃描與過期套件清單。
- [ ] 補上正式環境安全注意事項。
- [ ] 第二階段：API 標準化。
- [ ] 建立 `ApiResult<T>` 與 `ApiExceptionInfo`。
- [ ] 建立 API 統一例外封裝機制。
- [ ] 建立 CRUD API 樣板。
- [ ] 建立檔案下載錯誤時的 `ApiResult<T>` 回應。
- [ ] 第三階段：JWT 認證。
- [ ] 建立 `JwtSettings` 與 Options validation。
- [ ] 加入 JWT Bearer authentication scheme。
- [ ] 建立 login、refresh、me API。
- [ ] 加入 Swagger Bearer 授權。
- [ ] 第四階段：測試與 CI。
- [ ] 新增測試專案。
- [ ] 補 Business service 測試。
- [ ] 補 API controller 或 integration test。
- [ ] 補 JWT token service 測試。
- [ ] 新增 GitHub Actions workflow。
- [ ] 第五階段：架構整理。
- [ ] 拆分 `Program.cs`。
- [ ] 抽象化檔案儲存服務。
- [ ] 整理權限定義與 Menu 對應。
- [ ] 修正 nullable warning。
- [ ] 第六階段：維運能力。
- [ ] 新增 health checks。
- [ ] 新增 rate limiting。
- [ ] 補 CORS 設定策略。
- [ ] 補 SQL Server 切換指南。
- [ ] 補部署與環境變數設定指南。

## 驗收標準

- [ ] 每一階段都可以獨立建立 PR。
- [ ] 每一階段完成後都更新相關 TODO checkbox。
- [ ] API、JWT、測試、CI 完成後，腳手架可作為新系統開發起點。
- [ ] 重要安全風險都有文件、設定或測試保護。

## 相關檔案

- [ ] `docs/`
- [ ] `src/MyProject/MyProject.Web/`
- [ ] `src/MyProject/MyProject.Business/`
- [ ] `src/MyProject/MyProject.Models/`
- [ ] `src/MyProject/MyProject.AccessDatas/`
- [ ] `.github/workflows/`

## 備註風險

- [ ] 若先做大量重構再補測試，可能難以確認行為沒有改壞。
- [ ] 若 JWT 與 ApiResult 同時大改但沒有 integration test，外部 API 使用者容易遇到破壞性變更。
- [ ] 若文件 checkbox 完成後未同步驗收證據，TODO 文件會失去可信度。

