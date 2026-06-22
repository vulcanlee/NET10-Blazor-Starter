# 設計規格：紀錄分類/團隊標籤與團隊權控（階段二）

> 以 superpowers brainstorming 流程產出。對應實作見 changelog [`2026-06-22-紀錄分類團隊與權控.md`](../../changelog/2026-06-22-紀錄分類團隊與權控.md)。

## 背景與目標

承接階段一（Category/Team 主資料）。本階段把分類/團隊掛到 Project/MyTask/Meeting 成為多值標籤，並導入以角色為基礎的團隊行級權控，做法移植自母專案 KnowledgeExtraction.AI。

## 範圍決策（與使用者確認）

- 權控執行面 **同母專案**：Blazor 服務層（清單過濾 + 單筆 `GetAsync(id)` 守門 + 檔案下載守門）；雙模式 `IRecordAccessScopeProvider`；Web API repository 路徑不做行級過濾。
- Migration 延續階段一：僅 SQLite。

## 架構

- **儲存**：`Categories`/`Teams` 以換行分隔字串（`TagStringHelper`，`"\n值\n"` 精確比對）；`RoleView.DefaultTeamsJson` 以 JSON 陣列（`TeamJsonHelper`）。
- **權控**：`IRecordAccessScopeProvider.GetAsync()` 回 `RecordAccessScope(IsAdmin, Teams)`；非管理員清單套 `BuildTeamAccessPredicate`，單筆/下載用 `IsTeamAccessible`。使用者團隊來自 `CurrentUser.TeamList`（登入時由角色 `DefaultTeams` 載入）。
- **轉換**：AutoMapper `ForMember` 處理 List↔分隔字串、`DefaultTeams`↔`DefaultTeamsJson`。
- **UI**：三紀錄頁工具列分類/團隊過濾、表格標籤欄、編輯多選；角色頁預設團隊多選。

## 可見性規則

| 使用者 | 可見紀錄 |
|--------|----------|
| 管理員 | 全部 |
| 非管理員（角色有團隊） | 無團隊（公開）或 Teams 與角色團隊有交集 |
| 非管理員（角色無團隊） | 僅無團隊（公開） |

## 測試

- `TagStringHelperTests`：往返、去重去空白、精確成員比對述詞、`IsTeamAccessible` 四情境。
- `ProjectServiceTeamAccessTests`：管理員全見、非管理員交集/公開、無團隊僅公開、團隊過濾、單筆守門。

## 驗證結果

- `dotnet build -c Release`：0 錯誤。
- `dotnet test`：79 筆通過（既有 65 + 新增 14）。
- SQLite migration `AddRecordTagsAndRoleTeams` 為 7 欄 delta。

## 後續

- 階段三：changelog 通用型改善移植。
