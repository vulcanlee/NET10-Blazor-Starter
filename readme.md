# NET10-Blazor-Starter

一份基於 **.NET 10** 與 **Blazor Server**（全域 SSR）所建立的企業級應用程式樣板，預先整合 Ant Design Blazor、EF Core、Cookie 認證、角色權限、多語系、檔案上傳、Swagger 與 NLog，協助開發團隊以最低成本啟動內部管理類系統。

當前版本資訊定義在 [`src/MyProject/MyProject.Web/appsettings.json`](src/MyProject/MyProject.Web/appsettings.json) 之 `SystemSettings.SystemInformation.SystemVersion` 欄位。

---

## 1. 專案介紹

- **定位**：可立即啟動、預先配置、易於擴充的 Blazor Server 樣板。
- **適用情境**：管理後台、內部營運系統、專案追蹤平台。
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
| 資料庫 | SQLite（唯一支援；檔案位置由 `appsettings.json` 控制） | — |
| 認證 | ASP.NET Core Cookie Authentication（含 RememberMe） | — |
| 授權 | 宣告式 RBAC：`Menu.json` id → 權限鍵 → `IPermissionChecker`（動作級 `view/create/edit/delete/export`）| — |
| 多語系 | `RequestLocalization` + `AntDesignLocaleFactory` | zh-TW / en-US |
| 物件對映 | AutoMapper | 16.1.1 |
| API 文件 | Swashbuckle Swagger UI | 10.2.3 |
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

啟動流程詳見 [docs/architecture/架構總覽.md](docs/architecture/架構總覽.md)。

---

## 4. 主要功能

- 使用者帳號 CRUD（含預設開發者帳號自動 Seed）
- 角色管理（`RoleView`）與二維權限樹（對應 `Menu.json`）
- 登入 / 登出（Cookie 驗證、記住我、4 位數驗證碼、玻璃擬態 UI）
- 專案領域實體 CRUD（可作為新增其他領域模組的樣板）
- 資料定義主資料：分類清單（Category）、團隊清單（Team）管理頁面與 Web API
  （0.4.40 起分類可指定適用團隊、下拉依使用者所屬團隊過濾；0.4.41 起名稱唯一性由資料庫唯一索引保證）
- 紀錄分類/團隊標籤：專案可標記分類與團隊，並支援團隊行級權控（非管理員僅見公開或團隊交集紀錄）；
  使用者的有效團隊＝直接綁定的 `UserTeam` ∪ 其角色的 `DefaultTeamsJson`
- 每筆紀錄可附加多檔案，自動依年月分目錄存放
- Web API（含 Swagger UI、`ApiResult<T>` 信封、分頁搜尋）
- 平行 API 路由：保留 `/api/...`，新增 `/api/v1/...` 作為新用戶端標準入口
- Health checks：`/health/live`、`/health/ready`
- 系統健康監控頁：`/system-health`，管理員可查看健康百分比、紅黃綠燈號與最後 100 筆日誌
- 日誌檢視頁：`/logs`，管理員可依等級／關鍵字／時間區間查詢並匯出（0.4.26）
- 資料庫用量頁：`/database-usage`，管理員可查看各資料表筆數與估算用量（0.4.28）
- 日誌等級設定頁：`/log-level-setting`，管理員可在執行期調整日誌等級（0.4.29）
- API 安全基礎設施：依呼叫端分割的速率限制、安全回應標頭、上傳副檔名白名單（0.4.35）
- 分散式快取：`ICacheService` 統一抽象，透過 `appsettings.json` 在 Memory ↔ Redis 間切換（側邊選單已套用）
- Production 啟動安全檢查：JWT key、預設密碼、Swagger 暴露策略與 Redis 連線字串需明確設定
- Sidebar 導覽：JSON 定義、可收合、自動套用使用者角色權限
- 右上角使用者選單「關於」對話窗：顯示系統名稱／描述／版本、執行環境、.NET 版本、啟動與已運作時間
- 多語系：以瀏覽器 `Accept-Language` 自動切換，AntDesign 元件本地化
- 全站請求耗時 / 例外統一寫入 NLog
- 靜態資源外部對應（`/UploadFiles` → 實體下載目錄；**0.4.35 起需登入**才可取用）

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

預設帳號 / 密碼由 `appsettings.json` 之 `BootstrapSettings`（`SupportAccount` / `SupportPassword`）定義，首次啟動會自動建立 `support` 管理員帳號；`MagicObjectHelper.開發者帳號` 則用於後續識別並保護該開發者帳號。

外部目錄與資料庫檔位置由 `appsettings.json` 之 `SystemSettings.ExternalFileSystem` 控制，預設位於 `C:\temp\MyProject\…`，啟動時若不存在會自動建立。

關於 EF Core Migration 指令請見 [docs/guides/EFCore.md](docs/guides/EFCore.md)。

> **第一次接觸本專案，或要用本腳手架開新系統**，請改讀 [VS Code 開發環境與新專案上手指南](docs/guides/VS%20Code%20開發環境與新專案上手指南.md) —— 涵蓋必備工具、VS Code 啟動與偵錯、User Secrets 機密設定、複製後的完整更名步驟與驗證清單。

---

## 6. 專案結構

```
.
├── readme.md                       ← 本檔（系統入口說明）
├── AGENTS.md / CLAUDE.md           ← LLM 協作行為準則與專案速查入口
├── .editorconfig                   ← 程式碼格式規則（CI 以 dotnet format 強制）
├── global.json                     ← 鎖定 .NET SDK 版本
├── .vscode/                        ← VS Code 啟動／任務／編輯器設定與建議擴充套件
├── docs/                           ← 系統設計與規範文件（依特性分類，見第 9 節）
│   ├── README.md                   ← 文件目錄索引與分類規則
│   ├── planning/                   ← 專案規劃、TODO、路線圖
│   ├── architecture/               ← 架構、資料模型、API/DTO 規範、開發慣例速查
│   ├── security/                   ← 認證、授權、密碼與機密金鑰
│   ├── features/                   ← 個別功能機制（快取、多語系、上傳、健康監控）
│   ├── guides/                     ← 開發/操作教學（VS Code 上手、CRUD、EFCore、測試）
│   ├── operations/                 ← 維護、部署、設定檔、CI/CD
│   ├── prd/                        ← 產品需求文件（能力覆蓋矩陣 + 各能力 PRD）
│   ├── superpowers/                ← brainstorming 流程產出的設計規格
│   └── changelog/                  ← 變更紀錄
├── scripts/                        ← New-StarterProject.ps1 / New-CrudModule.ps1 / Test-DocsEncoding.ps1
├── .github/workflows/              ← CI（build / format / test / 文件編碼 / 弱點掃描）
└── src/MyProject/
    ├── MyProject.slnx              ← 方案檔（新版 .slnx 格式）
    ├── Directory.Build.props       ← 共用建置屬性（Nullable、TreatWarningsAsErrors）
    ├── Directory.Packages.props    ← 套件版本單一來源（Central Package Management）
    ├── MyProject.Web/              ← Blazor Server 宿主
    │   ├── Components/             ← Pages / Views / Layout / Auths / Commons / Dialogs
    │   ├── Controllers/            ← Web API（Project / Category / Team / Auth …）
    │   ├── Extensions/             ← ★ 服務與中介軟體註冊（AddApplicationServices 等）
    │   ├── Configuration/          ← 強型別設定（CacheSettings、RateLimitSettings …）
    │   ├── Auth/                   ← JwtTokenService、RecordAccessScopeProvider
    │   ├── Health/                 ← 健康檢查與計分
    │   ├── wwwroot/                ← 靜態資源
    │   ├── nlog.config
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
    │   ├── Models/                 ← Entity（MyUser、Project、Category、Team…）
    │   └── Migrations/
    ├── MyProject.Models/           ← AdapterModel、Systems、AutoMapper 來源
    ├── MyProject.Dtos/             ← API DTO（含 ApiResult/PagedResult）
    ├── MyProject.Share/            ← Helpers、Extensions
    └── MyProject.Tests/            ← xUnit 測試（含慣例守門測試）
```

---

## 7. 設定檔說明（`appsettings.json`）

| 區段 | 用途 |
|------|------|
| `AllowedHosts` | 允許的 Host 標頭白名單，預設 `*`。 |
| `Logging` | .NET 預設記錄等級設定（NLog 啟動後會接管實際輸出）。 |
| `Swagger.EnabledInProduction` | 是否在 Production 環境暴露 Swagger UI，預設 `false`。 |
| `Security.ReturnExceptionDetails` | 是否於 `ApiResult.Exception` 回傳例外細節；`null` 時依環境決定。 |
| `Cors.AllowedOrigins` | CORS 允許來源白名單（陣列）；留空表示不額外開放跨來源。 |
| `RateLimit.ApiRequestsPerMinute` | 一般 API 每分鐘配額，**每個呼叫端各自計算**，預設 `120`。 |
| `RateLimit.LoginRequestsPerMinute` | 登入端點每分鐘配額，預設 `10`（比一般 API 嚴格）。 |
| `ForwardedHeaders.KnownProxies` / `KnownNetworks` | **預設未寫入 `appsettings.json`**（程式面支援，未設定時採空白預設）。信任的反向代理 IP／網段；走代理時必設，否則來源 IP 會全部變成代理伺服器且 `X-Forwarded-For` 可被偽造。 |
| `CacheSettings.Provider` | 快取 provider，支援 `Memory` 與 `Redis`，預設 `Memory`（見 [分散式快取機制](docs/features/分散式快取機制.md)）。 |
| `CacheSettings.RedisConnection` | `Provider=Redis` 時的 Redis 連線字串；Production 使用 Redis 時必須設定。 |
| `CacheSettings.InstanceName` | Redis 快取鍵前綴，預設 `MyProject:`。 |
| `CacheSettings.DefaultExpirationMinutes` | 快取項目預設存活時間（分鐘），預設 `30`。 |
| `NLog.BasePath` | NLog 寫入的根目錄；專案會在其下建立 `MyProject.Web` 子目錄並輸出檔案日誌。 |
| `JwtSettings` | Web API JWT 設定：`Issuer`、`Audience`、`SigningKey`、`AccessTokenMinutes`、`RefreshTokenDays`、`ClockSkewMinutes`；Production 啟動時若仍為開發用 `SigningKey` 會中止啟動。 |
| `BootstrapSettings` | 預設 `support` 帳號種子設定：`SupportAccount` / `SupportName` / `SupportEmail` / `SupportPassword`（首次啟動建立，重啟時更新密碼）。 |
| `GoogleOAuthSettings` | Google OAuth2 第三方登入：`Enabled`、`ClientId`、`ClientSecret`、`DefaultRoleName`（見 [Google OAuth2 第三方登入](docs/security/Google%20OAuth2%20第三方登入.md)）。 |
| `SystemSettings.ConnectionStrings.SQLiteDefaultConnection` | SQLite 連線範本；實際連線字串由 `MagicObjectHelper.GetSQLiteConnectionString` 結合 `DatabasePath` 產生。 |
| `SystemSettings.SystemInformation.SystemName` | 顯示用系統名稱。 |
| `SystemSettings.SystemInformation.SystemDescription` | 顯示用系統描述。 |
| **`SystemSettings.SystemInformation.SystemVersion`** | **唯一版本來源**，每次完成異動必須遞增（見第 8 節）。 |
| `SystemSettings.ExternalFileSystem.DatabasePath` | SQLite 資料庫檔放置目錄。 |
| `SystemSettings.ExternalFileSystem.DownloadPath` | `/UploadFiles` 對應的實體目錄（靜態資源外掛）。 |
| `SystemSettings.ExternalFileSystem.UploadPath` | 通用上傳暫存目錄。 |
| `SystemSettings.ExternalFileSystem.ProjectFilePath` | 專案附件根目錄（再依年/月細分）。 |
| `SystemSettings.Upload.AllowedExtensions` | **預設未寫入 `appsettings.json`**。允許上傳的副檔名白名單（陣列）；留空採用 `UploadFileTypePolicy` 內建預設（不含 `.html`/`.svg`/`.exe` 等）。 |
| `AutoMapper:LicenseKey` | AutoMapper 商業授權金鑰（可留空）。 |

各區段詳解見 [docs/operations/日誌與設定檔說明.md](docs/operations/日誌與設定檔說明.md)。

---

## 8. 版本管理與維護規範（**重要**）

本專案採用單一版本來源策略。請務必遵守下列規範：

1. **每次更新完成必須 bump 版本**：
   每次異動一律將 `appsettings.json` 之 `SystemSettings.SystemInformation.SystemVersion` 的**最後一碼（Patch）+1**（例：`0.4.0 → 0.4.1`，不進位、不依異動性質區分），並把括號內日期更新為當天。

   ```json
   "SystemVersion": "0.4.1 (2026/06/22)"
   ```

   `SystemVersion` 為任何提交都應 bump 的最小單位。版號顯示格式為 `Major.Minor.Patch (YYYY/MM/DD)`；遞增規則詳見 [docs/operations/維護規範.md](docs/operations/維護規範.md) 第 1.2 節。

2. **影響到既有文件就必須同步更新**：
   只要本次異動會改變既有功能、模組、API、資料表、設定欄位或 UI 行為，必須同步修訂相關文件。對應表詳見 [docs/operations/維護規範.md](docs/operations/維護規範.md)。

3. **檔案編碼**：
   `docs/` 下所有 `.md` 一律使用 **UTF-8 含 BOM**（CI 以 [`scripts/Test-DocsEncoding.ps1`](scripts/Test-DocsEncoding.ps1) 遞迴強制，缺 BOM 或含亂碼即失敗）；其餘原始碼、設定檔採 UTF-8 即可。提交前需自行確認繁體中文無亂碼。

4. **撰寫文件時請對應實際 codebase**：
   引用程式檔請使用 `相對路徑:行號` 格式，避免假設、推測或外部連結失效。

---

## 9. 文件索引

完整分類規則與清單見 [docs/README.md](docs/README.md)；新增文件請先依特性歸入既有子目錄，無適用分類時自動新增英文小寫目錄並同步該索引與本節。

### 架構與設計（architecture）

- [開發慣例與限制速查](docs/architecture/開發慣例與限制速查.md) — **AI/開發者必讀**：分層、SQLite migration、追蹤清理、權限同步等不變量速查。
- [架構總覽](docs/architecture/架構總覽.md) — 6 個專案分層、依賴方向、啟動流程、DI 註冊清單。
- [資料模型與資料庫](docs/architecture/資料模型與資料庫.md) — `BackendDBContext`、主要 Entity、關聯與刪除政策。
- [DTO 與模型邊界規範](docs/architecture/DTO%20與模型邊界規範.md) — API / UI / Business / Entity 資料邊界原則與新 CRUD 模組待辦。
- [Web API 設計慣例](docs/architecture/Web%20API%20設計慣例.md) — Controller 樣板、`ApiResult<T>`、`PagedResult<T>`、Search DTO。
- [Web API 端點目錄](docs/architecture/Web%20API%20端點目錄.md) — 全部 controller 的實際路由、授權與回傳型別對照表。
- [API Versioning 策略](docs/architecture/API%20Versioning%20策略.md) — `/api/...` 與 `/api/v1/...` 平行路由、Swagger v1 分組與後續導入策略。

### 認證與安全（security）

- [認證授權與權限機制](docs/security/認證授權與權限機制.md) — Cookie / JWT 雙 scheme、Claims、RBAC 關聯表、動作級授權與稽核事件。
- [密碼種類與儲存機制](docs/security/密碼種類與儲存機制.md) — 密碼種類盤點、`MyUser.Password` 雜湊、API 密碼、種子密碼與機密金鑰。
- [Google OAuth2 第三方登入](docs/security/Google%20OAuth2%20第三方登入.md) — Google SSO 設定、自動建帳與審核、串接權控與 API（JWT）。
- [記住我登入原理說明](docs/security/記住我登入原理說明.md) — Cookie + RememberMe 完整原理。
- [權限授權現況評估與改善路線](docs/security/權限授權現況評估與改善路線.md) — RBAC 落地歷程、重構前後對照與尚未納入的項目。

### 功能機制（features）

- [分散式快取機制](docs/features/分散式快取機制.md) — `ICacheService`、Memory ↔ Redis 切換、選單快取與失效行為。
- [多語系與本地化](docs/features/多語系與本地化.md) — `RequestLocalization` 設定、`AntDesignLocaleFactory`、支援文化。
- [檔案上傳機制](docs/features/檔案上傳機制.md) — 專案附件、年月目錄、刪除同步、1GB 上限與副檔名白名單（0.4.35）。
- [系統健康監控](docs/features/系統健康監控.md) — 健康百分比、紅黃綠燈號、部署探針與最後 100 筆日誌。

### 開發與操作指南（guides）

- [VS Code 開發環境與新專案上手指南](docs/guides/VS%20Code%20開發環境與新專案上手指南.md) — **新手入口**：必備工具、VS Code 啟動與偵錯、User Secrets 機密設定、複製腳手架的完整更名步驟、驗證清單與疑難排解。
- [建立一個新 CRUD 操作網頁說明](docs/guides/建立一個新%20CRUD%20操作網頁說明.md) — 新模組請先跑 `scripts/New-CrudModule.ps1`；本文為手動微調時的對照說明。
- [腳手架新專案啟動流程](docs/guides/腳手架新專案啟動流程.md) — 從本腳手架複製成新系統的改名與設定檢查清單（操作細節見上方上手指南）。
- [EFCore 指令備忘](docs/guides/EFCore.md) — Migration 指令範本。
- [測試指南](docs/guides/測試指南.md) — 測試類別、本機執行、整合測試與覆蓋率。
- `scripts/New-StarterProject.ps1` — 從本腳手架複製新專案並替換 namespace / project 名稱。
- `scripts/New-CrudModule.ps1` — 產生新 CRUD 模組（13 個檔案，對齊現行慣例；產出附 README 列出註冊步驟）。

### 維運與部署（operations）

- [維護規範](docs/operations/維護規範.md) — 版本 bump、文件同步、commit 前自我檢查清單。
- [正式部署與安全檢查清單](docs/operations/正式部署與安全檢查清單.md) — 上線前 JWT、預設帳號、Swagger、例外揭露等必查項目。
- [日誌與設定檔說明](docs/operations/日誌與設定檔說明.md) — NLog 配置、各層級用法、`appsettings.json` 全表。
- [CI-CD 與品質檢查](docs/operations/CI-CD與品質檢查.md) — GitHub Actions 流程、文件編碼檢查、弱點掃描。

### 產品需求文件（prd）

- [PRD 主控台（能力覆蓋矩陣）](docs/prd/README.md) — 以產品能力為單位的單一入口：能力→入口→程式來源→狀態。
- 各能力 PRD 一律由上方主控台的**能力覆蓋矩陣**索引，避免此處與 `docs/prd/README.md` 長期不同步。

### 設計規格（superpowers）

- [docs/superpowers/](docs/superpowers/) — 以 brainstorming 流程產出的設計規格（分類/團隊頁面、紀錄權控）。

### 變更紀錄（changelog）

- [docs/changelog/](docs/changelog/README.md) — 全部改版紀錄的單一索引（依版本新→舊）。
  此處刻意不重複列舉，避免三處索引長期不同步。

## 10. 參考連結

- AntDesign Blazor — https://antblazor.com/
- BlazorMaterialIcons — https://github.com/dimohy/BlazorMaterialIcons
- Google Material Icons — https://fonts.google.com/icons
- NLog — https://nlog-project.org/
- AutoMapper — https://automapper.org/

---

## 11. 授權與貢獻

本專案為內部樣板，請依團隊約定條款使用。提交異動前請確認已遵守第 8 節「版本管理與維護規範」全部要求。
