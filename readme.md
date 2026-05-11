# NET10-Blazor-Starter

一份基於 **.NET 10** 與 **Blazor Server**（全域 SSR）所建立的企業級應用程式樣板，預先整合 Ant Design Blazor、EF Core、Cookie 認證、角色權限、多語系、檔案上傳、Swagger 與 NLog，協助開發團隊以最低成本啟動內部管理類系統。

當前版本資訊定義在 [`src/MyProject/MyProject.Web/appsettings.json`](src/MyProject/MyProject.Web/appsettings.json) 之 `SystemSettings.SystemInformation.SystemVersion` 欄位。

---

## 1. 專案介紹

- **定位**：可立即啟動、預先配置、易於擴充的 Blazor Server 樣板。
- **適用情境**：管理後台、內部營運系統、專案/任務/會議追蹤平台。
- **設計理念**：分層清晰、慣例優先、模板可複刻（CRUD 模板可直接套用至新實體）。

---

## 2. 技術棧

| 類別 | 採用方案 | 版本 |
|------|----------|------|
| 框架 | .NET / ASP.NET Core | 10.0 |
| 前端 | Blazor Server（Interactive Server）+ 全域 SSR 渲染 | — |
| UI 元件庫 | AntDesign Blazor | 1.6.0 |
| 圖示 | BlazorMaterialIcons（Google Material Icons） | 0.0.1 |
| 資料存取 | Entity Framework Core | 10.0.5 |
| 預設資料庫 | SQLite（檔案位置由 `appsettings.json` 控制） | — |
| 正式資料庫 | SQL Server（可透過 `SystemSettings.DatabaseProvider` 切換） | — |
| 認證 | ASP.NET Core Cookie Authentication（含 RememberMe） | — |
| 授權 | RoleView + Menu.json 二維權限樹 | — |
| 多語系 | `RequestLocalization` + `AntDesignLocaleFactory` | zh-TW / en-US |
| 物件對映 | AutoMapper | — |
| API 文件 | Swashbuckle Swagger UI | 10.1.5 |
| 日誌 | NLog.Web.AspNetCore | 6.1.2 |

---

## 3. 系統架構速覽

```
MyProject.Web ──► MyProject.Business ──► MyProject.AccessDatas
       │                  │                       │
       └──► MyProject.Dtos / MyProject.Models / MyProject.Share
```

| 專案 | 角色 |
|------|------|
| `MyProject.Web` | Blazor Server 宿主、Razor 元件、Web API Controller、登入頁、本地化、靜態資源。 |
| `MyProject.Business` | 商業邏輯：Services、Repositories、AutoMapper Profile、Helpers。 |
| `MyProject.AccessDatas` | EF Core `BackendDBContext`、Entity 定義、Migrations。 |
| `MyProject.Models` | 系統設定、AdapterModel、共用領域模型。 |
| `MyProject.Dtos` | API 對外傳輸物件：`ApiResult<T>`、`PagedResult<T>`、各模組 DTO。 |
| `MyProject.Share` | 跨層共用：Helpers、Extensions（無外部相依）。 |

啟動流程詳見 [docs/架構總覽.md](docs/架構總覽.md)。

---

## 4. 主要功能

- 使用者帳號 CRUD（含預設開發者帳號自動 Seed）
- 角色管理（`RoleView`）與二維權限樹（對應 `Menu.json`）
- 登入 / 登出（Cookie 驗證、記住我、4 位數驗證碼、玻璃擬態 UI）
- 專案 / 工作 / 會議三大領域實體 CRUD
- 每筆紀錄可附加多檔案，自動依年月分目錄存放
- Web API（含 Swagger UI、`ApiResult<T>` 信封、分頁搜尋）
- 平行 API 路由：保留 `/api/...`，新增 `/api/v1/...` 作為新用戶端標準入口
- Health checks：`/health/live`、`/health/ready`
- Production 啟動安全檢查：JWT key、預設密碼與 Swagger 暴露策略需明確設定
- Sidebar 導覽：JSON 定義、可收合、自動套用使用者角色權限
- 多語系：以瀏覽器 `Accept-Language` 自動切換，AntDesign 元件本地化
- 全站請求耗時 / 例外統一寫入 NLog
- 靜態資源外部對應（`/UploadFiles` → 實體下載目錄）

---

## 5. 快速開始

> 需求：Windows 10 / 11、.NET 10 SDK、IDE（Visual Studio 2026 / Rider / VS Code）。

```powershell
# 1. 還原與編譯
cd src/MyProject
dotnet restore
dotnet build

# 2. 第一次跑（會自動建立目錄、套用 Migration、Seed 預設角色與帳號）
dotnet run --project MyProject.Web/MyProject.Web.csproj
```

預設開發者帳號 / 密碼定義於 `MagicObjectHelper.開發者帳號`（首次啟動會自動建立）。

外部目錄與資料庫檔位置由 `appsettings.json` 之 `SystemSettings.ExternalFileSystem` 控制，預設位於 `C:\temp\MyProject\…`，啟動時若不存在會自動建立。

關於 EF Core Migration 指令請見 [docs/EFCore.md](docs/EFCore.md)。

---

## 6. 專案結構

```
.
├── readme.md                       ← 本檔（系統入口說明）
├── AGENTS.md                       ← LLM 協作行為準則
├── docs/                           ← 系統設計與規範文件（見第 9 節）
└── src/MyProject/
    ├── MyProject.slnx              ← 方案檔（新版 .slnx 格式）
    ├── MyProject.Web/              ← Blazor Server 宿主
    │   ├── Components/             ← Pages / Views / Layout / Auths / Commons
    │   ├── Controllers/            ← Web API（Project / MyTask / Meeting）
    │   ├── Localization/           ← AntDesignLocaleFactory
    │   ├── Datas/Menu.json         ← Sidebar 導覽與權限定義
    │   ├── Filters/                ← ApiValidationFilter 等
    │   ├── Program.cs              ← 啟動主程式
    │   └── appsettings.json        ← 系統設定（含 SystemVersion）
    ├── MyProject.Business/
    │   ├── Services/DataAccess/    ← Domain Service（CRUD）
    │   ├── Services/Other/         ← AuthenticationStateHelper 等
    │   ├── Repositories/           ← API 層使用的 Repository
    │   └── Models/AutoMapping.cs   ← AutoMapper Profile
    ├── MyProject.AccessDatas/
    │   ├── BackendDBContext.cs
    │   ├── Models/                 ← Entity（MyUser、Project、MyTask、Meeting…）
    │   └── Migrations/
    ├── MyProject.Models/           ← AdapterModel、Systems、AutoMapper 來源
    ├── MyProject.Dtos/             ← API DTO（含 ApiResult/PagedResult）
    └── MyProject.Share/            ← Helpers、Extensions
```

---

## 7. 設定檔說明（`appsettings.json`）

| 區段 | 用途 |
|------|------|
| `Logging` | .NET 預設記錄等級設定（NLog 啟動後會接管實際輸出）。 |
| `NLog.BasePath` | NLog 寫入的根目錄；專案會在其下建立 `MyProject.Web` 子目錄並輸出檔案日誌。 |
| `SystemSettings.DatabaseProvider` | 資料庫 provider，支援 `Sqlite` 與 `SqlServer`，預設 `Sqlite`。 |
| `SystemSettings.ConnectionStrings.DefaultConnection` | SQL Server 連線字串；`DatabaseProvider=SqlServer` 時使用。 |
| `SystemSettings.ConnectionStrings.SQLiteDefaultConnection` | SQLite 連線範本；實際連線字串由 `MagicObjectHelper.GetSQLiteConnectionString` 結合 `DatabasePath` 產生。 |
| `SystemSettings.SystemInformation.SystemName` | 顯示用系統名稱。 |
| `SystemSettings.SystemInformation.SystemDescription` | 顯示用系統描述。 |
| **`SystemSettings.SystemInformation.SystemVersion`** | **唯一版本來源**，每次完成異動必須遞增（見第 8 節）。 |
| `SystemSettings.ExternalFileSystem.DatabasePath` | SQLite 資料庫檔放置目錄。 |
| `SystemSettings.ExternalFileSystem.DownloadPath` | `/UploadFiles` 對應的實體目錄（靜態資源外掛）。 |
| `SystemSettings.ExternalFileSystem.UploadPath` | 通用上傳暫存目錄。 |
| `SystemSettings.ExternalFileSystem.ProjectFilePath` | 專案附件根目錄（再依年/月細分）。 |
| `SystemSettings.ExternalFileSystem.TaskFilePath` | 工作附件根目錄。 |
| `SystemSettings.ExternalFileSystem.MeetingFilePath` | 會議附件根目錄。 |
| `AutoMapper:LicenseKey` | AutoMapper 商業授權金鑰（可留空）。 |

各區段詳解見 [docs/日誌與設定檔說明.md](docs/日誌與設定檔說明.md)。

---

## 8. 版本管理與維護規範（**重要**）

本專案採用單一版本來源策略。請務必遵守下列規範：

1. **每次更新完成必須 bump 版本**：
   將 `appsettings.json` 之 `SystemSettings.SystemInformation.SystemVersion` 的 patch 編號加 1，並把括號內日期更新為當天。

   ```json
   "SystemVersion": "0.1.69 (2026/05/11)"
   ```

   `SystemVersion` 為任何提交都應 bump 的最小單位。版號格式為 `Major.Minor.Patch (YYYY/MM/DD)`。

2. **影響到既有文件就必須同步更新**：
   只要本次異動會改變既有功能、模組、API、資料表、設定欄位或 UI 行為，必須同步修訂相關文件。對應表詳見 [docs/維護規範.md](docs/維護規範.md)。

3. **檔案編碼**：
   所有原始碼、設定、`.md` 文件一律使用 UTF-8 編碼（建議無 BOM）。提交前需自行確認繁體中文無亂碼。

4. **撰寫文件時請對應實際 codebase**：
   引用程式檔請使用 `相對路徑:行號` 格式，避免假設、推測或外部連結失效。

---

## 9. 文件索引

### 系統設計

- [架構總覽](docs/架構總覽.md) — 6 個專案分層、依賴方向、啟動流程、DI 註冊清單。
- [資料模型與資料庫](docs/資料模型與資料庫.md) — `BackendDBContext`、主要 Entity、關聯與刪除政策。
- [Web API 設計慣例](docs/Web%20API%20設計慣例.md) — Controller 樣板、`ApiResult<T>`、`PagedResult<T>`、Search DTO。
- [認證授權與權限機制](docs/認證授權與權限機制.md) — Cookie scheme、Claims、`RoleView` JSON、`Menu.json` 權限樹。
- [多語系與本地化](docs/多語系與本地化.md) — `RequestLocalization` 設定、`AntDesignLocaleFactory`、支援文化。
- [檔案上傳機制](docs/檔案上傳機制.md) — 三類附件、年月目錄、刪除同步、容量上限。
- [日誌與設定檔說明](docs/日誌與設定檔說明.md) — NLog 配置、各層級用法、`appsettings.json` 全表。

### 操作與規範

- [維護規範](docs/維護規範.md) — 版本 bump、文件同步、commit 前自我檢查清單。
- `scripts/New-StarterProject.ps1` — 從本腳手架複製新專案並替換 namespace / project 名稱。
- `scripts/New-CrudModule.ps1` — 產生新 CRUD 模組所需檔案骨架。
- [EFCore 指令備忘](docs/EFCore.md) — Migration 指令範本。
- [建立一個新 CRUD 操作網頁說明](docs/建立一個新%20CRUD%20操作網頁說明.md) — 以 `RoleViewView` 為藍本複刻新 CRUD 頁面。

### 變更紀錄

- [Login 頁面改版紀錄](docs/login-redesign.md) — 玻璃擬態登入頁、RememberMe、驗證碼導入紀錄。
- [記住我登入原理說明](docs/記住我登入原理說明.md) — Cookie + RememberMe 完整原理。

---

## 10. 參考連結

- AntDesign Blazor — https://antblazor.com/
- BlazorMaterialIcons — https://github.com/dimohy/BlazorMaterialIcons
- Google Material Icons — https://fonts.google.com/icons
- NLog — https://nlog-project.org/
- AutoMapper — https://automapper.org/

---

## 11. 授權與貢獻

本專案為內部樣板，請依團隊約定條款使用。提交異動前請確認已遵守第 8 節「版本管理與維護規範」全部要求。
