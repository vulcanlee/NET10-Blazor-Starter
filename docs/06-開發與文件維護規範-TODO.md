# 開發與文件維護規範 TODO

## 文件規範
- [x] 目標說明：所有補強工作都要同步留下繁體中文文件，讓日後能逐項勾選與驗收。
- [x] 現況盤點：本輪六份 TODO 文件已更新為 UTF-8 BOM、繁體中文 Markdown checkbox 格式。
- [x] 實作待辦：完成後即更新 checkbox、驗收指令、剩餘風險與下一步。
- [x] 驗收標準：文件以 UTF-8 BOM 寫入，避免繁體中文亂碼。
- [x] 相關檔案：`docs/01-專案總覽與定位-TODO.md` 到 `docs/06-開發與文件維護規範-TODO.md`。
- [ ] 備註風險：部分既有程式註解或 appsettings 內容在終端顯示仍可能出現亂碼，需另開任務逐檔檢查原始編碼。

## 維護流程待辦
- [x] 每次完成功能後更新 TODO checkbox。
- [x] 每次驗證後記錄 build/test/vulnerability scan 結果。
- [ ] 建立自動檢查文件 UTF-8 BOM 的腳本或 CI step。
- [ ] 建立文件更新 PR checklist，避免功能已改但文件未同步。
- [ ] 將主要設計同步到 `docs/Web API 設計慣例.md` 與 `docs/認證授權與權限機制.md`，讓 TODO 與長期文件一致。