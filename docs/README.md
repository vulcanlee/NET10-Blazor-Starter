# 文件目錄索引

- 文件版本：1.2
- 文件狀態：維護中
- 現行系統版本：0.4.41
- 首次實作版本：0.2.8
- 最後核對日期：2026/08/26

本目錄收錄 NET10-Blazor-Starter 的所有設計、規範與教學文件。文件依「特性」分類到下列子目錄；新增文件時請先依特性歸入既有分類，**若沒有任何分類適用，請自動新增一個語意明確的英文小寫分類目錄**，並同步更新本檔與主 [`readme.md`](../readme.md) 第 9 節「文件索引」。

所有 `.md` 一律 UTF-8 **含 BOM**，CI 以 `scripts/Test-DocsEncoding.ps1`（遞迴）強制檢查。

## 分類規則

| 目錄 | 收納特性 | 範例 |
|------|----------|------|
| [`planning/`](planning/) | 專案規劃、進度追蹤、TODO、路線圖 | 專案總覽、架構盤點、缺口與風險、補強路線圖 |
| [`architecture/`](architecture/) | 系統架構、資料模型、API / DTO 設計規範、開發慣例 | 開發慣例速查、架構總覽、資料模型、Web API 設計慣例、API Versioning |
| [`security/`](security/) | 認證、授權、登入、密碼與機密金鑰機制 | 認證授權、Google OAuth2、記住我、密碼儲存 |
| [`features/`](features/) | 個別功能機制說明 | 分散式快取、多語系、檔案上傳、健康監控 |
| [`guides/`](guides/) | 開發 / 操作教學、how-to、流程指南 | 新 CRUD 頁面、新專案啟動、EFCore、測試 |
| [`operations/`](operations/) | 部署、維運、設定檔、上線檢查、CI/CD | 維護規範、部署安全清單、日誌與設定檔、CI-CD |
| [`prd/`](prd/) | 產品需求文件（功能 PRD）| PRD 主控台、各能力 PRD |
| [`superpowers/`](superpowers/) | brainstorming 設計流程產出的規格 | 分類/團隊頁面設計、紀錄權控設計 |
| [`changelog/`](changelog/) | 改版與變更紀錄 | 登入頁改版紀錄 |

> 每個子目錄都有自己的 `README.md` 作為該目錄索引；點上方分類連結即可進入並看到該目錄清單。

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
- [Web API 端點目錄](architecture/Web%20API%20端點目錄.md)
- [API Versioning 策略](architecture/API%20Versioning%20策略.md)

### security — 認證、授權與安全
- [權限授權現況評估與改善路線](security/權限授權現況評估與改善路線.md)
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
- [測試指南](guides/測試指南.md)

> 腳手架腳本：`scripts/New-StarterProject.ps1`（複製新專案並替換 namespace / project 名稱）、`scripts/New-CrudModule.ps1`（產生新 CRUD 模組骨架）。

### operations — 維運與部署
- [維護規範](operations/維護規範.md)
- [正式部署與安全檢查清單](operations/正式部署與安全檢查清單.md)
- [日誌與設定檔說明](operations/日誌與設定檔說明.md)
- [CI-CD 與品質檢查](operations/CI-CD與品質檢查.md)

### prd — 產品需求文件
- [PRD 主控台（能力覆蓋矩陣）](prd/README.md)
- [首頁與導覽 PRD](prd/首頁與導覽-prd.md)
- [登入與帳號流程 PRD](prd/登入與帳號流程-prd.md)
- [專案項目 PRD](prd/專案項目-prd.md)
- [使用者管理 PRD](prd/使用者管理-prd.md)
- [角色管理 PRD](prd/角色管理-prd.md)
- [分類清單 PRD](prd/分類清單-prd.md)
- [團隊清單 PRD](prd/團隊清單-prd.md)
- [系統健康監控 PRD](prd/系統健康監控-prd.md)
- [紀錄分類與團隊權控 PRD](prd/紀錄分類與團隊權控-prd.md)

### superpowers — 設計規格
- [分類清單 / 團隊清單管理頁面（階段一）](superpowers/specs/2026-06-22-category-team-pages-design.md)
- [紀錄分類/團隊標籤與團隊權控（階段二）](superpowers/specs/2026-06-22-record-tags-team-access-design.md)

### changelog — 變更紀錄
- [Login 頁面改版紀錄](changelog/login-redesign.md)
- [抑制 SQLite 已知弱點 CVE-2025-6965（0.2.9）](changelog/2026-06-22-抑制SQLite-CVE-2025-6965.md)
- [新增「分類清單」與「團隊清單」管理頁面（0.3.0）](changelog/2026-06-22-分類與團隊清單.md)
- [紀錄分類/團隊標籤與團隊權控（0.4.0）](changelog/2026-06-22-紀錄分類團隊與權控.md)
- [版本號規則調整為每次異動 Patch +1（0.4.1）](changelog/2026-06-22-版本號規則調整.md)
- [移植母專案通用型改善（0.4.2）](changelog/2026-06-22-通用型改善移植.md)
- [側邊欄收合飛出 hover 修正與日誌補缺（0.4.3）](changelog/2026-06-22-側邊欄收合修正與日誌補缺.md)
- [側邊欄群組圖示依名稱各自顯示（0.4.4）](changelog/2026-06-22-側邊欄群組圖示.md)
- [階段四 UI 實跑驗收（0.4.21，無版本變更）](changelog/2026-07-09-階段四UI實跑驗收.md)
- [選單/CheckAccessPage 改用 IPermissionChecker（0.4.22）](changelog/2026-07-09-選單改用IPermissionChecker.md)
- [階段五：文件收尾（RBAC 落地後文件對齊，0.4.22，無版本變更）](changelog/2026-07-09-階段五文件收尾.md)
- [稽核事件擴充：使用者/角色/權限異動（0.4.23）](changelog/2026-07-09-稽核事件擴充.md)
- [決定紀錄：三項待辦經決定不實作（0.4.23，無版本變更）](changelog/2026-07-09-三項待辦決定不實作.md)
- [移除工作項目、會議記錄與 SQL Server 支援，新增「關於」對話窗（0.4.24）](changelog/2026-08-17-移除工作項目會議記錄與MSSQL支援.md)
- [更換品牌圖片與網站圖示，品牌名稱收斂為單一來源（0.4.25）](changelog/2026-08-25-品牌圖片與網站圖示更換.md)
- [選單順序調整與新增日誌檢視頁面（0.4.26）](changelog/2026-08-25-選單重排與日誌檢視頁面.md)
- [統一按鈕圖示慣例：工具列 emoji 改用 MaterialIcon（0.4.27）](changelog/2026-08-25-按鈕圖示慣例統一.md)
- [新增資料庫用量頁面（0.4.28）](changelog/2026-08-25-資料庫用量頁面.md)
- [新增日誌等級設定頁面（0.4.29）](changelog/2026-08-25-日誌等級設定頁面.md)
- [日誌可觀測性補強（0.4.30）](changelog/2026-08-25-日誌可觀測性補強.md)
- [修正專案附件下載 404：補上缺少的端點（0.4.31）](changelog/2026-08-25-專案附件下載端點.md)
- [建立工程品質關卡（0.4.32）](changelog/2026-08-26-工程品質關卡.md)
- [權限一致性修正與四方守門測試（0.4.33）](changelog/2026-08-26-權限一致性修正.md)
- [API 安全缺陷修正：例外外洩、停用帳號、預設拒絕（0.4.34）](changelog/2026-08-26-API安全缺陷修正.md)
- [API 安全基礎設施補強：限流分割、安全標頭、上傳白名單（0.4.35）](changelog/2026-08-26-API安全基礎設施補強.md)
- [Blazor 路徑改用 IDbContextFactory，CleanTrackingHelper 退場（0.4.36）](changelog/2026-08-26-DbContextFactory遷移.md)
- [限流實跑驗證修正：政策被覆蓋與 429 信封（0.4.37）](changelog/2026-08-26-限流實跑驗證修正.md)
- [CRUD 樣板收斂與產生器重寫（0.4.38）](changelog/2026-08-26-CRUD樣板收斂與產生器重寫.md)
- [修正日誌查詢會跳過「正在寫入」的檔案（0.4.39）](changelog/2026-08-26-日誌查詢跳過當前檔案修正.md)
- [分類綁定團隊與儲存前團隊確認（0.4.40）](changelog/2026-08-26-分類綁定團隊.md)
- [分類／團隊名稱唯一性補強（0.4.41）](changelog/2026-08-26-名稱唯一性補強.md)
