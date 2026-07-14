# SQL Server 切換說明

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.23
- 首次實作版本：0.1.61
- 最後核對日期：2026/07/14

## 目標
- [x] 保留 SQLite 作為腳手架預設開發資料庫，同時提供切換 SQL Server 的實作方向。

## 現況
- [x] 目前 `Program.cs` 使用 `SystemSettings:ExternalFileSystem:DatabasePath` 組出 SQLite connection string。
- [x] `appsettings.json` 已保留 `SystemSettings:ConnectionStrings:DefaultConnection` 作為 SQL Server 連線字串範例。
- [x] 已實作 `SystemSettings:DatabaseProvider`，支援 `Sqlite` 與 `SqlServer`。
- [x] 已新增 `MyProject.AccessDatas.SqlServerMigrations` 作為 SQL Server 專用 migration assembly。

## 實作待辦
- [x] 新增 `SystemSettings:DatabaseProvider`，例如 `Sqlite` 或 `SqlServer`。
- [x] 將 EF Core 註冊抽出成 database service registration extension。
- [x] 當 provider 為 `SqlServer` 時使用 `UseSqlServer(DefaultConnection)`。
- [x] 將正式環境連線字串改由 secret 或部署平台環境變數提供，`appsettings.Production.json` 只保留空白 placeholder。
- [x] 建立獨立 SQL Server migration assembly，避免 SQLite 與 SQL Server migration 混用造成 schema 差異。

## 驗收標準
- [x] SQLite 預設建置與 integration tests 維持通過。
- [~] SQL Server provider 的實體資料庫驗證：**經決定不納入目前範圍**（見下方「SQL Server 遷移軌道」決定），日後如實作再於具 SQL Server 的環境驗證。
- [x] 文件明確列出 provider 切換、連線字串設定與 migration assembly 邊界。

## SQL Server 遷移軌道（經決定不納入目前實作範圍）

> **決定（2026/07/09）**：腳手架目前以 **SQLite 為預設且唯一受支援的執行資料庫**。SQL Server 遷移軌道的 bootstrap **經決定不納入目前實作範圍**（刻意不做、非遺漏）。`appsettings.Production.json` 雖保留 `DatabaseProvider=SqlServer` 與連線字串範例作為切換入口，但在補齊並驗證 migration 前，SQL Server 部署不受支援。日後如有實際 SQL Server 部署需求，再依下列範圍另案 bootstrap。

如日後實作，範圍與指令如下（保留供參考）：
- `MyProject.AccessDatas.SqlServerMigrations` 目前只有組件 marker、無任何 migration；需在具備 SQL Server 的環境一次補齊所有既有 schema 並驗證。
- 涵蓋範圍：`AddAccountLockout`、`AddAuditLog`、`AddTwoFactor`、`AddRbacTables`（`Permission`/`RolePermissionMap`/`UserRole`/`UserTeam`）等 SQLite 已有、SQL Server 尚缺的變更（實際以 `MyProject.AccessDatas/Migrations/` 累積的完整 schema 為準）。
- 產生指令範式：`dotnet ef migrations add <Name> --project src/MyProject/MyProject.AccessDatas.SqlServerMigrations --startup-project src/MyProject/MyProject.Web`（需先將 `SystemSettings:DatabaseProvider` 設為 `SqlServer` 並提供 `DefaultConnection`）。
- SQLite 與 SQL Server 的型別、索引、預設值與交易行為可能不同，屆時需做資料庫相容性測試。
