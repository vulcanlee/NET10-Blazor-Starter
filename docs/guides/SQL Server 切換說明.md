# SQL Server 切換說明

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
- [ ] SQL Server provider 需在具備 SQL Server 的本機或 CI integration environment 再做實體資料庫驗證。
- [x] 文件明確列出 provider 切換、連線字串設定與 migration assembly 邊界。

## 備註風險
- [ ] SQLite 與 SQL Server 的型別、索引、預設值與交易行為可能不同，正式切換前需做資料庫相容性測試。
