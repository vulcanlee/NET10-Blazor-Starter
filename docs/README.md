# 文件目錄索引

本目錄收錄 NET10-Blazor-Starter 的所有設計、規範與教學文件。文件依「特性」分類到下列子目錄；新增文件時請先依特性歸入既有分類，**若沒有任何分類適用，請自動新增一個語意明確的英文小寫分類目錄**，並同步更新本檔與主 [`readme.md`](../readme.md) 第 9 節「文件索引」。

所有 `.md` 一律 UTF-8 **含 BOM**，CI 以 `scripts/Test-DocsEncoding.ps1`（遞迴）強制檢查。

## 分類規則

| 目錄 | 收納特性 | 範例 |
|------|----------|------|
| [`planning/`](planning/) | 專案規劃、進度追蹤、TODO、路線圖 | 專案總覽、架構盤點、缺口與風險、補強路線圖 |
| [`architecture/`](architecture/) | 系統架構、資料模型、API / DTO 設計規範、開發慣例 | 開發慣例速查、架構總覽、資料模型、Web API 設計慣例、API Versioning |
| [`security/`](security/) | 認證、授權、登入、密碼與機密金鑰機制 | 認證授權、Google OAuth2、記住我、密碼儲存 |
| [`features/`](features/) | 個別功能機制說明 | 分散式快取、多語系、檔案上傳、健康監控 |
| [`guides/`](guides/) | 開發 / 操作教學、how-to、流程指南 | 新 CRUD 頁面、新專案啟動、EFCore、SQL Server 切換、測試 |
| [`operations/`](operations/) | 部署、維運、設定檔、上線檢查、CI/CD | 維護規範、部署安全清單、日誌與設定檔、CI-CD |
| [`changelog/`](changelog/) | 改版與變更紀錄 | 登入頁改版紀錄 |

## 各分類文件

### planning — 專案規劃與進度追蹤
- [01-專案總覽與定位-TODO](planning/01-專案總覽與定位-TODO.md)
- [02-現有架構盤點-TODO](planning/02-現有架構盤點-TODO.md)
- [03-缺口與風險清單-TODO](planning/03-缺口與風險清單-TODO.md)
- [04-WebAPI-JWT-ApiResult-設計-TODO](planning/04-WebAPI-JWT-ApiResult-設計-TODO.md)
- [05-腳手架補強實作路線圖-TODO](planning/05-腳手架補強實作路線圖-TODO.md)
- [06-開發與文件維護規範-TODO](planning/06-開發與文件維護規範-TODO.md)

### architecture — 系統架構與設計規範
- [開發慣例與限制速查（AI/開發者必讀）](architecture/開發慣例與限制速查.md)
- [架構總覽](architecture/架構總覽.md)
- [資料模型與資料庫](architecture/資料模型與資料庫.md)
- [DTO 與模型邊界規範](architecture/DTO%20與模型邊界規範.md)
- [Web API 設計慣例](architecture/Web%20API%20設計慣例.md)
- [API Versioning 策略](architecture/API%20Versioning%20策略.md)

### security — 認證、授權與安全
- [認證授權與權限機制](security/認證授權與權限機制.md)
- [Google OAuth2 第三方登入](security/Google%20OAuth2%20第三方登入.md)
- [記住我登入原理說明](security/記住我登入原理說明.md)
- [密碼種類與儲存機制](security/密碼種類與儲存機制.md)

### features — 功能機制
- [分散式快取機制](features/分散式快取機制.md)
- [多語系與本地化](features/多語系與本地化.md)
- [檔案上傳機制](features/檔案上傳機制.md)
- [系統健康監控](features/系統健康監控.md)

### guides — 開發與操作指南
- [建立一個新 CRUD 操作網頁說明](guides/建立一個新%20CRUD%20操作網頁說明.md)
- [腳手架新專案啟動流程](guides/腳手架新專案啟動流程.md)
- [EFCore 指令備忘](guides/EFCore.md)
- [SQL Server 切換說明](guides/SQL%20Server%20切換說明.md)
- [測試指南](guides/測試指南.md)

> 腳手架腳本：`scripts/New-StarterProject.ps1`（複製新專案並替換 namespace / project 名稱）、`scripts/New-CrudModule.ps1`（產生新 CRUD 模組骨架）。

### operations — 維運與部署
- [維護規範](operations/維護規範.md)
- [正式部署與安全檢查清單](operations/正式部署與安全檢查清單.md)
- [日誌與設定檔說明](operations/日誌與設定檔說明.md)
- [CI-CD 與品質檢查](operations/CI-CD與品質檢查.md)

### changelog — 變更紀錄
- [Login 頁面改版紀錄](changelog/login-redesign.md)
- [抑制 SQLite 已知弱點 CVE-2025-6965（0.2.9）](changelog/2026-06-22-抑制SQLite-CVE-2025-6965.md)
- [新增「分類清單」與「團隊清單」管理頁面（0.3.0）](changelog/2026-06-22-分類與團隊清單.md)
