# 會議記錄 PRD

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.23
- 首次實作版本：既有腳手架核心功能
- 最後核對日期：2026/07/14

## 一、目標與範圍

提供「會議記錄（Meeting）」的建立、查詢、修改、刪除與附件管理能力；每筆會議必隸屬一個專案（`ProjectId` 必填），並記錄摘要與與會人員。

- 範圍：清單查詢（關鍵字搜尋、分類／團隊過濾、排序、分頁）、單筆維護（含表單驗證）、多檔附件上傳／下載／刪除、動作級授權與團隊可見範圍控管、依所屬專案顯示與過濾。
- 非範圍：會議行事曆／邀請通知、線上會議整合、逐字稿／自動摘要（本腳手架不含 LLM 能力）、附件線上預覽。

## 二、使用者與入口

| 項目 | 內容 |
|------|------|
| UI 路由 | `/meeting`（`MeetingPage.razor`，掛 `MainLayout`） |
| REST API | `api/Meeting`、`api/v1/Meeting`（JWT Bearer） |
| 選單路徑 | 專案管理（`Menu.json` id=2）→ 會議記錄（id=23，`/meeting`） |
| 權限鍵 | 資源鍵「會議項目」（`MagicObjectHelper.角色_會議項目`），動作 `view`／`create`／`edit`／`delete`；管理員短路 |
| 主要使用者 | 具「會議項目」對應動作權限的登入者；管理員可見全部 |

> 頁面進入檢查用 `CheckAccessPage(角色_會議項目)`（`MeetingViewView.razor.cs:86`）；操作鈕以 `CheckAccessAction(角色_會議項目, 動作)` 個別控管。權限鍵字面值為「會議項目」。

## 三、畫面與欄位

- 工具列：新增、重新整理、分類過濾（多選）、團隊過濾（多選）、關鍵字輸入、清空、搜尋。
- 清單欄位（`MeetingViewView.razor:72-82`）：標題、描述、摘要、與會人員、開始日期、結束日期、所屬專案、分類、團隊、建立時間、更新時間。
- 搜尋比對欄位（`MeetingService.cs:60-64`）：Title、Description、Summary、Participants，以及所屬專案 `Project.Title`。
- 分頁：`RemoteDataSource`，預設每頁 `MagicObjectHelper.PageSize`。
- 編輯表單欄位（`Meeting` 實體 / `MeetingCreateUpdateDto`）：標題（必填）、描述（≤2000）、摘要（≤4000）、與會人員（≤2000）、開始日期、結束日期、所屬專案（`ProjectId` 必填）、分類（多值）、團隊（多值，不設定＝公開）。會議無狀態／優先級／完成百分比／負責人欄位。
- 附件：會議附件一次可多選，單檔上限 1GB；已上傳檔案可下載（`/api/meeting-files/{id}/download`）或標記移除。

## 四、內部系統運作

- 資料流：`MeetingPage.razor` → `MeetingViewView` → `MeetingService` → `BackendDBContext.Meeting`。REST API 走 `MeetingController` → `MeetingRepository`，皆回 `ApiResult`／`PagedResult`。
- 讀取：清單 `GetAsync` 以 `Include(x => x.Project)` 帶專案名稱；單筆 `Include(Project)`＋`Include(Files)`（`MeetingService.cs:54,154-155`）。
- 編輯前處理：開啟修改視窗時以 Service 重新取得資料副本並清空待上傳／待移除清單。
- 寫入前清追蹤：`AddAsync`／`UpdateAsync`／`DeleteAsync` 進入時呼叫 `CleanTrackingHelper.Clean<Meeting>(context)`（`MeetingService.cs:193,226,278`）。
- 附件 Adapter：UI 以會議附件輸入模型傳入；Service 依主表 `CreatedAt` 年／月建目錄、GUID 命名後寫入 `MeetingFile`；刪除主表時先刪實體檔。
- 雙資料庫 migration：模型異動需同步 `MyProject.AccessDatas`（SQLite）與 `MyProject.AccessDatas.SqlServerMigrations`。

## 五、權限與安全

- 動作級授權：`MeetingController` 各端點標註 `[HasPermission(角色_會議項目, 動作)]`（`MeetingController.cs:36,65,110,147,195`）；無權限回 403 並維持 `ApiResult`；管理員短路。
- UI 與 API 共用同一 RBAC 權威（`IPermissionChecker`）。
- 團隊可見範圍：非管理員清單以 `TagStringHelper.BuildTeamAccessPredicate<Meeting>` 只看到公開或與自身團隊交集的會議；單筆與附件下載以 `IsTeamAccessible` 守門（`MeetingService.cs:77-80,164,352`）。

## 六、錯誤與邊界

- 標題重複：`Create` 回 409、`Update` 回 409。
- 路由 ID 與 payload ID 不一致：`Update` 回 400。
- 缺少必填（標題／專案）或欄位超長（描述／摘要／與會人員）：DTO `DataAnnotations` 驗證失敗。
- 附件超過 1GB：前端提示並略過，後端再次驗證。
- 刪除時仍有關聯資料（FK 衝突）：回「此會議仍有關聯資料，無法刪除」。

## 七、驗收與測試

- `MyProject.Tests/PermissionCheckerTests.cs`：動作級授權涵蓋「會議項目」資源鍵。
- 團隊權控行為與「專案項目」共用 `TagStringHelper`／`RecordAccessScopeProvider` 機制，參考 `ProjectServiceTeamAccessTests.cs`（會議記錄未見獨立測試檔，標「未確認」）。

## 八、相關程式與文件

- `src/MyProject/MyProject.Web/Components/Pages/Projects/MeetingPage.razor:1`
- `src/MyProject/MyProject.Web/Components/Views/Projects/MeetingViewView.razor.cs:1`
- `src/MyProject/MyProject.Business/Services/DataAccess/MeetingService.cs:1`
- `src/MyProject/MyProject.Web/Controllers/MeetingController.cs:1`
- `src/MyProject/MyProject.AccessDatas/Models/Meeting.cs:1`、`MeetingFile.cs:1`
- `src/MyProject/MyProject.Share/Helpers/MagicObjectHelper.cs:32`
- 交叉連結：[Web API 設計慣例](../architecture/Web%20API%20設計慣例.md)、[檔案上傳機制](../features/檔案上傳機制.md)、[紀錄分類與團隊權控](紀錄分類與團隊權控-prd.md)、[專案項目](專案項目-prd.md)、[工作項目](工作項目-prd.md)
