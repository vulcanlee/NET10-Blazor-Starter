# 專案總覽與定位 TODO

## 目標說明

- [ ] 將本專案定位為未來開發系統時可重複使用的 Blazor Web 通用腳手架。
- [ ] 保留 Web 後台、Cookie 登入、角色權限、CRUD 範例、SQLite、NLog、Swagger 與檔案處理等基礎能力。
- [ ] 補齊 Web API 與 JWT Bearer 認證能力，使此腳手架可同時支援瀏覽器 UI 與外部 API 呼叫。
- [ ] 建立繁體中文文件入口，讓後續開發者能快速理解專案目的、功能邊界與補強方向。

## 現況盤點

- [ ] 專案目前以 `.NET 10`、Blazor Web、AntDesign、BlazorMaterialIcons 建立後台式 Web 應用骨架。
- [ ] 方案內已有 `MyProject.Web`、`MyProject.Business`、`MyProject.Models`、`MyProject.AccessDatas`、`MyProject.Share` 分層。
- [ ] 目前已具備 Cookie 登入、登出、目前使用者狀態、Sidebar 權限過濾與角色管理。
- [ ] 目前已具備 RoleView、MyUser、Project、MyTas、Meeting 等 CRUD 範例。
- [ ] 目前已具備 EF Core SQLite、migration、自動套用 migration 與預設資料初始化。
- [ ] 目前已具備 NLog、Swagger UI、檔案上傳與檔案下載端點。
- [ ] 目前仍帶有明確範例領域：專案、工作、會議；需在文件中標示這些是可參考、可移除、可替換的範例功能。

## 實作待辦

- [ ] 將 README 改寫為清楚的專案入口，指向本 TODO 文件組。
- [ ] 將「通用腳手架核心」與「範例功能」分開說明。
- [ ] 補上專案啟動流程：還原套件、建置、執行、登入、預設資料、SQLite 檔案位置。
- [ ] 補上預設帳號與密碼策略說明，並標示正式環境不可沿用預設帳號。
- [ ] 補上此腳手架適合的使用情境：內部系統、管理後台、資料維護系統、具有外部 API 的 Web 系統。
- [ ] 補上此腳手架不預設包含的項目：多租戶、完整 OAuth/OIDC Provider、背景排程、訊息佇列、雲端部署腳本。
- [ ] 補上新系統從此腳手架開始時的改名流程與專案命名規範。

## 驗收標準

- [ ] 新進開發者閱讀文件後，可在 30 分鐘內說明此專案用途與主要分層。
- [ ] 文件能清楚指出哪些功能是核心腳手架，哪些功能是範例 CRUD。
- [ ] 文件能清楚指出未來新增系統時，應先調整系統名稱、設定、資料庫路徑、預設帳號與 API 設定。
- [ ] 文件無亂碼，且以 UTF-8 BOM 儲存。

## 相關檔案

- [ ] `readme.md`
- [ ] `docs/`
- [ ] `src/MyProject/MyProject.slnx`
- [ ] `src/MyProject/MyProject.Web/Program.cs`
- [ ] `src/MyProject/MyProject.Web/appsettings.json`
- [ ] `src/MyProject/.github/copilot-instructions.md`

## 備註風險

- [ ] 若未分清楚核心骨架與範例功能，未來新系統可能會不必要地繼承 Project/MyTas/Meeting 領域模型。
- [ ] 若 README 持續混合舊說明與 TODO，開發者可能無法判斷哪些功能已完成、哪些仍待實作。
- [ ] 若預設帳號與預設密碼未明確標示，正式環境部署可能產生重大安全風險。

