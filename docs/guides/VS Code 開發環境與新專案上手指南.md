# VS Code 開發環境與新專案上手指南

- 文件版本：1.1
- 文件狀態：已實作
- 現行系統版本：0.9.2
- 首次實作版本：0.9.1
- 最後核對日期：2026/08/27

> 本文是「拿到這個腳手架之後怎麼開始」的單一入口，涵蓋 **VS Code 環境啟動 → 機密設定檔（User Secrets）→ 品牌客製化 → 複製成新專案並更名 → 驗證**全程。
>
> 動手改既有程式前，請另讀 [開發慣例與限制速查](../architecture/開發慣例與限制速查.md)（分層、Migration、權限同步等不變量）。

---

## 目錄

1. [適用對象與前置條件](#1-適用對象與前置條件)
2. [必備工具與 VS Code 擴充套件](#2-必備工具與-vs-code-擴充套件)
3. [取得原始碼與第一次建置](#3-取得原始碼與第一次建置)
4. [在 VS Code 啟動與偵錯](#4-在-vs-code-啟動與偵錯)
5. [機密設定檔（User Secrets）](#5-機密設定檔user-secrets)
6. [資料庫、Migration 與預設帳號](#6-資料庫migration-與預設帳號)
7. [品牌客製化：圖示、圖片、產品名稱與說明](#7-品牌客製化圖示圖片產品名稱與說明)
8. [從腳手架複製出新專案並更名](#8-從腳手架複製出新專案並更名)
9. [更名後驗證清單](#9-更名後驗證清單)
10. [常見疑難排解](#10-常見疑難排解)
11. [相關文件](#11-相關文件)

---

## 1. 適用對象與前置條件

| 你是 | 該讀哪幾章 |
|------|------------|
| 第一次 clone 本 repo，想把它跑起來 | 2 → 3 → 4 → 6 |
| 想把機密金鑰從 `appsettings.json` 搬走 | 5 |
| 要換掉圖示、品牌圖片、產品名稱與說明 | 7 |
| 要用本腳手架開一個**新系統** | 全部，重點在 7 → 8 |

前置知識：基本的 .NET CLI 與 Git 操作。作業系統以 **Windows 10 / 11** 為準（外部目錄預設值 `C:\temp\...` 為 Windows 路徑）。

---

## 2. 必備工具與 VS Code 擴充套件

### 2.1 必裝工具

| 工具 | 版本 / 說明 | 驗證指令 |
|------|-------------|----------|
| .NET SDK | **10.0.400**（`global.json` 已鎖定，`rollForward: latestFeature`，故 10.0.4xx 以上可用） | `dotnet --info` |
| PowerShell 7（`pwsh`）| `scripts/*.ps1` 與 CI 都以 pwsh 執行；**Windows 內建的 Windows PowerShell 5.1 不算** | `pwsh --version` |
| Git | 任意近期版本 | `git --version` |
| Visual Studio Code | 任意近期版本 | — |

> ⚠️ `global.json` 鎖定 SDK 版本。若 `dotnet --info` 找不到 10.0.4xx，所有 `dotnet` 指令都會直接失敗，請先到 [.NET 下載頁](https://dotnet.microsoft.com/download) 安裝對應 SDK。

### 2.2 VS Code 擴充套件

repo 已提供 `.vscode/extensions.json`，開啟工作區時 VS Code 會主動建議安裝：

| 擴充套件 ID | 用途 |
|-------------|------|
| `ms-dotnettools.csdevkit` | C# Dev Kit —— 方案總管、測試總管、`.slnx` 支援 |
| `ms-dotnettools.csharp` | C# 語言服務與偵錯器（`coreclr`） |
| `ms-dotnettools.vscode-dotnet-runtime` | 前兩者的相依 runtime 管理 |
| `ms-vscode.powershell` | 編輯與偵錯 `scripts/*.ps1` |
| `editorconfig.editorconfig` | 讓編輯器遵守 `.editorconfig`（CI 會以 `dotnet format` 強制） |

### 2.3 選用工具

```powershell
# EF Core 指令列工具（要自行新增 Migration 時才需要）
dotnet tool install --global dotnet-ef
dotnet ef --version

# 信任本機開發用 HTTPS 憑證（第一次跑 https profile 前務必執行）
dotnet dev-certs https --trust
```

Migration 指令細節見 [EFCore 指令備忘](EFCore.md)，本文不重複。

---

## 3. 取得原始碼與第一次建置

```powershell
git clone <repo-url> NET10-Blazor-Starter
cd NET10-Blazor-Starter

# 方案檔位於 src/MyProject/，格式為新版 .slnx（本 repo 沒有 .sln）
dotnet restore src/MyProject/MyProject.slnx
dotnet build   src/MyProject/MyProject.slnx -v:minimal

# 第一次執行（會自動建立外部目錄、套用 Migration、Seed 預設角色與帳號）
dotnet run --project src/MyProject/MyProject.Web/MyProject.Web.csproj --launch-profile https
```

### 3.1 第一次啟動會自動產生的東西

| 路徑 | 內容 | 來源設定 |
|------|------|----------|
| `C:\temp\MyProject\DB\BackendDB.db` | SQLite 資料庫 | `SystemSettings:ExternalFileSystem:DatabasePath` |
| `C:\temp\MyProject\Download` / `Upload` / `ProjectFile` | 檔案上傳與下載目錄 | 同上區段的另外三個鍵 |
| `C:\temp\Logs\MyProject.Web\` | NLog 日誌 | `NLog:BasePath` + 組件命名空間 |

這些目錄若不存在，`Program.cs` 會在啟動時自動建立。

### 3.2 建置注意事項

> ⚠️ `src/MyProject/Directory.Build.props` 開啟了 `TreatWarningsAsErrors`（唯一豁免是 `NU1900`）。**任何編譯警告都會讓建置失敗**，這是刻意的品質關卡，不要用 `#pragma warning disable` 繞過。

> ⚠️ 套件版本一律寫在 `src/MyProject/Directory.Packages.props`（Central Package Management）。個別 `.csproj` 的 `<PackageReference>` **不可以**帶 `Version` 屬性。

---

## 4. 在 VS Code 啟動與偵錯

### 4.1 開啟工作區

用 **`File > Open Folder` 開 repo 根目錄**（`NET10-Blazor-Starter\`），**不要**只開 `src/MyProject`。原因是 `scripts/`（文件編碼檢查）與 `docs/` 都必須在工作區內，`.vscode/` 的任務才找得到它們。

### 4.2 repo 提供的 `.vscode` 設定

| 檔案 | 作用 |
|------|------|
| `launch.json` | F5 偵錯設定。使用 **`https` profile**、自動開啟瀏覽器、`ASPNETCORE_ENVIRONMENT=Development` |
| `tasks.json` | 對齊 CI 的四道品質關卡：`build` / `format-check` / `test` / `docs-encoding`，另有 `restore` |
| `settings.json` | Markdown 預設存成 **UTF-8 含 BOM**（對應本專案不變量）、隱藏 `bin`/`obj`、指定預設方案檔 |
| `extensions.json` | 建議安裝的擴充套件清單 |

### 4.3 常用操作

| 操作 | 快捷鍵 | 結果 |
|------|--------|------|
| 啟動偵錯 | `F5` | 建置後啟動，瀏覽器自動開到 `https://localhost:7044` |
| 不進偵錯器執行 | `Ctrl+F5` | 同上但不附加偵錯器，熱重載較順 |
| 建置 | `Ctrl+Shift+B` | 執行 `build` 任務 |
| 執行其他任務 | `Ctrl+Shift+P` → `Tasks: Run Task` | 選 `format-check` / `test` / `docs-encoding` |

### 4.4 ⚠️ 一定要用 `https` profile

`Program.cs:389` **無條件**呼叫 `app.UseHttpsRedirection()`：

```csharp
app.UseHttpsRedirection();
```

`launchSettings.json` 的兩個 profile：

| profile | applicationUrl |
|---------|----------------|
| `http` | `http://localhost:5189` |
| `https` | `https://localhost:7044;http://localhost:5189` |

只跑 `http` profile 時，程式會把請求導向 HTTPS，但根本沒有監聽 HTTPS 埠 → 瀏覽器顯示連線失敗或無限重導。**請一律使用 `https` profile**（`.vscode/launch.json` 已預設如此）。

---

## 5. 機密設定檔（User Secrets）

### 5.1 為什麼需要

`appsettings.json` 會進版控，任何寫在裡面的金鑰都等於公開。目前腳手架內的預設值是**刻意留下的假值**：

```json
"JwtSettings": {
  "SigningKey": "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars"
}
```

`Configuration/StartupSafetyValidator.cs:7,17-21` 把這個字串寫成常數當作絆索 —— 只要 `ASPNETCORE_ENVIRONMENT=Production` 而金鑰還是它，程式會**丟出例外直接中止啟動**，不會帶著假金鑰上線。

**User Secrets** 讓你把真實金鑰放在 repo 之外、又不必改任何程式碼。

### 5.2 運作原理與檔案位置

`src/MyProject/MyProject.Web/MyProject.Web.csproj:6` 宣告了一組識別碼：

```xml
<UserSecretsId>83f6d54f-9f34-4cd9-a626-d4c05c996e5d</UserSecretsId>
```

這組 Id 決定機密檔案的實體位置：

| 作業系統 | 路徑 |
|----------|------|
| Windows | `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` |
| Linux / macOS | `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json` |

以本 repo 現值為例，Windows 上就是：

```
C:\Users\<你的帳號>\AppData\Roaming\Microsoft\UserSecrets\83f6d54f-9f34-4cd9-a626-d4c05c996e5d\secrets.json
```

> ⚠️ **只在 Development 環境載入**。本專案使用 `WebApplication.CreateBuilder(args)`（`Program.cs:42`）的預設設定來源鏈，全案**沒有**任何明確的 `AddUserSecrets(...)` 呼叫 —— 也就是完全依賴預設行為：`ASPNETCORE_ENVIRONMENT` 不是 `Development` 時，User Secrets **完全不會被讀取**。正式環境請改用環境變數或雲端 secret store，見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)。

> ✅ 這個檔案在 repo **之外**，不可能被 `git add` 進去，也不需要在 `.gitignore` 加任何規則。

### 5.3 建立方式 A：使用 CLI（建議）

```powershell
# 必須在 Web 專案的 csproj 目錄下執行（或加 --project 參數）
cd src/MyProject/MyProject.Web

# csproj 已有 UserSecretsId 時可略過；若沒有，這行會自動產生一組並寫回 csproj
dotnet user-secrets init

# 產生一把夠長的隨機金鑰（至少 32 字元）
$key = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
dotnet user-secrets set "JwtSettings:SigningKey" $key

# 確認寫入結果
dotnet user-secrets list
```

其他常用指令：

```powershell
dotnet user-secrets remove "JwtSettings:SigningKey"   # 移除單一鍵
dotnet user-secrets clear                             # 清空整份 secrets.json
```

### 5.4 建立方式 B：直接編輯 `secrets.json`

在 VS Code 裡直接編輯往往比一條條下指令快。先開啟目錄：

```powershell
# 目錄不存在時先建立
$dir = "$env:APPDATA\Microsoft\UserSecrets\83f6d54f-9f34-4cd9-a626-d4c05c996e5d"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
code "$dir\secrets.json"
```

`secrets.json` 完整範例（可直接貼上後改值）：

```json
{
  "JwtSettings": {
    "SigningKey": "把這裡換成你自己產生的 32 字元以上隨機字串"
  },
  "GoogleOAuthSettings": {
    "ClientId": "",
    "ClientSecret": ""
  },
  "CacheSettings": {
    "RedisConnection": ""
  },
  "AutoMapper:LicenseKey": ""
}
```

> **兩種寫法等價**：`dotnet user-secrets set "JwtSettings:SigningKey" "..."` 存進檔案後就是上面的巢狀結構。而 `"AutoMapper:LicenseKey"` 之所以維持扁平的冒號鍵，是因為 `appsettings.json:67` 本來就是這樣宣告、`Program.cs:221` 也以 `builder.Configuration["AutoMapper:LicenseKey"]` 讀取；兩種格式 .NET 都認得。

### 5.5 該搬哪些鍵

| 鍵路徑 | 目前值 | 必要性 | 不設定會怎樣 |
|--------|--------|--------|--------------|
| `JwtSettings:SigningKey` | `DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars` | **必要** | 開發環境可跑，但 Production 啟動直接中止（`StartupSafetyValidator.cs:18-21`）；且此金鑰已公開於版控，任何人都能偽造 JWT |
| `GoogleOAuthSettings:ClientId` | `""` | 啟用 Google 登入時必要 | `Enabled` 設 true 但憑證留空會登入失敗。設定細節見 [Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md) |
| `GoogleOAuthSettings:ClientSecret` | `""` | 同上 | 同上。**這是真正的密鑰，絕不可進版控** |
| `CacheSettings:RedisConnection` | `""` | `CacheSettings:Provider` 改成 `Redis` 時必要 | 連線字串通常含密碼；Production 下留空會被 `StartupSafetyValidator.cs:34-39` 擋下 |
| `AutoMapper:LicenseKey` | `""` | 商業授權情境 | 留空不影響開發（`Program.cs:221`），但授權金鑰不應進版控 |

> ⚠️ `BootstrapSettings:SupportPassword`（預設管理者密碼）**不建議**放進 User Secrets —— 它的行為與一般機密不同，改動會反向覆寫資料庫。詳見 [§6.4](#64-預設管理者帳號)。

### 5.6 驗證有沒有生效

```powershell
cd src/MyProject/MyProject.Web
dotnet user-secrets list
# 應輸出：JwtSettings:SigningKey = <你的值>
```

設定來源的**覆寫優先序**（後者蓋前者）：

```
appsettings.json
  → appsettings.{Environment}.json
    → User Secrets（僅 Development）
      → 環境變數
        → 命令列參數
```

### 5.7 常見誤區

| 誤區 | 說明 |
|------|------|
| 改了 `UserSecretsId` 卻沒重建 | Id 是編譯期嵌入組件的，改完 `.csproj` 必須重新 `dotnet build` 才會指向新目錄 |
| 在 repo 根目錄下 `dotnet user-secrets` | 會找不到專案。請先 `cd` 到 `MyProject.Web`，或加 `--project src/MyProject/MyProject.Web/MyProject.Web.csproj` |
| 以為正式環境也會讀 | 不會。非 Development 環境完全不載入 User Secrets |
| 以為 `secrets.json` 在專案資料夾裡 | 不在。它在 `%APPDATA%` 下，故意放在 repo 之外 |
| 多個專案共用同一個 `UserSecretsId` | 機密會互相污染。從腳手架複製新專案時務必換掉，見 [§8.2 步驟 5](#82-手動更名步驟) |

---

## 6. 資料庫、Migration 與預設帳號

### 6.1 資料庫檔案位置

本系統**只支援 SQLite**。連線字串由 `MyProject.Share/Helpers/MagicObjectHelper.cs:6-11` 組出：

```csharp
public const string SQLiteDatabaseFilename = "BackendDB.db";
public static string GetSQLiteConnectionString(string databasePath)
{
    return $"Data Source={Path.Combine(databasePath, SQLiteDatabaseFilename)}";
}
```

目錄部分來自 `appsettings.json:61` 的 `SystemSettings:ExternalFileSystem:DatabasePath`，所以開發環境的實際檔案是 **`C:\temp\MyProject\DB\BackendDB.db`**。

> ⚠️ **陷阱**：`appsettings.json:52-54` 還有一個 `SystemSettings:ConnectionStrings:SQLiteDefaultConnection`（值為 `Data Source=BackendDB.db`）。這是**死設定，執行期完全不會被讀取**，改它沒有任何作用。要換資料庫位置請改 `ExternalFileSystem:DatabasePath`。

### 6.2 啟動時自動套用 Migration

`Program.cs:267-282`：若專案內有 Migration 就呼叫 `Database.Migrate()`，否則退回 `Database.EnsureCreated()`。本 repo 的 `MyProject.AccessDatas/Migrations/` 有完整 Migration，所以走的是 `Migrate()` 這條路 —— 你不需要手動執行 `dotnet ef database update`。

### 6.3 重建資料庫

```powershell
# 1. 停掉正在執行的程式（重要，SQLite 檔案會被鎖住）
# 2. 刪除資料庫檔
Remove-Item "C:\temp\MyProject\DB\BackendDB.db" -Force
# 3. 重新執行，會重新套用 Migration 並 Seed
```

新增 Migration 的指令見 [EFCore 指令備忘](EFCore.md)。

### 6.4 預設管理者帳號

| 項目 | 值 | 來源 |
|------|----|------|
| 帳號 | `support` | `appsettings.json:40` `BootstrapSettings:SupportAccount` |
| 密碼 | `support` | `appsettings.json:43` `BootstrapSettings:SupportPassword` |
| 權限 | `IsAdmin = true` | `Program.cs:313-352` 強制設定 |

> ⚠️ **重要行為，不是 bug**：每次啟動時，若資料庫內 `support` 的密碼雜湊**驗不過設定檔的值**，程式會把密碼**覆寫回設定檔的值**，並強制 `IsAdmin = true`（`Program.cs:337-345`）。
>
> 這造成兩個容易誤解的現象：
> 1. 在 UI 改了 `support` 的密碼，重啟後又變回 `support`。
> 2. 把 `SupportPassword` 改成新值後，下次啟動會**靜默重設**資料庫裡的密碼。
>
> 這是刻意的「救援後門」設計，讓你永遠有辦法登入。正式上線前的處理方式見 [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md)。

`MagicObjectHelper.cs:17` 的 `開發者帳號 = "support"` 用於後續識別並保護這個帳號（例如不允許在 UI 刪除）。

---

## 7. 品牌客製化：圖示、圖片、產品名稱與說明

把腳手架變成「你的產品」，需要動的是**四樣東西**。本章交代每一樣改哪個檔、規格是什麼、使用者會在哪裡看到。

### 7.1 一次看懂：改哪裡、會在哪裡看到

| 要改的東西 | 檔案 / 設定鍵 | 使用者會在哪看到 |
|------|------|------|
| 網頁圖示 | `wwwroot/favicon.png` | 瀏覽器分頁與書籤（`Components/App.razor:15`） |
| 產品代表圖片 | `wwwroot/images/brand-logo.png` | 啟動頁（`SplashView.razor:5-7`）、登入頁品牌面板（`Login.razor:14`） |
| 產品名稱 | `SystemSettings:SystemInformation:SystemName` | 啟動頁大標、登入頁大標、「關於」對話窗 |
| 產品簡短說明 | `SystemSettings:SystemInformation:SystemDescription` | 啟動頁副說明、登入頁副說明、「關於」對話窗 |
| 版本號 | `SystemSettings:SystemInformation:SystemVersion` | 「關於」對話窗、**「系統健康監控」頁**（`/system-health`）的診斷文字 |
| 側邊欄品牌圖示 | `Components/Layout/NavMenu.razor:14` 的 `MaterialIcon Kind="dashboard_customize"` | 側邊欄左上（**不是** `brand-logo.png`，是字型圖示） |

> **沿革**：`0.9.2` 之前，登入頁與啟動頁的兩段說明文字是**寫死在 `.razor` 裡**的，`SystemDescription` 只影響「關於」對話窗 —— 改設定檔那兩頁不會變。0.9.2 起兩頁都改讀設定，`SystemName` 與 `SystemDescription` 都是單一來源。

### 7.2 更換產品代表圖片 `brand-logo.png`

現況規格：**1024×1024 PNG**（約 171 KB），滿版構圖、無透明邊。

兩處都用 `object-fit: cover` 填滿容器，**會裁成正方形**，所以主體務必置中、四周留安全邊距：

| 位置 | 容器（CSS） | 實際顯示尺寸 |
|------|------|------|
| 登入頁 | `.brand-logo`（`Login.razor.css:99`），圓角 30px | **108 × 108** |
| 啟動頁 | `.splash-brand-image-wrap`（`SplashView.razor.css:29`），圓角 24px | **120 × 120**（螢幕寬 ≤640.98px 時為 96 × 96） |

換圖建議：

- 正方形、**至少 512×512**（現況 1024×1024 是為了高 DPI 螢幕）。
- 兩頁背景都是**淺色**（啟動頁淺藍漸層卡片、登入頁淺色底），圖片本身若也是淺色會糊掉，建議用有對比的深色或彩色主體。
- **檔名不要改** —— 兩處 `.razor` 都硬編這個路徑。
- 0.9.2 起兩處改用 `@Assets["images/brand-logo.png"]`，換檔後 URL 會自動帶上新的 fingerprint（形如 `images/brand-logo.854k3jgc1o.png`），**不需要清瀏覽器快取**。

### 7.3 更換網頁圖示 `favicon.png`

現況規格：**64×64 PNG**（約 4.4 KB）。

> ⚠️ 本專案**沒有 `favicon.ico`**，也沒有 apple-touch-icon、`site.webmanifest` 或 PWA 圖示 —— 這是刻意的（Blazor Server，不是 PWA 範本）。整份專案只有 `Components/App.razor:15` 一處宣告圖示。

由 `brand-logo.png` 產生（沿用 0.4.25 的原始作法）：

```bash
ffmpeg -i images/brand-logo.png \
  -vf "crop='min(iw,ih)':'min(iw,ih)',scale=64:64:flags=lanczos" \
  -pix_fmt rgba -y favicon.png
```

沒有 ffmpeg 就用任何影像工具置中裁成正方形、縮到 64×64、存成 PNG 即可。

> ⚠️ **不要把 `@Assets["favicon.png"]` 改回裸相對路徑**（例如 `href="favicon.png"`）。沒有 fingerprint 的話，你覆蓋了檔案但 URL 沒變，使用者會一直看到快取中的舊圖示。

換完看不到新圖示？先 `Ctrl+F5` 強制重新整理 —— favicon 的瀏覽器快取特別黏，必要時到瀏覽器設定清除該站台資料。

### 7.4 更換產品名稱與簡短說明

0.9.2 起兩者都是單一來源，**只改 `appsettings.json` 一處**，啟動頁、登入頁與「關於」對話窗三處同步生效：

```json
"SystemSettings": {
  "SystemInformation": {
    "SystemName": "你的系統名稱",
    "SystemDescription": "一句話說明這個系統在做什麼",
    "SystemVersion": "0.0.1 (2026/01/01)"
  }
}
```

版面提示：

| 欄位 | 建議長度 | 說明 |
|------|------|------|
| `SystemName` | 4–10 字 | 兩頁都是最大的標題字（啟動頁 `.splash-title`、登入頁 `.app-title`），過長會換行擠壓版面 |
| `SystemDescription` | 30–40 字 | 啟動頁單行可容納約 40 字；登入頁面板較窄會折成兩行。超過約 60 字會開始破版 |
| `SystemVersion` | — | 格式固定為 `Major.Minor.Patch (YYYY/MM/DD)`；**留空**會讓「系統健康監控」頁把應用程式判為 Degraded 並顯示「SystemVersion 未設定。」 |

### 7.5 ⚠️ 瀏覽器分頁標題是另一件事

`Components/App.razor` **沒有全域 `<title>`**，瀏覽器分頁標題完全由各頁自己的 `<PageTitle>` 決定 —— **`SystemName` 不參與**。沒有宣告 `<PageTitle>` 的頁面會顯示空白標題。

全專案 17 個 `<PageTitle>` 中有三處是範本殘留的英文（其餘 14 個都已中文化）：

| 檔案 | 目前標題 |
|------|------|
| `Components/Pages/Home.razor:4` | `Home` ← **這就是啟動頁（網站根路徑 `/`）的分頁標題** |
| `Components/Pages/HomeAuthed.razor:3` | `Home` |
| `Components/Pages/Error.razor:5` | `Error` |

想讓每個分頁都帶產品名（例如「使用者管理 — 你的系統」），需要自行加後綴機制，本專案未提供。

### 7.6 仍然寫死、需要自行決定的文案

以下都是**設計文案**，刻意不參數化。要不要改由你決定，但更名時建議至少掃過一遍：

| 位置 | 目前文字 |
|------|------|
| `Components/Auths/Login.razor:12` | `ENTERPRISE ACCESS`（品牌徽章） |
| `Components/Auths/Login.razor:22` | `Welcome Back` |
| `Components/Auths/Login.razor:23` / `:24` | `使用者登入` / `請輸入您的帳號資訊以存取系統。` |
| `Components/Auths/Login.razor:58` | `企業級安全登入` |
| `Components/Views/Commons/SplashView.razor:11` | `Welcome` |
| `Components/Views/Commons/SplashView.razor:19` | `系統載入中，正在為你準備工作環境...` |
| `Components/Layout/NavMenu.razor:17` | `MyProject.Web` —— ⚠️ 側邊欄品牌文字，**不走 `SystemName`**。更名腳本會把它換成 `新代號.Web`，當作產品名仍然不對，請自行改成正式名稱 |
| `Components/Layout/NavMenu.razor:18` / `:25` | `管理後台功能清單` / `功能選單` |
| `Components/Layout/MainLayout.razor.cs:52` | `系統首頁`（頂列標題的 fallback，**不是**瀏覽器分頁標題） |
| `Components/Commons/ViewNotification.cs:15` | `系統訊息`（全站通知的標題） |
| `MainLayout.razor:103`、`EmptyLayout.razor:12`、`NoFooterLayout.razor:10` | `An unhandled error has occurred.`（Blazor 預設，尚未中文化） |

另註：全專案**沒有** footer 或版權文字，`.csproj` 也沒有設定 `<Product>` / `<AssemblyTitle>`，所以組件層級沒有產品名要改。

### 7.7 換完怎麼確認

啟動系統後逐項檢查：

- [ ] 啟動頁（`https://localhost:7044/`）：品牌圖片、大標題（`SystemName`）、副說明（`SystemDescription`）
      —— 啟動頁只在驗證身分那一瞬間出現，會很快跳走，可先登出再開首頁觀察
- [ ] 登入頁（`/Auths/Login`）：同樣三項，且說明文字沒有溢出面板
- [ ] 登入後右上使用者選單 →「關於」：系統名稱／系統描述／系統版本三列正確
- [ ] 「系統健康監控」頁（`/system-health`）：診斷文字含正確的 `版本：x.y.z`
- [ ] 瀏覽器分頁圖示已換（先 `Ctrl+F5`）
- [ ] 檢視網頁原始碼，確認 `brand-logo` 的 URL 帶了 fingerprint（形如 `images/brand-logo.<雜湊>.png`）

---

## 8. 從腳手架複製出新專案並更名

目標：把 `MyProject` 這個代號整套換成你的新系統代號（以下以 **`Acme.Erp`** 為例），包含**資料夾名稱、檔案名稱、命名空間、組件名稱、設定值與文件**。

### 8.1 快速捷徑：`New-StarterProject.ps1`

repo 已附一支腳本能一次做掉絕大部分工作：

```powershell
pwsh ./scripts/New-StarterProject.ps1 `
     -ProjectName Acme.Erp `
     -DestinationPath D:\Work\Acme.Erp
```

**腳本會做的事：**

- 複製整個 repo 到目標路徑，跳過 `.git` / `bin` / `obj` / `.vs` / `.playwright-cli` / `output`
- 把文字檔（`.cs .csproj .slnx .json .md .razor .css .js .ps1 .yml .yaml .config .xml`）內的 `MyProject` 全部換成新代號，**逐檔保留原本的 BOM 狀態**（`docs/*.md` 的 BOM 不會被抹掉）
- 由深到淺改資料夾名，再改檔名
- **產生一組新的 `UserSecretsId`** 寫入 Web 專案 csproj，並印在畫面上
- 把 `JwtSettings:SigningKey` 換成 `<新代號>-ChangeThisJwtSigningKey-AtLeast32Chars`、`SupportPassword` 換成 `change-me`
- 最後掃一次殘留字串並警告

**腳本做完之後你仍須手動處理：**

| 項目 | 說明 |
|------|------|
| 品牌圖檔 | `wwwroot/images/brand-logo.png`、`wwwroot/favicon.png` 是二進位檔，腳本不會動 —— 規格與作法見 [§7 品牌客製化](#7-品牌客製化圖示圖片產品名稱與說明) |
| 產品名稱與說明 | `appsettings.json` 的 `SystemSettings:SystemInformation`（`SystemName` / `SystemDescription` / `SystemVersion`）—— 見 [§7.4](#74-更換產品名稱與簡短說明) |
| 文件內文 | `docs/**` 與 `readme.md` 裡描述舊系統的敘述句（不只是代號） |
| 外部目錄路徑 | `ExternalFileSystem` 四個路徑會被換成 `C:\temp\Acme.Erp\...`，確認是你要的位置 |
| 機密 | 用新的 `UserSecretsId` 重新設定一次 User Secrets（見 [§5](#5-機密設定檔user-secrets)） |

即使用了腳本，仍**強烈建議**照 [§8.3 高風險清單](#83-高風險清單) 逐條核對，再跑 [§9 驗證清單](#9-更名後驗證清單)。

### 8.2 手動更名步驟

想完全掌控過程、或腳本在你的環境出狀況時，照以下步驟做。

#### 步驟 1：複製並清乾淨

複製整個目錄後刪除這些（它們都是建置產物或本機狀態，留著會干擾後續的全域取代）：

```
.git/                                  ← 要開新的版控歷史
.playwright-cli/                       ← UI 測試快照
output/                                ← 產生器輸出
src/MyProject/.vs/                     ← Visual Studio 本機狀態
src/MyProject/**/bin/  **/obj/         ← 所有建置產物
src/MyProject/MyProject.AccessDatas.SqlServerMigrations/   ← 殘骸目錄（只剩 bin/obj，沒有 csproj，也不在方案內）
```

#### 步驟 2：改資料夾名稱（由深到淺）

先改內層再改外層，否則路徑會失效：

| 順序 | 舊名 | 新名 |
|------|------|------|
| 1 | `src/MyProject/MyProject.Share` | `src/MyProject/Acme.Erp.Share` |
| 2 | `src/MyProject/MyProject.Models` | `src/MyProject/Acme.Erp.Models` |
| 3 | `src/MyProject/MyProject.Dtos` | `src/MyProject/Acme.Erp.Dtos` |
| 4 | `src/MyProject/MyProject.AccessDatas` | `src/MyProject/Acme.Erp.AccessDatas` |
| 5 | `src/MyProject/MyProject.Business` | `src/MyProject/Acme.Erp.Business` |
| 6 | `src/MyProject/MyProject.Web` | `src/MyProject/Acme.Erp.Web` |
| 7 | `src/MyProject/MyProject.Tests` | `src/MyProject/Acme.Erp.Tests` |
| 8 | `src/MyProject` | `src/Acme.Erp` |

#### 步驟 3：改檔案名稱

6 個 `.csproj` 加 1 個方案檔：

```
Acme.Erp.Share/MyProject.Share.csproj             → Acme.Erp.Share.csproj
Acme.Erp.Models/MyProject.Models.csproj           → Acme.Erp.Models.csproj
Acme.Erp.Dtos/MyProject.Dtos.csproj               → Acme.Erp.Dtos.csproj
Acme.Erp.AccessDatas/MyProject.AccessDatas.csproj → Acme.Erp.AccessDatas.csproj
Acme.Erp.Business/MyProject.Business.csproj       → Acme.Erp.Business.csproj
Acme.Erp.Web/MyProject.Web.csproj                 → Acme.Erp.Web.csproj
Acme.Erp.Tests/MyProject.Tests.csproj             → Acme.Erp.Tests.csproj
MyProject.slnx                                    → Acme.Erp.slnx
```

> **不需要**設定 `<AssemblyName>` 或 `<RootNamespace>` —— 本 repo 全部專案都沒有明確宣告這兩個屬性，組件名稱與根命名空間都隱含取自檔名。**改了 csproj 檔名，兩者就一起改好了。**

#### 步驟 4：全域文字取代

在 VS Code 按 `Ctrl+Shift+H`：

| 欄位 | 值 |
|------|-----|
| Search | `MyProject` |
| Replace | `Acme.Erp` |
| files to include | （留空 = 整個工作區） |
| files to exclude | `**/bin,**/obj,**/.vs,**/.git,**/.playwright-cli` |

按 `Alt+C` 開啟**大小寫相符**（Match Case），避免誤傷。取代前先看一次結果清單。

> 這一步會處理掉大部分內容：命名空間、`using`、`_Imports.razor`（11 行 `@using`）、`.csproj` 的 `ProjectReference`、`.slnx` 的專案路徑、CI workflow、`appsettings.json`、`docs/**`、`scripts/New-CrudModule.ps1`（範本內嵌 48 處）、`CLAUDE.md` / `AGENTS.md` / `.github/copilot-instructions.md`。

#### 步驟 5：換掉 `UserSecretsId`

```powershell
[guid]::NewGuid().ToString()
# 例如輸出 a1b2c3d4-5e6f-7890-abcd-ef1234567890
```

把新值填進 `src/Acme.Erp/Acme.Erp.Web/Acme.Erp.Web.csproj`：

```xml
<UserSecretsId>a1b2c3d4-5e6f-7890-abcd-ef1234567890</UserSecretsId>
```

> ⚠️ **不換的後果**：所有從這個腳手架複製出來的專案會共用同一份 `secrets.json`。A 專案設定的 Redis 連線字串會出現在 B 專案；在 A 專案 `dotnet user-secrets clear` 會把 B 專案的機密一起清掉。這種污染很難察覺，務必換掉。
>
> 換完記得 `dotnet build`（Id 是編譯期嵌入的），再依 [§5.3](#53-建立方式-a使用-cli建議) 重新設定機密。

#### 步驟 6：逐條核對高風險清單

見 [§8.3](#83-高風險清單)。步驟 4 的全域取代通常已經蓋掉這些，但**務必逐條確認**，因為漏掉其中幾條不會有任何錯誤訊息。

#### 步驟 7：設定值與品牌

`src/Acme.Erp/Acme.Erp.Web/appsettings.json`：

| 鍵 | 建議值 |
|----|--------|
| `SystemSettings:SystemInformation:*` | 產品名稱、簡短說明與版本 —— 完整說明見 [§7.4](#74-更換產品名稱與簡短說明)；版本建議歸零為 `0.0.1 (2026/01/01)` |
| `SystemSettings:ExternalFileSystem:*` | 四個路徑，確認 `C:\temp\Acme.Erp\...` 是你要的 |
| `JwtSettings:Issuer` / `Audience` | `Acme.Erp` / `Acme.Erp.WebApi` |
| `JwtSettings:SigningKey` | 換成你自己的值；建議直接搬進 User Secrets |
| `BootstrapSettings:*` | 改掉預設的 `support` / `support` |
| `CacheSettings:InstanceName` | `Acme.Erp:`（Redis 鍵前綴，避免與其他系統衝突） |

品牌圖檔（二進位，全域取代不會處理）—— 尺寸規格、裁切行為與 favicon 產生指令見 **[§7 品牌客製化](#7-品牌客製化圖示圖片產品名稱與說明)**：

```
src/Acme.Erp/Acme.Erp.Web/wwwroot/images/brand-logo.png   ← 登入頁與啟動畫面
src/Acme.Erp/Acme.Erp.Web/wwwroot/favicon.png             ← 瀏覽器分頁圖示
```

#### 步驟 8：文件與 CI

| 檔案 | 要改什麼 |
|------|----------|
| `.github/workflows/dotnet-ci.yml` | 6 處硬編路徑（`src/MyProject/...`、`MyProject.slnx`），步驟 4 應已處理，確認一次 |
| `readme.md` | 系統介紹、架構圖、專案結構樹、快速開始指令 |
| `docs/**` | 內文敘述（不只代號，還有描述舊系統功能的句子）；`docs/changelog/` 建議整個清空重來 |
| `CLAUDE.md` / `AGENTS.md` / `.github/copilot-instructions.md` | LLM 協作準則裡的專案描述 |
| `.vscode/launch.json` / `tasks.json` / `settings.json` | 內含 `src/MyProject/...` 路徑，確認已更新 |

### 8.3 高風險清單

以下是**改資料夾名稱不會自動修好**、且多數**漏改也不會報錯**的字串。請逐條核對：

| 位置 | 內容 | 漏改的後果 |
|------|------|------------|
| `Components/App.razor:13` | `@Assets["MyProject.Web.styles.css"]` | ⚠️ **最危險**：Blazor scoped CSS 套件名取自組件名。漏改會讓**全站 scoped CSS 完全不載入**，畫面嚴重跑版，但**不會有任何錯誤訊息** |
| `MyProject.Web.csproj:30` | `<InternalsVisibleTo Include="MyProject.Tests" />` | 慣例守門測試無法編譯（會報錯，容易發現） |
| `Program.cs:47` | `typeof(Program).Namespace ?? nameof(MyProject.Web)` | 決定 NLog 目錄與檔名前綴。漏改會讓日誌寫到舊名目錄，系統內的「日誌檢視」頁讀不到任何資料 |
| `Program.cs:97` | Swagger `Title = "MyProject API"` | Swagger UI 殘留舊系統名 |
| `Extensions/ApplicationBuilderExtensions.cs:28` | `SwaggerEndpoint(..., "MyProject API v1")` | 同上 |
| `Program.cs:200` | `options.Cookie.Name = ".MyProject.External"` | 外部登入（OAuth）暫存 Cookie 名稱殘留舊名；同機多系統時可能互撞 |
| `Components/Layout/NavMenu.razor:17` | `<a class="navbar-brand" href="">MyProject.Web</a>` | ⚠️ 側邊欄品牌文字是**硬編的，沒有走 `SystemName` 設定** —— 改了 `appsettings.json` 也不會變 |
| `Configuration/CacheSettings.cs:9` | `InstanceName { get; set; } = "MyProject:"` | Redis 鍵前綴的程式預設值。共用 Redis 時會與其他系統鍵值衝突 |
| `Components/Views/Analytics/LogViewerView.razor.cs:151` | 下載檔名 `MyProject.Web-logs-{時間}.log` | 使用者下載的日誌檔名殘留舊名 |
| `AccessDatas/Migrations/*.Designer.cs`、`BackendDBContextModelSnapshot.cs` | 數百處 `modelBuilder.Entity("MyProject.AccessDatas.Models.X", ...)` 字串常值 | Model snapshot 與實際模型不符，EF Core 會誤判「有尚未產生的 Migration」 |
| `wwwroot/images/brand-logo.png`、`wwwroot/favicon.png` | 二進位圖檔，全域文字取代**完全不會處理** | 新系統掛著舊產品的圖示與品牌圖片。作法見 [§7 品牌客製化](#7-品牌客製化圖示圖片產品名稱與說明) |
| `Components/Layout/NavMenu.razor:17` 的產品名 | 側邊欄品牌文字，更名腳本只會換成 `新代號.Web` | 側邊欄顯示的是專案代號而非正式產品名，見 [§7.6](#76-仍然寫死需要自行決定的文案) |
| `MyProject.Tests/*.cs` | 多支守門測試以字串或路徑比對專案名：`LoggingConventionTests`、`ButtonIconConventionTests`、`MenuIconTests`、`MenuPermissionConsistencyTests`、`LogLevelRuntimeStateTests`、`LogQueryServiceTests`、`SystemHealthTests`、`ApiIntegrationTests`、`TotpServiceTests` | 測試失敗。**這其實是好事** —— 它們是漏改的偵測網，所以 [§9](#9-更名後驗證清單) 一定要跑 `dotnet test` |
| `.vscode/launch.json`、`tasks.json`、`settings.json` | `src/MyProject/...` 路徑與 `MyProject.Web.dll` | F5 啟動失敗、任務找不到方案檔 |

### 8.4 不需要改的東西

避免過度改名造成不必要的風險：

| 項目 | 為什麼不用改 |
|------|--------------|
| `BackendDBContext` 類別名 | 本來就沒有品牌字，只是 DbContext 的名字 |
| `Migrations/` 的檔名（`*_InitialCreate.cs` 等） | 檔名是 EF 的時間戳記慣例，與專案名無關 |
| `launchSettings.json` 的 `http` / `https` profile 名稱 | 兩個 profile 名都是通用字，不含品牌 |
| `Directory.Build.props` / `Directory.Packages.props` / `global.json` / `.editorconfig` / `.gitignore` | 經確認**都不含** `MyProject` 字串 |
| `Datas/Menu.json` | 選單定義檔不含專案名 |
| `MagicObjectHelper` 的常數值 | `開發者帳號`、`預設角色`、`NeedChangePassword` 等都是業務語意，不是品牌 |

---

## 9. 更名後驗證清單

四道關卡（與 CI `.github/workflows/dotnet-ci.yml` 完全一致），全部在 repo 根目錄執行：

```powershell
# 1. 建置（TreatWarningsAsErrors，任何警告都會失敗）
dotnet build src/Acme.Erp/Acme.Erp.slnx -v:minimal --no-incremental

# 2. 格式（CI 會以 --verify-no-changes 檢查）
dotnet format src/Acme.Erp/Acme.Erp.slnx --verify-no-changes

# 3. 測試（守門測試會抓出漏改的專案名）
dotnet test src/Acme.Erp/Acme.Erp.slnx

# 4. 文件編碼（必須從 repo 根目錄執行，預設掃 ./docs）
pwsh ./scripts/Test-DocsEncoding.ps1
```

手動驗收項目：

- [ ] `dotnet run --launch-profile https` 能啟動，`https://localhost:7044` 開得起來
- [ ] **畫面樣式正常**（若整站沒有樣式，回頭查 `App.razor` 的 `styles.css` —— 見 [§8.3](#83-高風險清單)）
- [ ] 側邊欄品牌文字已是新系統名（`NavMenu.razor:17` 硬編字串）
- [ ] 能以 `BootstrapSettings` 設定的帳密登入
- [ ] Swagger UI（`/swagger`）標題正確，且能用 Bearer token 呼叫受保護 API
- [ ] `C:\temp\Acme.Erp\{DB,Download,Upload,ProjectFile}` 已正確產生
- [ ] `C:\temp\Logs\Acme.Erp.Web\` 有新日誌檔產生，且系統內的「日誌檢視」頁讀得到
- [ ] 全文搜尋（排除 `bin`/`obj`）已無任何 `MyProject` 殘留
- [ ] `git grep -i "DevelopmentOnly-ChangeThis"` 無殘留，或已改用 User Secrets
- [ ] `Acme.Erp.Web.csproj` 的 `UserSecretsId` 是新產生的 GUID

---

## 10. 常見疑難排解

| 症狀 | 原因 | 解法 |
|------|------|------|
| 瀏覽器連不上或無限重導 | 用了 `http` profile，但 `Program.cs:389` 無條件做 HTTPS 重導 | 改用 `https` profile（`.vscode/launch.json` 已預設） |
| 瀏覽器顯示憑證不受信任 | 尚未信任本機開發憑證 | `dotnet dev-certs https --trust` |
| `Address already in use` / 埠被占用 | 5189 或 7044 被其他程式占用 | 改 `launchSettings.json` 的 `applicationUrl`，或 `Get-NetTCPConnection -LocalPort 7044` 找出占用者 |
| **畫面完全沒有樣式** | `App.razor:13` 的 `MyProject.Web.styles.css` 沒跟著改名 | 改成 `<新代號>.Web.styles.css` |
| User Secrets 設了卻沒生效 | ①`ASPNETCORE_ENVIRONMENT` 不是 `Development` ②`UserSecretsId` 與實際目錄不符 ③改了 csproj 沒重建 | 逐項確認；`dotnet user-secrets list` 應讀得到值 |
| `dotnet user-secrets` 說找不到專案 | 不在 csproj 目錄下 | `cd src/.../*.Web`，或加 `--project <csproj 路徑>` |
| 建置因為一個小警告就失敗 | `Directory.Build.props` 的 `TreatWarningsAsErrors` | 修掉警告，別 disable。這是刻意的品質關卡 |
| CI 的 `dotnet format` 步驟失敗 | 格式不符 `.editorconfig` | 先跑一次不帶 `--verify-no-changes` 的 `dotnet format` 讓它自動修 |
| CI 掛在 `Test-DocsEncoding` | 某個 `docs/**/*.md` 缺 BOM 或含 U+FFFD 亂碼 | 以 UTF-8 **含 BOM** 重存。用 PowerShell 寫檔時必須 `-Encoding utf8BOM`，**`-Encoding utf8` 在 pwsh 7 不含 BOM** |
| `Test-DocsEncoding.ps1` 說找不到 docs | 不是在 repo 根目錄執行 | `-DocsPath` 預設相對於工作目錄，請 `cd` 到根目錄 |
| EF 說有尚未套用的模型變更 | Migration 的 `.Designer.cs` / snapshot 內的 `MyProject.AccessDatas.Models.*` 字串沒換 | 對 `Migrations/` 目錄再做一次全域取代 |
| 登入失敗，或密碼自己變回舊值 | `Program.cs:337-345` 的密碼救援覆寫行為 | 見 [§6.4](#64-預設管理者帳號)。要改密碼請同步改 `BootstrapSettings:SupportPassword` |
| 資料庫檔刪不掉 | 程式還在執行，SQLite 檔案被鎖 | 先停掉程式（VS Code 按 `Shift+F5`）再刪 |
| C# Dev Kit 找不到方案 | 工作區開錯層級 | 用 `File > Open Folder` 開 **repo 根目錄**；`.vscode/settings.json` 的 `dotnet.defaultSolution` 已指定方案檔 |

---

## 11. 相關文件

| 文件 | 內容 |
|------|------|
| [開發慣例與限制速查](../architecture/開發慣例與限制速查.md) | **改程式前必讀**：分層、Migration、權限同步等不變量 |
| [腳手架新專案啟動流程](腳手架新專案啟動流程.md) | 改名與 API/DTO 待辦的勾選式檢查清單 |
| [日誌與設定檔說明](../operations/日誌與設定檔說明.md) | `appsettings.json` 每個區段的完整說明 |
| [正式部署與安全檢查清單](../operations/正式部署與安全檢查清單.md) | 上線前必須處理的機密、帳號與 Swagger 設定 |
| [密碼種類與儲存機制](../security/密碼種類與儲存機制.md) | 各種密碼與金鑰分別存在哪、怎麼雜湊 |
| [Google OAuth2 第三方登入](../security/Google%20OAuth2%20第三方登入.md) | Google SSO 憑證申請與設定 |
| [EFCore 指令備忘](EFCore.md) | Migration 指令範本 |
| [測試指南](測試指南.md) | 測試分類、本機執行與覆蓋率 |
| [CI-CD 與品質檢查](../operations/CI-CD與品質檢查.md) | CI 流程與四個品質關卡設定檔 |
| [維護規範](../operations/維護規範.md) | 版本號、文件同步與 commit 前的檢查 |
| [建立一個新 CRUD 操作網頁說明](建立一個新%20CRUD%20操作網頁說明.md) | 新專案開好後，怎麼加第一個功能模組 |

> 返回 [文件總索引](../README.md)
