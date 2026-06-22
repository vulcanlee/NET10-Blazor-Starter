# 設計規格：分類清單 / 團隊清單管理頁面（階段一）

> 以 superpowers brainstorming 流程產出。對應實作見 changelog [`2026-06-22-分類與團隊清單.md`](../../changelog/2026-06-22-分類與團隊清單.md)。

## 背景與目標

NET10-Blazor-Starter 將以母專案 KnowledgeExtraction.AI 為藍本，分階段成長為知識庫萃取系統。本規格涵蓋**階段一**：新增兩個獨立主資料管理頁面（Category 分類、Team 團隊），含 Web API、權限與雙資料庫 migration 政策，作為階段二「紀錄分類/團隊標籤 + 團隊權控」的前置基礎。

## 範圍決策（與使用者確認）

- 交付方式：**分階段**（本規格僅階段一）。
- 四頁面整合深度（階段二）：**完整團隊權控**（同母專案）。
- Category/Team **提供 Web API Controller**（與既有 CRUD API 一致）。
- changelog 通用型改善：**另開階段**（階段三）。
- SqlServer migration：腳手架 SqlServerMigrations 專案從未 bootstrap，本階段**僅產生 SQLite migration**。

## 架構

兩個獨立主資料 CRUD，完全沿用腳手架既有分層慣例：

```
Blazor Page (@page) → XxxViewView (.razor/.cs/.css)
   → Service (DataRequest/DataRequestResult/VerifyRecordResult, BeforeXxxCheck)
Web API Controller (ApiResult, JWT, /api + /api/v1)
   → Repository (PagedResult, SearchRequestDto)
共用：Entity ↔ AdapterModel ↔ Dto/CreateUpdateDto（AutoMapper 雙向）
```

### Entity 欄位
- Category：Id、Name（必填、唯一）、Description?、IsEnabled、CreatedAt、UpdatedAt。
- Team：Id、Name（必填、唯一）、Code?（選填、有填則唯一）、Description?、IsEnabled、CreatedAt、UpdatedAt。

### 驗證
- Service 層 `BeforeAddCheckAsync` / `BeforeUpdateCheckAsync` 做名稱唯一性（trim、不分大小寫；更新排除自身）；Team 另驗 Code 唯一。
- API 層 Controller 以 `ExistsByNameAsync`／`ExistsByCodeAsync` 回 409 Conflict。

### 權限（位置索引對應，三處同步）
- `MagicObjectHelper`：`角色_資料定義`、`角色_分類清單`、`角色_團隊清單`。
- `RolePermissionService.GetRoleListPermissionAllName()`：在「登出」群組之前插入 `[資料定義, 分類清單, 團隊清單]`。
- `Menu.json`：對應位置（系統管理後、登出前）新增「資料定義」群組。
- 不變量：`SidebarMenuService.ApplyPermissionStructure` 以 index 對應 menu 與權限群組，故兩者順序必須一致。預設角色啟動 seed 自動同步；既有自訂角色需重新勾選。

## 測試

- `CategoryServiceTests`、`TeamServiceTests`（in-memory SQLite fixture）：名稱/代號唯一性、更新排除自身、新增持久化。

## 驗證結果

- `dotnet build -c Release`：0 錯誤。
- `dotnet test`：65 筆通過（既有 52 + 新增 13）。
- SQLite migration `AddCategoryAndTeam` 為僅含 Category/Team 的 delta。

## 後續階段（備忘）

- 階段二：Project/Task/Meeting 加 `Categories`/`Teams`、`RoleView.DefaultTeamsJson`、`TagStringHelper`/`TeamJsonHelper`、`IRecordAccessScopeProvider` 行級權控、四頁面多選與篩選、角色頁指派團隊。
- 階段三：changelog 通用型改善移植。
