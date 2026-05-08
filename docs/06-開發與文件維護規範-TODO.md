# 開發與文件維護規範 TODO

## 文件規範
- [x] 目標說明：所有補強工作都要同步留下繁體中文文件，讓日後能逐項勾選與驗收。
- [x] 現況盤點：本輪六份 TODO 文件已更新為 UTF-8 BOM、繁體中文 Markdown checkbox 格式。
- [x] 實作待辦：完成後即更新 checkbox、驗收指令、剩餘風險與下一步。
- [x] 驗收標準：文件以 UTF-8 BOM 寫入，避免繁體中文亂碼。
- [x] 相關檔案：`docs/01-專案總覽與定位-TODO.md` 到 `docs/06-開發與文件維護規範-TODO.md`。
- [x] 備註風險：已掃描 `src`、`docs`、`scripts`、`.github` 主要文字檔，未發現 replacement character；終端顯示亂碼多半與主控台字型/編碼顯示有關，後續若要修正文案可另開逐檔校稿任務。

## 維護流程待辦
- [x] 每次完成功能後更新 TODO checkbox。
- [x] 每次驗證後記錄 build/test/vulnerability scan 結果。本輪驗證：Release build 成功、solution test 實際執行 9 個測試並全數通過、弱點掃描未列出風險；CI 已設定 `NUGET_HTTP_TIMEOUT_SECONDS=180`，降低 NuGet 來源偶發逾時造成假失敗。
- [x] 建立自動檢查文件 UTF-8 BOM 的腳本與 CI step。腳本：`scripts/Test-DocsEncoding.ps1`。
- [x] 建立文件更新 PR checklist，正式部署與安全檢查清單已納入驗收流程。
- [x] 將主要設計同步到長期文件：新增 API versioning、DTO 邊界、release checklist 與 seed 設定文件，並保留 TODO 作為追蹤入口。