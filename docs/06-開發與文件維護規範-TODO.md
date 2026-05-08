# 開發與文件維護規範 TODO

## 目標說明

- [ ] 建立本專案文件與開發待辦的維護規範。
- [ ] 確保所有新增文件使用 UTF-8 BOM 與繁體中文，避免 Windows 工具讀取時產生亂碼。
- [ ] 確保日後完成待辦時，同步更新文件與驗收結果。

## 現況盤點

- [ ] 現有 `readme.md` 與 `docs/*.md` 以 UTF-8 明確讀取時可正常顯示繁體中文。
- [ ] 若 PowerShell 未指定 `-Encoding UTF8`，可能因環境預設編碼導致顯示亂碼。
- [ ] 目前 docs 內有 EF Core 與 CRUD 說明，但尚未有統一文件模板。
- [ ] 目前沒有自動檢查文件編碼的流程。

## 實作待辦

- [ ] 所有新增 Markdown 文件使用 UTF-8 BOM。
- [ ] 所有文件使用繁體中文。
- [ ] 文件檔名可使用繁體中文，但需保持主題清楚且排序穩定。
- [ ] 待辦文件使用 Markdown checkbox：`- [ ]` 與 `- [x]`。
- [ ] 每份 TODO 文件固定包含：目標說明、現況盤點、實作待辦、驗收標準、相關檔案、備註風險。
- [ ] 完成任何功能後，更新對應文件 checkbox。
- [ ] 完成任何功能後，補上驗收命令與結果摘要。
- [ ] 建立文件編碼檢查腳本或 CI 檢查，確認 `.md` 檔案為 UTF-8 BOM。
- [ ] 建立文件讀取建議：PowerShell 使用 `Get-Content -Encoding UTF8`。
- [ ] 建立文件寫入建議：PowerShell 7 可用 `Set-Content -Encoding utf8BOM`。
- [ ] 建立 pull request 檢查清單，要求有功能變更時同步更新 docs。

## 驗收標準

- [ ] 新增文件以十六進位檢查時，開頭為 `EF BB BF`。
- [ ] 使用 `Get-Content -Encoding UTF8` 讀取時，繁體中文無亂碼。
- [ ] 每份 TODO 文件都有固定六個章節。
- [ ] 每個重大機制都有對應待辦、驗收標準與相關檔案。
- [ ] 日後實作完成後，文件 checkbox 與程式碼狀態一致。

## 相關檔案

- [ ] `docs/`
- [ ] `readme.md`
- [ ] `.github/workflows/`
- [ ] `src/MyProject/.github/copilot-instructions.md`

## 備註風險

- [ ] 若未強制 UTF-8 BOM，部分 Windows 工具可能顯示亂碼，造成使用者誤判文件內容損壞。
- [ ] 若 TODO 文件只新增不維護，日後會變成過期文件。
- [ ] 若功能完成但未更新驗收結果，下一位開發者需要重新盤點狀態，會浪費時間。

