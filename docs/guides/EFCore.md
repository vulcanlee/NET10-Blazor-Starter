# 第一次 Migration 

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.4.42
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/08/26

> **前置說明**
>
> - 本專案**只支援 SQLite**，migration 一律放在單一組件 `MyProject.AccessDatas`，
>   不需要產生雙資料庫 migration（0.4.24 起已移除 SQL Server 軌道）。
> - 以下 `dotnet ef` 指令的相對路徑以 **`src/MyProject/`** 為工作目錄；
>   在 repo 根目錄執行請自行補上 `src/MyProject/` 前綴。

```
Add-Migration AddCategoryTeams -Project MyProject.AccessDatas -StartupProject MyProject.Web
Add-Migration AddCategoryTeamNameUniqueIndex -Project MyProject.AccessDatas -StartupProject MyProject.Web
```

* -Context <String>	

  The DbContext class to use. Class name only or fully qualified with namespaces. If this parameter is omitted, EF Core finds the context class. If there are multiple context classes, this parameter is required.
* -Project <String>	

  The target project. If this parameter is omitted, the Default project for Package Manager Console is used as the target project.
* -StartupProject <String>	

  The startup project. If this parameter is omitted, the Startup project in Solution properties is used as the target project.
* -Args <String>	

  Arguments passed to the application.
* -Verbose	

  Show verbose output.

# 更新資料庫

```
Update-Database -Context BackendDBContext -StartupProject MyProject.Web -Project MyProject.AccessDatas
```

# 套用 Migration 

```
Add-Migration AddCategoryTeams -Context BackendDBContext -Project MyProject.AccessDatas -StartupProject MyProject.Web 
```

```
dotnet ef migrations add AddCategoryTeams --project MyProject.AccessDatas --startup-project MyProject.Web 
```


# 移除 Migration

```
Remove-Migration -Context BackendDBContext -Project MyProject.AccessDatas -StartupProject MyProject.Web
```

```
dotnet ef migrations remove --project MyProject.AccessDatas --startup-project MyProject.Web
```

# 套用移轉

```
Script-Migration -Project MyProject.AccessDatas -StartupProject MyProject.Web
```

