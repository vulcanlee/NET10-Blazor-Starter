# 第一次 Migration 

- 文件版本：1.0
- 文件狀態：已實作
- 現行系統版本：0.4.23
- 首次實作版本：—（未追溯，約 0.1.x 初始腳手架）
- 最後核對日期：2026/07/14

```
Add-Migration add-meeting -Project MyProject.AccessDatas -StartupProject MyProject.Web 
Add-Migration Add-Status -Project MyProject.AccessDatas -StartupProject MyProject.Web 
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
Add-Migration AddAthleteExamine -Context BackendDBContext -Project MyProject.AccessDatas -StartupProject MyProject.Web 
```

```
dotnet ef migrations add AddAthleteExamine --project MyProject.AccessDatas --startup-project MyProject.Web 
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

