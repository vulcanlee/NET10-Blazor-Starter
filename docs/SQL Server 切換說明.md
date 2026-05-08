# SQL Server 切換說明

## 目標
- [x] 保留 SQLite 作為腳手架預設開發資料庫，同時提供切換 SQL Server 的實作方向。

## 現況
- [x] 目前 `Program.cs` 使用 `SystemSettings:ExternalFileSystem:DatabasePath` 組出 SQLite connection string。
- [x] `appsettings.json` 已保留 `SystemSettings:ConnectionStrings:DefaultConnection` 作為 SQL Server 連線字串範例。
- [ ] 尚未實作以設定值切換 provider 的正式機制。

## 實作待辦
- [ ] 新增 `SystemSettings:DatabaseProvider`，例如 `Sqlite` 或 `SqlServer`。
- [ ] 將 EF Core 註冊抽出成 database service registration extension。
- [ ] 當 provider 為 `SqlServer` 時使用 `UseSqlServer(DefaultConnection)`。
- [ ] 將正式環境連線字串改由 secret 或部署平台環境變數提供。
- [ ] 建立 migration 操作說明，避免 SQLite 與 SQL Server migration 混用造成 schema 差異。

## 驗收標準
- [ ] SQLite 預設建置與 integration tests 維持通過。
- [ ] SQL Server provider 可在本機或 CI integration environment 建立資料庫並套用 migration。
- [ ] 文件明確列出 provider 切換、連線字串設定、migration 與 rollback 操作。

## 備註風險
- [ ] SQLite 與 SQL Server 的型別、索引、預設值與交易行為可能不同，正式切換前需做資料庫相容性測試。